using System.Text.Json;

namespace D365FO.Core;

/// <summary>
/// Resolved runtime configuration. Values are sourced, in order:
/// (1) explicit CLI flags, (2) process environment variables,
/// (3) the active named profile (<see cref="D365FoProfiles"/>), when one is selected,
/// (4) JSON config file at <see cref="GetDefaultConfigPath"/>,
/// (5) built-in defaults. See docs/CONFIGURATION.md.
/// </summary>
public sealed record D365FoSettings(
    string? PackagesPath,
    string? WorkspacePath,
    string DatabasePath,
    IReadOnlyList<string> CustomModels,
    IReadOnlyList<string> LabelLanguages,
    IReadOnlyList<string> CustomPackagesPaths)
{
    public const string DefaultDatabaseFile = "d365fo-index.sqlite";
    public const string ConfigFileName      = "settings.json";

    /// <summary>
    /// Process env var that relocates the whole config root (settings.json,
    /// profiles, default index DB). Read from the process environment only —
    /// it decides where settings.json lives, so it cannot come from it. Exists
    /// because Windows resolves <c>%LOCALAPPDATA%</c> through the known-folder
    /// API, which ignores the <c>LOCALAPPDATA</c> env var, so there was no other
    /// way to run the CLI against a throw-away config (tests, smoke runs,
    /// portable installs).
    /// </summary>
    public const string ConfigDirKey = "D365FO_CONFIG_DIR";

    /// <summary>Source labels reported by <see cref="ResolveWithSource"/>.</summary>
    public const string SourceEnv = "env";
    public const string SourceSettings = "settings";
    public const string SourceProfilePrefix = "profile:";
    public const string SourceDefault = "default";
    public const string SourceFlag = "flag";

    /// <summary>
    /// Root directory of all persisted CLI configuration: <c>%LOCALAPPDATA%\d365fo-cli</c>
    /// unless <see cref="ConfigDirKey"/> is set.
    /// </summary>
    public static string GetConfigRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable(ConfigDirKey);
        return !string.IsNullOrWhiteSpace(overrideDir)
            ? Path.GetFullPath(overrideDir)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "d365fo-cli");
    }

    /// <summary>
    /// Returns the path to the JSON config file used as a fallback when
    /// environment variables are not set. Typically
    /// <c>%LOCALAPPDATA%\d365fo-cli\settings.json</c> on Windows.
    /// </summary>
    public static string GetDefaultConfigPath() => Path.Combine(GetConfigRoot(), ConfigFileName);

    /// <summary>
    /// settings.json and profile-file contents, loaded once per process and
    /// cached. Invalidated by the save methods. Protected by <see cref="_cacheLock"/>.
    /// </summary>
    private static readonly object _cacheLock = new();
    // Keyed by file path, not just "loaded once", so a change of the config
    // root (D365FO_CONFIG_DIR) within a process never serves another root's values.
    private static string? jsonConfigCachePath;
    private static Dictionary<string, string>? jsonConfigCache;
    private static readonly Dictionary<string, Dictionary<string, string>> profileCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Override the config file path. For unit tests only — do not set in
    /// production code. Reset to null and call <see cref="ClearCacheForTests"/>
    /// between tests.
    /// </summary>
    internal static string? ConfigPathOverrideForTests;

    /// <summary>Clear the in-process JSON config cache. For unit tests only.</summary>
    internal static void ClearCacheForTests()
    {
        lock (_cacheLock)
        {
            jsonConfigCache = null;
            jsonConfigCachePath = null;
            profileCache.Clear();
        }
    }

    private static Dictionary<string, string> GetJsonConfig()
    {
        var path = ConfigPathOverrideForTests ?? GetDefaultConfigPath();
        lock (_cacheLock)
        {
            if (jsonConfigCache is null || !string.Equals(jsonConfigCachePath, path, StringComparison.OrdinalIgnoreCase))
            {
                jsonConfigCache = LoadJsonFile(path);
                jsonConfigCachePath = path;
            }
            return jsonConfigCache;
        }
    }

    /// <summary>Raw value from the global settings.json only (no env, no profile).</summary>
    internal static string? ReadGlobalJsonValue(string key)
        => GetJsonConfig().TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;

    /// <summary>All key/values of the global settings.json (no env, no profile).</summary>
    public static IReadOnlyDictionary<string, string> ReadGlobalJson() => GetJsonConfig();

    internal static Dictionary<string, string> GetProfileJson(string name)
    {
        if (!D365FoProfiles.IsValidName(name)) return new(StringComparer.OrdinalIgnoreCase);
        var path = D365FoProfiles.GetProfilePath(name);
        lock (_cacheLock)
        {
            if (!profileCache.TryGetValue(path, out var cached))
            {
                cached = LoadJsonFile(path);
                profileCache[path] = cached;
            }
            return cached;
        }
    }

    /// <summary>
    /// The active profile's key/values, or null when no (valid, existing) profile is
    /// selected. A missing profile resolves as "no layer" here; the hard error is
    /// raised once by the entry points via <see cref="D365FoProfiles.CheckActive"/>
    /// so that library callers never see an exception from a plain lookup.
    /// </summary>
    private static (string Name, Dictionary<string, string> Values)? GetActiveProfileLayer()
    {
        var active = D365FoProfiles.GetActive();
        if (active is null || !active.IsValidName || !active.Exists) return null;
        return (active.Name, GetProfileJson(active.Name));
    }

    /// <summary>
    /// Resolve a single configuration value using the standard precedence:
    /// (1) process environment variable, (2) the active profile file, (3) settings.json.
    /// Returns null when the key is set in none. This is the single entry point every
    /// call site should use so that settings.json and profiles are honored consistently.
    /// </summary>
    public static string? Resolve(string key) => ResolveWithSource(key).Value;

    /// <summary>
    /// <see cref="Resolve"/> plus where the value came from: <see cref="SourceEnv"/>,
    /// <c>profile:&lt;name&gt;</c>, <see cref="SourceSettings"/>, or null when unset.
    /// Used by <c>d365fo config show</c> so a user can see that e.g. a leftover
    /// shell-profile env var is shadowing the profile they just switched to.
    /// </summary>
    public static (string? Value, string? Source) ResolveWithSource(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return (env, SourceEnv);

        var profile = GetActiveProfileLayer();
        if (profile is { } p && p.Values.TryGetValue(key, out var pv) && !string.IsNullOrWhiteSpace(pv))
            return (pv, SourceProfilePrefix + p.Name);

        var config = GetJsonConfig();
        return config.TryGetValue(key, out var jv) && !string.IsNullOrWhiteSpace(jv)
            ? (jv, SourceSettings)
            : (null, null);
    }

    /// <summary>
    /// Resolve a boolean flag via <see cref="Resolve"/>. True when the resolved
    /// value is <c>"1"</c> or <c>"true"</c> (case-insensitive); otherwise
    /// <paramref name="defaultValue"/> when the key is unset.
    /// </summary>
    public static bool ResolveFlag(string key, bool defaultValue = false)
    {
        var v = Resolve(key);
        if (v is null) return defaultValue;
        return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolve the index DB path with its source. Differs from <see cref="Resolve"/>
    /// in one deliberate way: while a profile is active, a <c>D365FO_INDEX_DB</c> in
    /// the global settings.json is NOT inherited — the profile gets its own default
    /// under <c>profiles\&lt;name&gt;\</c>. Every <c>init --persist-profile</c> writes
    /// that key globally, so inheriting it would make every profile share (and
    /// overwrite) one index, which is the cross-environment mix-up profiles exist
    /// to prevent. An env var or the profile's own value still wins.
    /// </summary>
    public static (string Path, string Source) ResolveDatabasePath(string? databaseOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(databaseOverride)) return (databaseOverride, SourceFlag);

        const string key = "D365FO_INDEX_DB";
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return (env, SourceEnv);

        // Keyed on the selection, not on the file existing: `init --profile <new>`
        // resolves this before the profile file is written, and must already
        // point at the new profile's own index rather than the global one.
        var active = D365FoProfiles.GetActive();
        if (active is { IsValidName: true })
        {
            if (active.Exists
                && GetProfileJson(active.Name).TryGetValue(key, out var pv)
                && !string.IsNullOrWhiteSpace(pv))
                return (pv, SourceProfilePrefix + active.Name);
            return (GetDefaultDatabasePath(active.Name), SourceDefault);
        }

        var global = ReadGlobalJsonValue(key);
        if (global is not null) return (global, SourceSettings);

        return (GetDefaultDatabasePath(null), SourceDefault);
    }

    /// <summary>
    /// Built-in index DB location: <c>&lt;config-root&gt;\d365fo-index.sqlite</c>, or
    /// <c>&lt;config-root&gt;\profiles\&lt;name&gt;\d365fo-index.sqlite</c> for a profile.
    /// </summary>
    public static string GetDefaultDatabasePath(string? profileName)
        => string.IsNullOrWhiteSpace(profileName)
            ? Path.Combine(GetConfigRoot(), DefaultDatabaseFile)
            : Path.Combine(D365FoProfiles.GetProfileDataDirectory(profileName), DefaultDatabaseFile);

    public static D365FoSettings FromEnvironment(string? databaseOverride = null)
    {
        // Env var first, then profile, then settings.json fallback — same chain as Resolve.
        static string Env(string k) => Resolve(k) ?? string.Empty;

        var models = Split(Env("D365FO_CUSTOM_MODELS"));
        var langs = Split(Env("D365FO_LABEL_LANGUAGES"));
        if (langs.Count == 0) langs = new[] { "en-us" };

        var db = ResolveDatabasePath(databaseOverride).Path;

        // D365FO_CUSTOM_PACKAGES_PATH was previously named D365FO_EXTRA_PACKAGES_PATH.
        // Honor the old name as a deprecated alias so existing UDE configs keep
        // working after the rename — without it, custom-model roots would silently
        // drop out of the index. Explicit process env vars must outrank the JSON file,
        // and the new name wins over the deprecated one when both are set.
        var customPackages = ResolveDeprecatedAlias("D365FO_CUSTOM_PACKAGES_PATH", "D365FO_EXTRA_PACKAGES_PATH");

        return new D365FoSettings(
            PackagesPath: NullIfEmpty(Env("D365FO_PACKAGES_PATH")),
            WorkspacePath: NullIfEmpty(Env("D365FO_WORKSPACE_PATH")),
            DatabasePath: db,
            CustomModels: models,
            LabelLanguages: langs,
            CustomPackagesPaths: Split(customPackages ?? string.Empty));
    }

    /// <summary>
    /// Persists the supplied key/value pairs to the JSON config file,
    /// merging with any values already present. Existing keys are overwritten;
    /// keys absent from <paramref name="values"/> are preserved; keys listed in
    /// <paramref name="remove"/> are deleted.
    /// </summary>
    public static void SaveJsonConfig(IReadOnlyDictionary<string, string> values, IEnumerable<string>? remove = null)
    {
        var path = ConfigPathOverrideForTests ?? GetDefaultConfigPath();
        var merged = MergeAndWrite(path, values, remove);

        // Keep the in-process cache consistent with what we just persisted.
        lock (_cacheLock)
        {
            jsonConfigCache = merged;
            jsonConfigCachePath = path;
        }
    }

    internal static string SaveProfileJson(string name, IReadOnlyDictionary<string, string> values, IEnumerable<string>? remove)
    {
        var path = D365FoProfiles.GetProfilePath(name); // validates the name
        var merged = MergeAndWrite(path, values, remove);
        lock (_cacheLock) { profileCache[path] = merged; }
        return path;
    }

    // ---- private helpers -----------------------------------------------------

    private static Dictionary<string, string> MergeAndWrite(string path, IReadOnlyDictionary<string, string> values, IEnumerable<string>? remove)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var merged = LoadJsonFile(path);
        foreach (var (k, v) in values) merged[k] = v;
        if (remove is not null)
            foreach (var k in remove) merged.Remove(k);

        File.WriteAllText(path, JsonSerializer.Serialize(merged, D365Json.Pretty));
        return merged;
    }

    private static Dictionary<string, string> LoadJsonFile(string path)
    {
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = File.ReadAllText(path);
            return new Dictionary<string, string>(
                JsonSerializer.Deserialize<Dictionary<string, string>>(json, D365Json.Options)
                    ?? new(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ResolveDeprecatedAlias(string preferredKey, string deprecatedKey)
    {
        var preferredEnv = Environment.GetEnvironmentVariable(preferredKey);
        if (!string.IsNullOrWhiteSpace(preferredEnv)) return preferredEnv;

        var deprecatedEnv = Environment.GetEnvironmentVariable(deprecatedKey);
        if (!string.IsNullOrWhiteSpace(deprecatedEnv)) return deprecatedEnv;

        // File layers, most specific first. Within a layer the new name beats the
        // deprecated one; across layers the profile beats settings.json even when
        // the profile only carries the old name — a profile is an explicit choice.
        var layers = new List<Dictionary<string, string>>(2);
        if (GetActiveProfileLayer() is { } p) layers.Add(p.Values);
        layers.Add(GetJsonConfig());

        foreach (var config in layers)
        {
            if (config.TryGetValue(preferredKey, out var preferredJson) && !string.IsNullOrWhiteSpace(preferredJson))
                return preferredJson;

            if (config.TryGetValue(deprecatedKey, out var deprecatedJson) && !string.IsNullOrWhiteSpace(deprecatedJson))
                return deprecatedJson;
        }

        return null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IReadOnlyList<string> Split(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
