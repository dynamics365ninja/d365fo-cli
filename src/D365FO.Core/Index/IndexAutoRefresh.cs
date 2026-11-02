using System.Text.Json.Nodes;

namespace D365FO.Core.Index;

/// <summary>
/// Re-index the model(s) a write just touched, so the next read in the same session sees it.
/// </summary>
/// <remarks>
/// <para>
/// No write used to refresh the index. An agent created a table, then <c>generate entity --table</c>
/// answered <c>TABLE_NOT_FOUND</c> for it; it added a method, then <c>modify method</c> could not find
/// it. The documented workaround — run <c>index refresh --model</c> after every write — was easy to
/// forget and, without <c>--index-source</c>, silently dropped the model's method-body index.
/// </para>
/// <para>
/// The journal is the hook. Every reversible write appends a <c>JournalEntry</c> naming its model
/// and target path, and undo reverses one, so both call <see cref="Touch"/>. A host (the CLI
/// process, one MCP tool call) opens a <see cref="Scope"/>, runs the command, then
/// <see cref="Scope.Flush"/> re-syncs each touched model once through <see cref="IndexSync.Sync"/>.
/// Without an open scope nothing is collected, so library callers and tests that write files are
/// unaffected.
/// </para>
/// <para>
/// Only models under a configured packages root are refreshed, found by where the written file
/// sits rather than by name: a scaffold written with <c>--out</c> to a scratch folder is not part of
/// the index, and a test model that happens to share a standard model's name must not trigger a
/// minutes-long re-extract of the real one. Whether the model had method bodies in the full-text
/// index is read before the sync and passed on, so a refresh never drops them.
/// </para>
/// <para>Opt out with <c>D365FO_AUTO_REFRESH=0</c> (or <c>--no-refresh</c> on the CLI).</para>
/// </remarks>
public static class IndexAutoRefresh
{
    /// <summary>Environment variable that turns the refresh off when set to <c>0</c>, <c>false</c> or <c>off</c>.</summary>
    public const string EnvVar = "D365FO_AUTO_REFRESH";

    private static readonly AsyncLocal<Scope?> Current = new();

    /// <summary>True unless <see cref="EnvVar"/> turns the refresh off.</summary>
    public static bool EnabledByEnvironment()
    {
        var raw = D365FoSettings.Resolve(EnvVar);
        return raw is null || !(raw.Trim().Equals("0", StringComparison.Ordinal)
                                || raw.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)
                                || raw.Trim().Equals("off", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Start collecting touched models on this logical flow. Dispose to stop; flush first to refresh.
    /// </summary>
    /// <param name="enabled">False records nothing, as when the caller passed <c>--no-refresh</c>.</param>
    /// <param name="databasePath">Index to refresh. Defaults to the configured one, the same index the journal sits next to.</param>
    /// <param name="packagesRoots">Roots a write must land under to be refreshed. Defaults to the configured ones.</param>
    public static Scope BeginScope(bool enabled = true, string? databasePath = null, IReadOnlyList<string>? packagesRoots = null)
    {
        var scope = new Scope(enabled && EnabledByEnvironment(), databasePath, packagesRoots, Current.Value);
        Current.Value = scope;
        return scope;
    }

    /// <summary>The scope open on this logical flow, or null.</summary>
    public static Scope? CurrentScope => Current.Value;

    /// <summary>
    /// Record that a write landed in <paramref name="model"/> at <paramref name="path"/>. No-op without an open scope.
    /// </summary>
    public static void Touch(string? model, string? path)
    {
        var scope = Current.Value;
        if (scope is null || !scope.Enabled) return;
        if (string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(path)) return;
        scope.Add(model, path);
    }

    /// <summary>One model's refresh.</summary>
    /// <param name="Model">Model re-read.</param>
    /// <param name="Ok">False when the sync failed. <see cref="Error"/> then says why.</param>
    /// <param name="ElapsedMs">Time the sync took.</param>
    /// <param name="MethodSourceIndexed">Whether method bodies were full-text indexed again, as they were before.</param>
    /// <param name="Error">The sync's error message, or null.</param>
    public sealed record ModelRefresh(string Model, bool Ok, long ElapsedMs, bool MethodSourceIndexed, string? Error);

    /// <summary>A set of touched models collected during one command or tool call.</summary>
    public sealed class Scope : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<(string? Model, string? Path)> _touched = new();
        private readonly Scope? _parent;
        private readonly IReadOnlyList<string>? _roots;
        private bool _disposed;

        internal Scope(bool enabled, string? databasePath, IReadOnlyList<string>? roots, Scope? parent)
        {
            Enabled = enabled;
            DatabasePath = databasePath;
            _roots = roots?
                .Where(Directory.Exists)
                .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
                .ToList();
            _parent = parent;
        }

        /// <summary>False when the refresh is turned off for this scope.</summary>
        public bool Enabled { get; }

        /// <summary>Index the refresh writes to, or null for the configured one.</summary>
        public string? DatabasePath { get; }

        internal void Add(string? model, string? path)
        {
            lock (_gate) _touched.Add((model, path));
        }

        /// <summary>
        /// Re-sync every model touched since the last flush. Returns null when nothing under a
        /// configured root was touched.
        /// </summary>
        public IReadOnlyList<ModelRefresh>? Flush()
        {
            List<(string? Model, string? Path)> touched;
            lock (_gate)
            {
                if (_touched.Count == 0) return null;
                touched = new List<(string?, string?)>(_touched);
                _touched.Clear();
            }

            var roots = _roots ?? ConfiguredRoots(DatabasePath);
            if (roots.Count == 0) return null;

            // model → the root its write landed under (null when only the model name is known).
            var models = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (model, path) in touched)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    var located = Locate(path!, roots);
                    if (located is null) continue;              // written outside every configured root
                    models.TryAdd(located.Value.Model, located.Value.Root);
                }
                else if (!string.IsNullOrWhiteSpace(model))
                {
                    // A bridge write names its model and lands in that model's configured folder.
                    models.TryAdd(model!, null);
                }
            }
            if (models.Count == 0) return null;

