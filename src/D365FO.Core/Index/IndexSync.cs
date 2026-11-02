using D365FO.Core.Extract;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Index;

/// <summary>
/// Re-index ONE model — the unit an edit actually lands in — rather than the whole
/// installation.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to "I changed that table in Visual Studio, the index is now lying about
/// it". The full extract walks every package for minutes, which is the wrong shape for a caller
/// waiting on an answer, so it stayed shell-only; the effect was that an agent's index went
/// stale after any edit it did not make itself and nothing could refresh it.
/// </para>
/// <para>
/// The unit is a model and not a file because <see cref="MetadataRepository.ApplyExtract(ExtractBatch)"/>
/// is model-scoped by design — re-extract replaces the model's rows, which is what keeps the
/// pipeline idempotent. Handing it a single object would not add that object; it would delete
/// every other object of that model. A custom model re-indexes in seconds; naming a large
/// standard model is a minutes-long call, and the result says how long it took.
/// </para>
/// </remarks>
public static class IndexSync
{
    /// <summary>
    /// Re-index the model named by <paramref name="model"/>, or the model that owns
    /// <paramref name="path"/>.
    /// </summary>
    /// <param name="model">Model to re-read. Wins over <paramref name="path"/> when both are given.</param>
    /// <param name="path">
    /// Any file inside the model — the model is read off the
    /// <c>&lt;root&gt;\&lt;Model&gt;\&lt;Model&gt;\Ax&lt;Kind&gt;\&lt;Name&gt;.xml</c> layout, which is
    /// the same convention the journal infers a model from.
    /// </param>
    /// <param name="packagesOverride">Packages root to look in first; the configured roots are searched after it.</param>
    /// <param name="databasePath">Index database to write to. Defaults to the configured one.</param>
    /// <param name="indexSource">
    /// Whether to full-text index the model's method bodies: <c>true</c> as
    /// <c>--index-source</c>, <c>false</c> as <c>--no-index-source</c>, <c>null</c> to keep
    /// whatever the model's last extract did (see <see cref="MetadataRepository.IsSourceIndexed"/>).
    /// </param>
    public static ToolResult<object> Sync(
        string? model, string? path, string? packagesOverride = null,
        string? databasePath = null, bool? indexSource = null)
    {
        var cfg = D365FoSettings.FromEnvironment(databasePath);

        var modelName = model;
        if (string.IsNullOrWhiteSpace(modelName) && !string.IsNullOrWhiteSpace(path))
        {
            modelName = ModelFromPath(path!);
            if (modelName is null)
                return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Could not tell which model '{path}' belongs to.",
                    "A path only names a model when it sits in the packages layout "
                    + "(<root>\\<Model>\\<Model>\\Ax<Kind>\\<Name>.xml). Pass the model name instead.");
        }

