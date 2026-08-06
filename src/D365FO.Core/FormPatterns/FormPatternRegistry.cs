using System.Text.Json;

namespace D365FO.Core.FormPatterns;

/// <summary>One pattern the AOS will validate a form against.</summary>
/// <param name="Alias">Human-readable name the validator uses in its messages ("Details Master").</param>
public sealed record RegisteredFormPattern(
    string Name,
    string Version,
    string? Alias,
    bool Active,
    string? Category);

/// <summary>
/// The AOT form-pattern registry, derived from
/// <c>Microsoft.Dynamics.AX.Metadata.Patterns.dll</c> by
/// <c>scripts/emit-form-patterns.ps1</c> and embedded so it can be consulted with no
/// D365FO installation present.
/// </summary>
/// <remarks>
/// <para>
/// This is <em>not</em> the same thing as <see cref="FormPatternCatalog"/>. That is
/// this repo's own model of the patterns, used by FP001–FP010 to check a form before
/// it is written. This is the list the AOS itself validates against, and the two do
/// not currently agree — a disagreement only <c>eval verify-build</c> could see,
/// because the AOS reports it as <c>FormPatternValidation Error</c> at compile time:
/// "Unable to validate pattern 'DetailsMaster 1.1'. Message: Pattern
/// 'DetailsMaster 1.1' not found."
/// </para>
/// <para>
/// Naming a pattern that is not here is unambiguous breakage, so it is worth checking
/// offline even before the two models are reconciled — see
/// <c>FormTemplatePatternRegistryTests</c>, which pins every template's design pattern
/// against this list and carries the reviewed exceptions.
/// </para>
/// </remarks>
public static class FormPatternRegistry
{
    private const string ResourceName = "D365FO.Core.FormPatterns.form-patterns.json";

    private static readonly Lazy<IReadOnlyList<RegisteredFormPattern>> PatternsLazy = new(Load);

    /// <summary>Every pattern the registry declares, including inactive and superseded versions.</summary>
    public static IReadOnlyList<RegisteredFormPattern> All => PatternsLazy.Value;

    /// <summary>True when this exact name+version pair exists and is active.</summary>
    public static bool Exists(string name, string version) =>
        Find(name, version) is { Active: true };

    public static RegisteredFormPattern? Find(string name, string version) =>
        All.FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Version, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>Active versions of a pattern name, newest first — the "did you mean" list on a miss.</summary>
    public static IReadOnlyList<string> VersionsOf(string name) =>
        All.Where(p => p.Active && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
           .Select(p => p.Version)
           .OrderByDescending(v => v, StringComparer.Ordinal)
           .ToList();

    private static IReadOnlyList<RegisteredFormPattern> Load()
    {
        using var stream = typeof(FormPatternRegistry).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");

        using var doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("patterns", out var patterns))
            return [];

        var result = new List<RegisteredFormPattern>();
        foreach (var p in patterns.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = p.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version)) continue;

            result.Add(new RegisteredFormPattern(
                name,
                version,
                p.TryGetProperty("alias", out var a) ? a.GetString() : null,
                !p.TryGetProperty("active", out var act) || act.GetBoolean(),
                p.TryGetProperty("category", out var c) ? c.GetString() : null));
        }

        return result;
    }
}
