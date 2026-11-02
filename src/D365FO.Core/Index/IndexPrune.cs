namespace D365FO.Core.Index;

/// <summary>
/// Models the index still holds although their folder has left every configured packages root.
/// </summary>
/// <remarks>
/// <para>
/// Refresh and sync only ever re-read models they find under a configured root. A model whose
/// folder moved away — a repository moved to a new clone, a model deleted, a root dropped from
/// <c>D365FO_CUSTOM_PACKAGES_PATH</c> — is never visited again, so its rows stay forever: <c>search</c>
/// keeps returning its objects with paths that lead nowhere, and <c>find references --model</c>
/// scans zero files and answers zero. On one UDE, 80 classes of a test model still pointed at a
/// clone the team had stopped using.
/// </para>
/// <para>
/// A model is <em>departed</em> when no configured root holds a folder of that name any more, and
/// the folder it was indexed from either sits outside every configured root or no longer exists.
/// A model whose indexed folder sits under a configured root that is itself missing is never
/// departed: that is an unmounted drive or a typo in a setting, and pruning it would delete a
/// perfectly good model (including a standard one) because the disk was briefly not there.
/// For the same reason, while any configured root is missing no model is judged "outside every
/// root": the missing one may be a misspelling of the root that model lives under.
/// A model found under a root but indexed from somewhere else is <em>relocated</em> instead —
/// <c>index sync</c> fixes it, and deleting it would be wrong.
/// </para>
/// </remarks>
public static class IndexPrune
{
    /// <summary>Why a model counts as departed.</summary>
    public static class Reasons
    {
        /// <summary>The indexed folder is outside every configured root.</summary>
        public const string OutsideRoots = "outside-configured-roots";

        /// <summary>The indexed folder is under a configured root but no longer exists.</summary>
        public const string FolderMissing = "folder-missing";
    }

    /// <param name="Model">Model name.</param>
    /// <param name="IsCustom">Whether the index classed it as custom.</param>
    /// <param name="IndexedFolder">The model folder its rows point at.</param>
    /// <param name="Reason">One of <see cref="Reasons"/>.</param>
    public sealed record DepartedModel(string Model, bool IsCustom, string IndexedFolder, string Reason);

    /// <param name="Model">Model name.</param>
    /// <param name="IndexedFolder">The folder its rows point at.</param>
    /// <param name="CurrentFolder">The folder a configured root holds for it now.</param>
    public sealed record RelocatedModel(string Model, string IndexedFolder, string CurrentFolder);

    /// <param name="Departed">Models whose rows can go.</param>
    /// <param name="Relocated">Models present under a root but indexed from another folder.</param>
    /// <param name="MissingRoots">Configured roots that do not exist right now. Models under them are left alone.</param>
    /// <param name="CheckedRoots">The roots that were looked in.</param>
    public sealed record Report(
        IReadOnlyList<DepartedModel> Departed,
        IReadOnlyList<RelocatedModel> Relocated,
        IReadOnlyList<string> MissingRoots,
        IReadOnlyList<string> CheckedRoots)
    {
        /// <summary>True when no configured root exists, so nothing can be judged.</summary>
        public bool NoRootReachable => CheckedRoots.Count == 0;
    }

    /// <summary>The hint every report of departed models carries.</summary>
    public const string PruneHint =
        "Run `d365fo index refresh --prune` (MCP: `index_sync` with `prune: true`) to remove their rows. "
        + "If the folder moved to a root that is not configured yet, add that root to "
        + "D365FO_CUSTOM_PACKAGES_PATH and refresh instead.";

    /// <summary>
    /// Compare where each indexed model was read from with the configured roots.
    /// </summary>
    /// <param name="repo">Index to read.</param>
    /// <param name="configuredRoots">Packages root and custom packages roots, existing or not.</param>
    public static Report Find(MetadataRepository repo, IEnumerable<string?> configuredRoots)
    {
        var roots = configuredRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Normalize(r!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var existing = roots.Where(Directory.Exists).ToList();
        var missing = roots.Except(existing, StringComparer.OrdinalIgnoreCase).ToList();

        var departed = new List<DepartedModel>();
        var relocated = new List<RelocatedModel>();
        if (existing.Count == 0)
            return new Report(departed, relocated, missing, existing);

        // Where each model lives now, by one listing per root.
        var present = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in existing)
            foreach (var dir in IndexSync.EnumerateModelDirs(root, onlyModel: null))
                present.TryAdd(Path.GetFileName(dir), Normalize(dir));

        foreach (var loc in repo.GetModelLocations())
        {
            if (string.IsNullOrWhiteSpace(loc.SamplePath)) continue;     // nothing says where it came from
            var indexedFolder = ModelFolderOf(loc.SamplePath!);
            if (indexedFolder is null) continue;

            if (present.TryGetValue(loc.Model, out var current))
            {
                if (!string.Equals(current, indexedFolder, StringComparison.OrdinalIgnoreCase))
                    relocated.Add(new RelocatedModel(loc.Model, indexedFolder, current));
                continue;
            }

            if (missing.Any(r => IsUnder(indexedFolder, r))) continue;     // its root is not there right now
            if (!existing.Any(r => IsUnder(indexedFolder, r)))
            {
                // A configured root that is missing may be a typo or a changed drive letter for the
                // very root this model lives under, so "outside every root" cannot be told apart
                // from "under a root spelled differently". Only judge it once every root resolves.
                if (missing.Count == 0)
                    departed.Add(new DepartedModel(loc.Model, loc.IsCustom, indexedFolder, Reasons.OutsideRoots));
            }
            else if (!Directory.Exists(indexedFolder))
                departed.Add(new DepartedModel(loc.Model, loc.IsCustom, indexedFolder, Reasons.FolderMissing));
        }

        return new Report(departed, relocated, missing, existing);
    }