            return models.Select(m => RefreshModel(m.Key, m.Value, DatabasePath)).ToList();
        }

        /// <summary>Close the scope and restore the one it replaced. Untouched models are not refreshed.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(Current.Value, this)) Current.Value = _parent;
        }
    }

    /// <summary>
    /// The packages roots that exist: the custom roots first, then <c>D365FO_PACKAGES_PATH</c>,
    /// full paths without a trailing separator.
    /// </summary>
    public static IReadOnlyList<string> ConfiguredRoots(string? databasePath = null, string? packagesOverride = null)
    {
        var cfg = D365FoSettings.FromEnvironment(databasePath);
        return new[] { packagesOverride }
            .Concat(cfg.CustomPackagesPaths)
            .Append(cfg.PackagesPath)
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The model a changed file belongs to, when it sits under one of the configured roots.
    /// </summary>
    public static (string Model, string Root)? LocateUnderRoots(string path, IReadOnlyList<string> roots)
        => Locate(path, roots);

    /// <summary>
    /// Re-sync one model, keeping its method bodies in the full-text index when it had them.
    /// </summary>
    /// <param name="model">Model to re-read.</param>
    /// <param name="packagesRoot">Root to look in first, typically the one the change landed under. The configured roots are searched after it.</param>
    /// <param name="databasePath">Index to write. Defaults to the configured one.</param>
    /// <remarks>
    /// The method-source state is read from the index rather than assumed: a sync without
    /// <c>indexSource</c> clears the model's bodies, which is what made the old advice to run
    /// <c>index refresh --model</c> after a write quietly break <c>find references</c>.
    /// </remarks>
    public static ModelRefresh RefreshModel(string model, string? packagesRoot, string? databasePath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hadSource = false;
        ToolResult<object> r;
        try
        {
            var repo = new MetadataRepository(D365FoSettings.FromEnvironment(databasePath).DatabasePath);
            repo.EnsureSchema();
            hadSource = repo.CountMethodSource(model) > 0;
            r = IndexSync.Sync(model, null, packagesRoot, databasePath, indexSource: hadSource);
        }
        catch (Exception ex)
        {
            r = ToolResult<object>.Fail("INDEX_REFRESH_FAILED", ex.Message);
        }
        sw.Stop();
        return new ModelRefresh(model, r.Ok, sw.ElapsedMilliseconds, hadSource && r.Ok, r.Error?.Message);
    }

    /// <summary>
    /// The model a path under one of <paramref name="roots"/> belongs to:
    /// <c>&lt;root&gt;/&lt;Package&gt;/&lt;Model&gt;/…</c>. Null when the path is under none of them.
    /// </summary>
    /// <remarks>
    /// Read relative to the root, not off the folder names: a label file sits four levels below
    /// its model (<c>AxLabelFile/LabelResources/&lt;lang&gt;/</c>), so the doubled-folder rule
    /// <see cref="IndexSync.ModelFromPath"/> uses for an object file does not reach it.
    /// </remarks>
    internal static (string Model, string Root)? Locate(string path, IReadOnlyList<string> roots)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch { return null; }
        foreach (var root in roots)
        {
            // A root at a drive or filesystem root already ends with the separator.
            var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = full[prefix.Length..].Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            // <Package>/<Model>/Ax<Kind>/… — only the AOT folders are metadata the index reads. A build
            // writes <Package>/bin, <Package>/XppMetadata/<Model>/… and the descriptor sits in
            // <Package>/Descriptor; none of those is a model, and naming them would make the daemon
            // watcher try (and fail) to re-sync a model called "bin" on every compile.
            if (parts.Length >= 4
                && parts[2].StartsWith("Ax", StringComparison.OrdinalIgnoreCase)
                && !parts[1].Equals("XppMetadata", StringComparison.OrdinalIgnoreCase))
                return (parts[1], root);
        }
        return null;
    }

    /// <summary>
    /// The payload a write result carries: <c>{ model, elapsedMs }</c> for the common single-model
    /// write, plus <c>models</c> listing each one when a write touched several.
    /// </summary>
    public static JsonObject Describe(IReadOnlyList<ModelRefresh> refreshed)
    {
        var ok = refreshed.Where(r => r.Ok).ToList();
        var first = ok.FirstOrDefault() ?? refreshed[0];
        var node = new JsonObject
        {
            ["model"] = first.Model,
            ["elapsedMs"] = refreshed.Sum(r => r.ElapsedMs),
        };
        if (first.MethodSourceIndexed) node["methodSourceIndexed"] = true;
        if (refreshed.Count > 1)
        {
            var arr = new JsonArray();
            foreach (var r in refreshed)
                arr.Add(new JsonObject { ["model"] = r.Model, ["ok"] = r.Ok, ["elapsedMs"] = r.ElapsedMs });
            node["models"] = arr;
        }
        return node;
    }

    /// <summary>How the write paths word the warning that the index was not refreshed.</summary>
    public const string NotRefreshedWarningPrefix = "Index not auto-refreshed";

    /// <summary>Warnings for refreshes that failed: the write stands, the index did not catch up.</summary>
    public static IReadOnlyList<string> FailureWarnings(IReadOnlyList<ModelRefresh> refreshed)
        => refreshed.Where(r => !r.Ok)
            .Select(r => $"index-refresh-failed: model '{r.Model}' was written but not re-indexed ({r.Error}). "
                         + $"Run `d365fo index sync {r.Model}`.")
            .ToList();

    /// <summary>
    /// Add <c>indexRefreshed</c> (and any failure warnings) to a tool result. A result whose data is
    /// not a JSON object is returned with the warnings only.
    /// </summary>
    public static ToolResult<object> Attach(ToolResult<object> result, IReadOnlyList<ModelRefresh> refreshed)
    {
        // Write paths still say "Index not auto-refreshed" for callers that run them without a
        // scope. Once every touched model was re-read, that sentence is false.
        var allOk = refreshed.All(r => r.Ok);
        var warnings = (result.Warnings ?? Array.Empty<string>())
            .Where(w => !(allOk && w.StartsWith(NotRefreshedWarningPrefix, StringComparison.Ordinal)))
            .Concat(FailureWarnings(refreshed))
            .ToList();
        object? data = result.Data;
        if (refreshed.Any(r => r.Ok) && result.Data is not null)
        {
            var node = result.Data as JsonNode
                       ?? System.Text.Json.JsonSerializer.SerializeToNode(result.Data, D365Json.Options);
            if (node is JsonObject obj)
            {
                obj["indexRefreshed"] = Describe(refreshed);
                data = obj;
            }
        }
        return new ToolResult<object>(result.Ok, data, result.Error, warnings.Count > 0 ? warnings : null);
    }
}
