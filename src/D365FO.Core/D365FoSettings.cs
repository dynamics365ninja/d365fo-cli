namespace D365FO.Core;

/// <summary>
/// Resolved runtime configuration. Values are sourced, in order:
/// (1) explicit CLI flags, (2) process environment, (3) optional JSON profile,
/// (4) built-in defaults. See docs/CONFIGURATION.md.
/// </summary>
public sealed record D365FoSettings(
    string? PackagesPath,
    string? WorkspacePath,
    string DatabasePath,
    IReadOnlyList<string> CustomModels,
    IReadOnlyList<string> LabelLanguages)
{
    public const string DefaultDatabaseFile = "d365fo-index.sqlite";

    public static D365FoSettings FromEnvironment(string? databaseOverride = null)
    {
        string Env(string k) => Environment.GetEnvironmentVariable(k) ?? string.Empty;

        var models = Split(Env("D365FO_CUSTOM_MODELS"));
        var langs = Split(Env("D365FO_LABEL_LANGUAGES"));
        if (langs.Count == 0) langs = new[] { "en-us" };

        var db = databaseOverride
                 ?? (string.IsNullOrWhiteSpace(Env("D365FO_INDEX_DB")) ? null : Env("D365FO_INDEX_DB"))
                 ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "d365fo-cli", DefaultDatabaseFile);

        return new D365FoSettings(
            PackagesPath: NullIfEmpty(Env("D365FO_PACKAGES_PATH")),
            WorkspacePath: NullIfEmpty(Env("D365FO_WORKSPACE_PATH")),
            DatabasePath: db,
            CustomModels: models,
            LabelLanguages: langs);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static IReadOnlyList<string> Split(string s) =>
        string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