    /// <summary>Delete the rows of every departed model in <paramref name="report"/>. Returns the models removed.</summary>
    public static IReadOnlyList<string> Prune(MetadataRepository repo, Report report)
    {
        var removed = new List<string>();
        foreach (var d in report.Departed)
            if (repo.DeleteModel(d.Model, d.IndexedFolder)) removed.Add(d.Model);
        if (removed.Count > 0) repo.CheckpointWal();
        return removed;
    }

    /// <summary>
    /// <c>index sync</c> with <c>prune</c>: re-read the named model when one is given, then delete the
    /// rows of every departed model. With prune off this is exactly <see cref="IndexSync.Sync"/>.
    /// </summary>
    /// <remarks>
    /// Pruning needs no model, so <c>prune</c> alone is a complete request — the one an agent
    /// makes after <c>index status</c> reports departed models. It refuses when no configured root
    /// exists: with nothing to compare against, every model would look departed.
    /// </remarks>
    public static ToolResult<object> SyncThenPrune(
        string? model, string? path, string? packagesOverride, string? databasePath,
        bool indexSource, bool prune)
    {
        var namesModel = !string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(path);
        if (!prune)
            return IndexSync.Sync(model, path, packagesOverride, databasePath, indexSource);

        object? synced = null;
        if (namesModel)
        {
            var sync = IndexSync.Sync(model, path, packagesOverride, databasePath, indexSource);
            if (!sync.Ok) return sync;
            synced = sync.Data;
        }

        var cfg = D365FoSettings.FromEnvironment(databasePath);
        if (string.IsNullOrWhiteSpace(packagesOverride) && string.IsNullOrWhiteSpace(cfg.PackagesPath))
            return ToolResult<object>.Fail("PACKAGES_PATH_NOT_FOUND",
                "No packages root is configured, so every standard model would look departed.",
                "Set D365FO_PACKAGES_PATH (or pass packagesPath) before pruning.");
        var roots = new List<string?> { packagesOverride, cfg.PackagesPath };
        roots.AddRange(cfg.CustomPackagesPaths);

        var repo = new MetadataRepository(cfg.DatabasePath);
        repo.EnsureSchema();
        var report = Find(repo, roots);
        if (report.NoRootReachable)
            return ToolResult<object>.Fail("PACKAGES_PATH_NOT_FOUND",
                "No configured packages root exists, so no model can be judged as departed.",
                report.MissingRoots.Count == 0
                    ? "Set D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots)."
                    : $"None of these exist right now: {string.Join(", ", report.MissingRoots)}. "
                      + "Mount or fix them first; pruning against no root would delete every model.");

        var removed = Prune(repo, report);
        return ToolResult<object>.Success(new
        {
            sync = synced,
            pruned = removed,
            relocatedModels = report.Relocated.Count > 0
                ? report.Relocated.Select(r => new { model = r.Model, indexedFolder = r.IndexedFolder, currentFolder = r.CurrentFolder })
                : null,
            missingRoots = report.MissingRoots.Count > 0 ? report.MissingRoots : null,
            note = report.MissingRoots.Count > 0
                ? "Some configured roots are missing, so only models whose folder vanished under an existing root were pruned: "
                  + "a root that is briefly unmounted or mistyped is not a reason to delete."
                : null,
        });
    }

    /// <summary>
    /// The model folder of an indexed object: <c>&lt;Model&gt;/Ax&lt;Kind&gt;/&lt;Name&gt;.xml</c>, so two
    /// levels up through an <c>Ax*</c> folder. Null for a path not in that layout.
    /// </summary>
    internal static string? ModelFolderOf(string sourcePath)
    {
        try
        {
            var axFolder = Path.GetDirectoryName(sourcePath);
            if (axFolder is null || !Path.GetFileName(axFolder).StartsWith("Ax", StringComparison.OrdinalIgnoreCase))
                return null;
            var modelFolder = Path.GetDirectoryName(axFolder);
            return string.IsNullOrEmpty(modelFolder) ? null : Normalize(modelFolder);
        }
        catch { return null; }
    }

    private static bool IsUnder(string path, string root)
        => path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
           || string.Equals(path, root, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return Path.TrimEndingDirectorySeparator(path); }
    }
}
