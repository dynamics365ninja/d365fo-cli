using System.Text.Json;

namespace D365FO.Core;

/// <summary>
/// Resolved runtime configuration. Values are sourced, in order:
/// (1) explicit CLI flags, (2) process environment variables,
/// (3) JSON config file at <see cref="GetDefaultConfigPath"/>,
/// (4) built-in defaults. See docs/CONFIGURATION.md.
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
    /// Returns the path to the JSON config file used as a fallback when
    /// environment variables are not set. Typically
    /// <c>%LOCALAPPDATA%\d365fo-cli\settings.json</c> on Windows.
    /// </summary>
    public static string GetDefaultConfigPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "d365fo-cli", ConfigFileName);

    /// <summary>
    /// settings.json contents, loaded once per process and cached. Invalidated
    /// by <see cref="SaveJsonConfig"/>. Protected by <see cref="_cacheLock"/>.
    /// </summary>
    private static readonly object _cacheLock = new();
    private static Dictionary<string, string>? jsonConfigCache;

    /// <summary>
    /// Override the config file path. For unit tests only — do not set in
    /// production code. Reset to null and call <see cref="ClearCacheForTests"/>
    /// between tests.
    /// </summary>
    internal static string? ConfigPathOverrideForTests;

    /// <summary>Clear the in-process JSON config cache. For unit tests only.</summary>
    internal static void ClearCacheForTests()
    {
        lock (_cacheLock) { jsonConfigCache = null; }
    }

    private static Dictionary<string, string> GetJsonConfig()
    {
        lock (_cacheLock)
        {
            return jsonConfigCache ??= LoadJsonConfig();
        }
    }

    /// <summary>
    /// Resolve a single configuration value using the standard precedence:
    /// (1) process environment variable, (2) settings.json. Returns null when
    /// the key is set in neither. This is the single entry point every call
    /// site should use so that settings.json is honored consistently.
    /// </summary>
    public static string? Resolve(string key)
    {
        var env = Environment.GetEnvironmentVariable(key);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        var config = GetJsonConfig();
        return config.TryGetValue(key, out var jv) && !string.IsNullOrWhiteSpace(jv)
            ? jv
            : null;
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

    public static D365FoSettings FromEnvironment(string? databaseOverride = null)
    {
        // Env var first, then settings.json fallback — same chain as Resolve.
        static string Env(string k) => Resolve(k) ?? string.Empty;

        var models = Split(Env("D365FO_CUSTOM_MODELS"));
        var langs = Split(Env("D365FO_LABEL_LANGUAGES"));
        if (langs.Count == 0) langs = new[] { "en-us" };

        var db = databaseOverride
                 ?? (string.IsNullOrWhiteSpace(Env("D365FO_INDEX_DB")) ? null : Env("D365FO_INDEX_DB"))
                 ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "d365fo-cli", DefaultDatabaseFile);

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
            CustomPackagesPaths: Split(customPackages));
    }

    /// <summary>
    /// Persists the supplied key/value pairs to the JSON config file,
    /// merging with any values already present. Existing keys are overwritten;
    /// keys absent from <paramref name="values"/> are preserved.
    /// </summary>
    public static void SaveJsonConfig(IReadOnlyDictionary<string, string> values)
    {
        var path = GetDefaultConfigPath();
        var dir  = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var existing = LoadJsonConfig();
        var merged   = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) merged[k] = v;

        File.WriteAllText(path, JsonSerializer.Serialize(merged, D365Json.Pretty));

        // Keep the in-process cache consistent with what we just persisted.
        lock (_cacheLock) { jsonConfigCache = merged; }
    }

    // ---- private helpers -----------------------------------------------------

    private static Dictionary<string, string> LoadJsonConfig()
    {
        var path = ConfigPathOverrideForTests ?? GetDefaultConfigPath();
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

        var config = GetJsonConfig();
        if (config.TryGetValue(preferredKey, out var preferredJson) && !string.IsNullOrWhiteSpace(preferredJson))
            return preferredJson;

        if (config.TryGetValue(deprecatedKey, out var deprecatedJson) && !string.IsNullOrWhiteSpace(deprecatedJson))
            return deprecatedJson;

        return null;
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IReadOnlyList<string> Split(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