        if (string.IsNullOrWhiteSpace(modelName))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "A model (or a path inside one) is required.",
                "Re-indexing everything is `d365fo index refresh`, which walks every package and is "
                + "not something to wait on inside a tool call.");

        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(packagesOverride)) roots.Add(packagesOverride!);
        roots.AddRange(cfg.CustomPackagesPaths);
        if (!string.IsNullOrWhiteSpace(cfg.PackagesPath)) roots.Add(cfg.PackagesPath!);

        var modelRoot = FindModelDir(roots, modelName!);

        if (modelRoot is null)
            return ToolResult<object>.Fail("MODEL_NOT_FOUND",
                $"No model directory called '{modelName}' under any configured packages path.",
                roots.Count == 0
                    ? "Set D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots)."
                    : $"Looked in: {string.Join(", ", roots)}");

        // The directory match is case-insensitive, but Models.Name is not: keep the on-disk
        // spelling so `sync fleetmanagement` updates (and reads SourceIndexed of) FleetManagement.
        modelName = Path.GetFileName(modelRoot);

        var repo = new MetadataRepository(cfg.DatabasePath);
        repo.EnsureSchema();

        var matcher = new ModelMatcher(cfg.CustomModels);
        var captureSource = indexSource ?? repo.IsSourceIndexed(modelName!);
        var extractor = new MetadataExtractor { CaptureMethodSource = captureSource };

        var startedUtc = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Taken before the extract, not after: a file saved while the model is being read then
        // leaves the stored fingerprint behind the disk, and the next refresh picks it up. Taken
        // after, that edit would be stamped as already indexed.
        var fingerprint = ComputeFingerprint(modelRoot, cfg.LabelLanguages);

        ExtractBatch batch;
        try
        {
            batch = extractor.ExtractModel(modelRoot, modelName!, cfg.LabelLanguages, matcher.IsMatch(modelName!));
        }
        catch (Exception ex)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable,
                $"Could not read model '{modelName}' at {modelRoot}: {ex.Message}");
        }

        // Parsing needs no lock; replacing the model's rows does. Taken after the parse so a
        // sync waiting behind a refresh holds nothing while it reads XML.
        using (var writeLock = IndexWriteLock.TryAcquire(repo.DatabasePath))
        {
            if (writeLock is null) return IndexWriteLock.Busy(repo.DatabasePath);

            repo.ApplyExtract(batch, fingerprint, captureSource);
            sw.Stop();
            repo.RecordExtractionRun(batch.Model, startedUtc, sw.ElapsedMilliseconds,
                batch.Tables.Count, batch.Classes.Count, batch.Edts.Count,
                batch.Enums.Count, batch.Labels.Count, batch.IsCustom);
        }

        return ToolResult<object>.Success(new
        {
            model = batch.Model,
            modelRoot,
            isCustom = batch.IsCustom,
            elapsedMs = sw.ElapsedMilliseconds,
            sourceIndexed = repo.IsSourceIndexed(batch.Model),
            counts = new
            {
                tables = batch.Tables.Count,
                classes = batch.Classes.Count,
                edts = batch.Edts.Count,
                enums = batch.Enums.Count,
                forms = batch.Forms.Count,
                labels = batch.Labels.Count,
            },
            note = "Only this model was re-read. An object that moved to a different model needs that "
                 + "model synced too.",
        });
    }

    /// <summary>
    /// The tri-state a caller's <c>--index-source</c> / <c>--no-index-source</c> pair stands for:
    /// <c>true</c>, <c>false</c>, or <c>null</c> for "keep what the model had".
    /// </summary>
    public static bool? SourceChoice(bool indexSource, bool noIndexSource) =>
        indexSource ? true : noIndexSource ? false : null;

    /// <summary>The refusal for a caller that asked for both, or null when the pair is consistent.</summary>
    public static ToolResult<object>? ConflictingSourceFlags(bool indexSource, bool noIndexSource) =>
        indexSource && noIndexSource
            ? ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--index-source and --no-index-source contradict each other.",
                "Pass --index-source to full-text index method bodies, --no-index-source to drop them, "
                + "or neither to keep what each model's last extract did.")
            : null;

    /// <summary>The model a path belongs to, or null when the path is not in the packages layout.</summary>
    /// <remarks>
    /// Anchored, not searched. The layout puts the doubled segment at a fixed depth —
    /// <c>&lt;root&gt;/&lt;Model&gt;/&lt;Model&gt;/Ax&lt;Kind&gt;/&lt;Name&gt;.xml</c> — so a file's model is
    /// exactly two levels up, through an <c>Ax*</c> folder. Walking up looking for any repeated
    /// folder name finds one wherever a path happens to contain one: a CI checkout at
    /// <c>/work/&lt;repo&gt;/&lt;repo&gt;</c> answered with the repository's own name for a path that had
    /// nothing to do with D365FO.
    /// </remarks>
    public static string? ModelFromPath(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath)) return null;

            // The model folder named directly: <Model>/<Model>.
            var asFolder = Doubled(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (asFolder is not null) return asFolder;

            // A file inside it: the parent is the AOT subfolder, the model is above that.
            var axFolder = Path.GetDirectoryName(fullPath);
            if (axFolder is null) return null;
            if (!Path.GetFileName(axFolder).StartsWith("Ax", StringComparison.OrdinalIgnoreCase)) return null;
            return Doubled(Path.GetDirectoryName(axFolder));
        }
        catch { /* not a usable path */ }
        return null;
    }

    /// <summary>The folder's name when its parent repeats it, which is how a model announces itself.</summary>
    private static string? Doubled(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var name = Path.GetFileName(dir);
        var parent = Path.GetDirectoryName(dir);
        if (name.Length == 0 || parent is null) return null;
        return string.Equals(name, Path.GetFileName(parent), StringComparison.OrdinalIgnoreCase) ? name : null;
    }

    /// <summary>
    /// The directory of <paramref name="model"/> under the first root that has one, or null.
    /// </summary>
    /// <remarks>
    /// The one on-disk model lookup. <c>index sync</c>, <c>verify project</c> and the
    /// <c>generate --install-to</c> fallback all resolve a model name through it, so they agree
    /// about where a model lives. Pass <see cref="D365FoSettings.ModelRoots"/> for the
    /// configured order (custom-model roots first).
    /// </remarks>
    public static string? FindModelDir(IEnumerable<string?> roots, string model)
        => string.IsNullOrWhiteSpace(model)
            ? null
            : roots
                .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                .SelectMany(r => EnumerateModelDirs(r!, model))
                .FirstOrDefault();

    /// <summary>
    /// Model directories under a packages root, optionally narrowed to one model.
    /// </summary>
    /// <remarks>
    /// The marker folders come from <see cref="ObjectTypeRegistry"/>, so this and
    /// <see cref="MetadataExtractor"/> never disagree about what counts as a model directory.
    /// </remarks>
    public static IEnumerable<string> EnumerateModelDirs(string packagesRoot, string? onlyModel)
    {
        static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        foreach (var pkg in SafeDirs(packagesRoot))
        {
            // Mirror MetadataExtractor: skip FormAdaptor shim packages.
            if (MetadataExtractor.IsFormAdaptorPackage(Path.GetFileName(pkg))) continue;
            foreach (var model in SafeDirs(pkg))
            {
                if (MetadataExtractor.IsFormAdaptorPackage(Path.GetFileName(model))) continue;
                // Name first: it costs nothing, and narrows a one-model lookup to one listing.
                if (onlyModel is { Length: > 0 } only &&
                    !string.Equals(Path.GetFileName(model), only, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!ObjectTypeRegistry.IsModelDirectory(model)) continue;
                yield return model;
            }
        }
    }

    /// <summary>What one walk of a model folder learns: enough for the fingerprint and for staleness.</summary>
    /// <param name="FileCount">Metadata files (<c>*.xml</c>, <c>*.label.txt</c>) under the folder.</param>
    /// <param name="NewestUtc">Newest last-write time among them, or <see cref="DateTime.MinValue"/> when there are none.</param>
    /// <param name="NewestFile">Full path of that newest file, or null when there are none.</param>
    /// <param name="NameHash">Order-independent hash of the relative file names, so a rename or a delete-and-add changes it.</param>
    public readonly record struct ModelScan(int FileCount, DateTime NewestUtc, string? NewestFile, ulong NameHash);

    /// <summary>
    /// Walk a model folder once and return its file count, newest mtime and a hash of the
    /// relative file names.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One enumeration, no per-file stat. <see cref="DirectoryInfo.EnumerateFiles(string, EnumerationOptions)"/>
    /// hands back <see cref="FileInfo"/> objects already filled from the directory listing, so the
    /// last-write time costs nothing extra. The previous shape — <c>Directory.EnumerateFiles</c>
    /// then <c>File.GetLastWriteTimeUtc</c> per path, done once for the fingerprint and again for
    /// <c>--since</c> — was two walks and one syscall per file, which is how a refresh that changed
    /// nothing took 45 s on a 178-model UDE.
    /// </para>
    /// <para>
    /// The name hash is what lets the fingerprint notice a rename: a renamed file keeps its mtime
    /// and the count does not move, so <c>count:newestMtime</c> alone called the model unchanged
    /// and the index kept the old name. Each relative path is hashed with FNV-1a (stable across
    /// processes, unlike <see cref="string.GetHashCode()"/>) and the hashes are summed, so the
    /// result does not depend on the order the file system lists entries in.
    /// </para>
    /// </remarks>
    public static ModelScan ScanModel(string dir)
    {
        int count = 0;
        long newestTicks = 0;
        string? newestFile = null;
        ulong nameHash = 0;
        try
        {
            var root = new DirectoryInfo(dir);
            var prefix = root.FullName.Length;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            };
            foreach (var f in root.EnumerateFiles("*", options))
            {
                var name = f.Name;
                if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                var full = f.FullName;
                nameHash += HashRelativeName(full.AsSpan(prefix));
                long ticks;
                try { ticks = f.LastWriteTimeUtc.Ticks; }
                catch { continue; }
                if (ticks > newestTicks)
                {
                    newestTicks = ticks;
                    newestFile = full;
                }
            }
        }
        catch { /* unreadable folder: whatever was counted so far stands */ }
        return new ModelScan(
            count,
            newestTicks == 0 ? DateTime.MinValue : new DateTime(newestTicks, DateTimeKind.Utc),
            newestFile,
            nameHash);
    }

    /// <summary>FNV-1a over a relative path, case- and separator-insensitive.</summary>
    private static ulong HashRelativeName(ReadOnlySpan<char> relative)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var h = offset;
        foreach (var c in relative)
        {
            var n = c == '\\' ? '/' : char.ToUpperInvariant(c);
            h = (h ^ (byte)n) * prime;
            h = (h ^ (byte)(n >> 8)) * prime;
        }
        return h;
    }

    /// <summary>
    /// Cheap per-model content fingerprint used by <c>index refresh</c> to skip untouched models.
    /// Format: <c>"{fileCount}:{newestMtimeTicks}:{langs}:{nameHash}"</c>. Sensitive enough to
    /// catch touches, deletes and renames (re-export, rebase, partial sync) without paying for a
    /// content hash.
    /// </summary>
    /// <remarks>
    /// Fingerprints stored before the name hash was added have three segments and never equal a
    /// four-segment one, so an index written by an older build re-extracts each model once on its
    /// first refresh and is skipped from then on. Nothing parses the stored value — it is only
    /// compared — so an old or foreign format cannot fail a refresh.
    /// </remarks>
    public static string ComputeFingerprint(string dir, IReadOnlyList<string>? labelLanguages = null)
        => FormatFingerprint(ScanModel(dir), labelLanguages);

    /// <summary>The fingerprint for a scan already taken, so a caller that also needs the mtime walks once.</summary>
    public static string FormatFingerprint(ModelScan scan, IReadOnlyList<string>? labelLanguages = null)
    {
        var langs = labelLanguages is { Count: > 0 }
            ? string.Join(",", labelLanguages.Select(l => l.ToLowerInvariant()).OrderBy(l => l))
            : "";
        var ticks = scan.FileCount == 0 ? 0 : scan.NewestUtc.Ticks;
        return $"{scan.FileCount}:{ticks}:{langs}:{scan.NameHash:x16}";
    }
}
