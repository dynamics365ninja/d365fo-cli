namespace D365FO.Core.Index;

/// <summary>
/// Index staleness detection — port of the upstream MCP server's
/// <c>indexStaleness</c> module.
///
/// The index is the single source of truth for grounding — but only while it
/// reflects the current workspace. This compares the newest XML/label mtime
/// under each custom model's folder with that model's <c>Models.LastExtractedUtc</c>
/// bookkeeping and produces the <c>stale-index</c> warning when the workspace
/// has changed since the last (re)extract.
/// </summary>
public static class IndexStaleness
{
    /// <summary>Hard cap on stat'ed files so the scan stays fast on huge roots.</summary>
    public const int MaxScannedFiles = 5000;

    /// <summary>Files newer than the index by less than this are tolerated (clock skew, in-flight writes).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(60);

    public sealed record MtimeScanResult(DateTime NewestMtimeUtc, string NewestFile, int ScannedFiles, bool Truncated);

    /// <param name="IsStale">True when at least one model changed on disk after it was last extracted.</param>
    /// <param name="Detail">What changed and what to run, or null when fresh.</param>
    /// <param name="NewestFileMtimeUtc">Newest metadata mtime seen (of a stale model when there is one).</param>
    /// <param name="NewestFile">The file carrying that mtime.</param>
    /// <param name="LastExtractedUtc">The extract time the newest file was compared with.</param>
    /// <param name="ScannedFiles">Metadata files looked at.</param>
    /// <param name="Truncated">True when a scan hit <see cref="MaxScannedFiles"/>.</param>
    /// <param name="StaleModels">Models whose folder changed after their own last extract, or null when the check could not judge per model.</param>
    public sealed record StalenessResult(
        bool IsStale,
        string? Detail,
        DateTime? NewestFileMtimeUtc,
        string? NewestFile,
        DateTime? LastExtractedUtc,
        int ScannedFiles,
        bool Truncated,
        IReadOnlyList<string>? StaleModels = null);

