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
    /// <param name="indexSource">Also full-text index method bodies, as <c>index extract --index-source</c> does.</param>
    public static ToolResult<object> Sync(
        string? model, string? path, string? packagesOverride = null,
        string? databasePath = null, bool indexSource = false)
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

        var modelRoot = roots
            .Where(Directory.Exists)
            .SelectMany(root => EnumerateModelDirs(root, modelName))
            .FirstOrDefault();

        if (modelRoot is null)
            return ToolResult<object>.Fail("MODEL_NOT_FOUND",
                $"No model directory called '{modelName}' under any configured packages path.",
                roots.Count == 0
                    ? "Set D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots)."
                    : $"Looked in: {string.Join(", ", roots)}");

        var repo = new MetadataRepository(cfg.DatabasePath);
        repo.EnsureSchema();

        var matcher = new ModelMatcher(cfg.CustomModels);
        var extractor = new MetadataExtractor { CaptureMethodSource = indexSource };

        var startedUtc = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

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

        var fingerprint = ComputeFingerprint(modelRoot, cfg.LabelLanguages);
        repo.ApplyExtract(batch, fingerprint, indexSource);
        sw.Stop();
        repo.RecordExtractionRun(batch.Model, startedUtc, sw.ElapsedMilliseconds,
            batch.Tables.Count, batch.Classes.Count, batch.Edts.Count,
            batch.Enums.Count, batch.Labels.Count, batch.IsCustom);

        return ToolResult<object>.Success(new
        {
            model = batch.Model,
            modelRoot,
            isCustom = batch.IsCustom,
            elapsedMs = sw.ElapsedMilliseconds,
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

        static bool HasAot(string dir)
        {
            foreach (var s in ObjectTypeRegistry.ModelMarkerFolders())
                if (Directory.Exists(Path.Combine(dir, s))) return true;
            return false;
        }

        foreach (var pkg in SafeDirs(packagesRoot))
        {
            // Mirror MetadataExtractor: skip FormAdaptor shim packages.
            if (MetadataExtractor.IsFormAdaptorPackage(Path.GetFileName(pkg))) continue;
            foreach (var model in SafeDirs(pkg))
            {
                if (MetadataExtractor.IsFormAdaptorPackage(Path.GetFileName(model))) continue;
                if (!HasAot(model)) continue;
                if (onlyModel is { Length: > 0 } only &&
                    !string.Equals(Path.GetFileName(model), only, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return model;
            }
        }
    }

    /// <summary>
    /// Cheap per-model content fingerprint used by <c>index refresh</c> to skip untouched models.
    /// Format: <c>"{fileCount}:{newestMtimeTicks}:{langs}"</c>. Sensitive enough to catch touches
    /// (re-export, rebase, partial sync) without paying for a content hash.
    /// </summary>
    public static string ComputeFingerprint(string dir, IReadOnlyList<string>? labelLanguages = null)
    {
        long newestTicks = 0;
        int count = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                try
                {
                    var t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > newestTicks) newestTicks = t;
                }
                catch { }
            }
        }
        catch { }
        var langs = labelLanguages is { Count: > 0 }
            ? string.Join(",", labelLanguages.Select(l => l.ToLowerInvariant()).OrderBy(l => l))
            : "";
        return $"{count}:{newestTicks}:{langs}";
    }
}