    /// <summary>
    /// Recursively find the newest metadata file mtime under <paramref name="rootDir"/>.
    /// Returns null when the directory does not exist or contains no metadata files.
    /// </summary>
    /// <remarks>
    /// Reads the mtime off the <see cref="FileSystemInfo"/> the directory listing already
    /// returned, rather than asking the file system again for each path.
    /// </remarks>
    public static MtimeScanResult? FindNewestMetadataMtime(string rootDir)
    {
        if (!Directory.Exists(rootDir)) return null;
        DateTime newest = DateTime.MinValue;
        string newestFile = "";
        int scanned = 0;
        bool truncated = false;

        void Walk(DirectoryInfo dir)
        {
            if (truncated) return;
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos())
                {
                    if (truncated) return;
                    if (entry is DirectoryInfo sub)
                    {
                        // bin/obj and Descriptor churn are not metadata edits.
                        if (sub.Name is "bin" or "obj" or ".git" or "XppMetadata") continue;
                        Walk(sub);
                    }
                    else if (entry.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                             || entry.Name.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase))
                    {
                        scanned++;
                        if (scanned >= MaxScannedFiles) truncated = true;
                        DateTime mtime;
                        try { mtime = entry.LastWriteTimeUtc; }
                        catch { continue; }
                        if (mtime > newest)
                        {
                            newest = mtime;
                            newestFile = entry.FullName;
                        }
                    }
                }
            }
            catch { /* unreadable, or the folder vanished mid-walk */ }
        }

        Walk(new DirectoryInfo(rootDir));
        if (scanned == 0) return null;
        return new MtimeScanResult(newest, newestFile, scanned, truncated);
    }

    /// <summary>
    /// Compare each custom model's folder with that model's own <c>LastExtractedUtc</c>, or — when
    /// no custom model folder is found — the whole roots with the newest extract of any model.
    /// </summary>
    /// <remarks>
    /// Per model, not global. Comparing every folder against the newest extract of ANY model meant
    /// that syncing one model stamped a fresh time that covered all the others: edit model A, run
    /// <c>index sync B</c>, and A's edit was reported as indexed. Each model is now held to its own
    /// extract time, so the answer names the models that really are behind.
    /// </remarks>
    public static StalenessResult Check(MetadataRepository repo, IReadOnlyCollection<string> packagesRoots)
    {
        DateTime? lastExtracted = repo.GetNewestExtractTimestampUtc();
        if (lastExtracted is null)
        {
            return new StalenessResult(true, "Index has never been extracted — run `d365fo index extract`.",
                null, null, null, 0, false);
        }

        // Scan only custom models' folders when known: standard models do not
        // change outside platform updates, and the 5000-file cap goes further.
        IReadOnlyList<MetadataRepository.ModelExtractState> custom;
        try { custom = repo.GetModelExtractStates().Where(m => m.IsCustom).ToList(); }
        catch { custom = Array.Empty<MetadataRepository.ModelExtractState>(); }

        if (custom.Count > 0)
        {
            var perModel = CheckPerModel(custom, packagesRoots, lastExtracted.Value);
            if (perModel is not null) return perModel;
        }

        // No custom model folder found: fall back to the whole roots against the global time.
        MtimeScanResult? newest = null;
        foreach (var root in packagesRoots.Where(Directory.Exists))
        {
            var scan = FindNewestMetadataMtime(root);
            if (scan is null) continue;
            if (newest is null || scan.NewestMtimeUtc > newest.NewestMtimeUtc)
                newest = scan with { ScannedFiles = (newest?.ScannedFiles ?? 0) + scan.ScannedFiles };
        }
        if (newest is null)
        {
            return new StalenessResult(false, null, null, null, lastExtracted, 0, false);
        }

        var stale = newest.NewestMtimeUtc > lastExtracted.Value + Tolerance;
        return new StalenessResult(
            stale,
            stale
                ? $"Workspace changed after the last extract: \"{newest.NewestFile}\" " +
                  $"({newest.NewestMtimeUtc:O}) is newer than the index ({lastExtracted.Value:O}). " +
                  "Run `d365fo index refresh`."
                : null,
            newest.NewestMtimeUtc,
            newest.NewestFile,
            lastExtracted,
            newest.ScannedFiles,
            newest.Truncated);
    }

    /// <summary>
    /// Hold each custom model's folder to that model's own extract time. Null when none of the
    /// models has a folder under the roots, so the caller can fall back to the whole roots.
    /// </summary>
    private static StalenessResult? CheckPerModel(
        IReadOnlyList<MetadataRepository.ModelExtractState> custom,
        IReadOnlyCollection<string> packagesRoots,
        DateTime newestExtract)
    {
        // One listing per root maps model name to folder(s): a package can hold several models,
        // and a model's folder is not always named after its package.
        var wanted = new HashSet<string>(custom.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var dirsByModel = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in packagesRoots.Where(Directory.Exists))
        {
            foreach (var dir in IndexSync.EnumerateModelDirs(root, onlyModel: null))
            {
                var name = Path.GetFileName(dir);
                if (!wanted.Contains(name)) continue;
                if (!dirsByModel.TryGetValue(name, out var list)) dirsByModel[name] = list = new List<string>();
                if (!list.Contains(dir, StringComparer.OrdinalIgnoreCase)) list.Add(dir);
            }
        }
        if (dirsByModel.Count == 0) return null;

        var staleModels = new List<string>();
        int scanned = 0;
        bool truncated = false;
        MtimeScanResult? newestStale = null, newestAny = null;
        DateTime staleBaseline = newestExtract;

        foreach (var model in custom)
        {
            if (!dirsByModel.TryGetValue(model.Name, out var dirs)) continue;
            // A row no extract has stamped has no time of its own. The global one is the best
            // stand-in, and is what every model was judged by before.
            var baseline = model.LastExtractedUtc ?? newestExtract;
            MtimeScanResult? modelNewest = null;
            foreach (var dir in dirs)
            {
                var scan = FindNewestMetadataMtime(dir);
                if (scan is null) continue;
                scanned += scan.ScannedFiles;
                truncated |= scan.Truncated;
                if (modelNewest is null || scan.NewestMtimeUtc > modelNewest.NewestMtimeUtc) modelNewest = scan;
            }
            if (modelNewest is null) continue;
            if (newestAny is null || modelNewest.NewestMtimeUtc > newestAny.NewestMtimeUtc) newestAny = modelNewest;
            if (modelNewest.NewestMtimeUtc > baseline + Tolerance)
            {
                staleModels.Add(model.Name);
                if (newestStale is null || modelNewest.NewestMtimeUtc > newestStale.NewestMtimeUtc)
                {
                    newestStale = modelNewest;
                    staleBaseline = baseline;
                }
            }
        }

        if (newestStale is null)
        {
            return new StalenessResult(false, null, newestAny?.NewestMtimeUtc, newestAny?.NewestFile,
                newestExtract, scanned, truncated, staleModels);
        }

        var next = staleModels.Count == 1
            ? $"Run `d365fo index sync {staleModels[0]}` or `d365fo index refresh`."
            : "Run `d365fo index refresh` (or `d365fo index sync <model>` for each).";
        return new StalenessResult(
            true,
            $"{staleModels.Count} model(s) changed after their last extract ({string.Join(", ", staleModels)}): " +
            $"\"{newestStale.NewestFile}\" ({newestStale.NewestMtimeUtc:O}) is newer than that model's index " +
            $"({staleBaseline:O}). {next}",
            newestStale.NewestMtimeUtc,
            newestStale.NewestFile,
            staleBaseline,
            scanned,
            truncated,
            staleModels);
    }
}
