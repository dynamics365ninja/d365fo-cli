using System.Text.Json;

namespace D365FO.Core.FormPatterns;

/// <summary>
/// One control slot in a registry pattern: what the AOS requires at this position.
/// </summary>
/// <param name="Part">Stable part id ("NavigationList"); empty on the design root.</param>
/// <param name="Alias">Name the AOS uses in its messages ("Navigation List").</param>
/// <param name="Type">Control type ("Group", "Grid", "ActionPane", "QuickFilterControl").</param>
/// <param name="Count">Cardinality as the registry writes it: <c>1</c>, <c>0..1</c>, <c>1..*</c>, <c>0..*</c>.</param>
/// <param name="ExtraChildrenAllowed">The registry's <c>Children="*"</c> — anything else may appear inside.</param>
/// <param name="Properties">Property values the AOS requires on this control.</param>
public sealed record RegisteredPart(
    string Part,
    string? Alias,
    string Type,
    string Count,
    bool ExtraChildrenAllowed,
    IReadOnlyDictionary<string, string> Properties,
    IReadOnlyList<RegisteredPart> Children);

/// <summary>One pattern the AOS will validate a form against.</summary>
/// <param name="Alias">Human-readable name the validator uses in its messages ("Details Master").</param>
/// <param name="Design">The <c>FormDesign</c> slot: its required properties and the parts beneath it.</param>
public sealed record RegisteredFormPattern(
    string Name,
    string Version,
    string? Alias,
    bool Active,
    string? Category,
    RegisteredPart? Design);

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
    /// <remarks>
    /// A pattern carries two version lineages: plain numbers ("1.4") and the older
    /// UX7 series, whose version string is literally "UX7 1.0". Sorting the strings
    /// puts "UX7 …" on top, which would make the newest DetailsMaster look like
    /// UX7 1.2 rather than 1.4 — so the numeric lineage is ranked first, and inside
    /// each lineage the version is compared part by part as numbers.
    /// </remarks>
    public static IReadOnlyList<string> VersionsOf(string name) =>
        All.Where(p => p.Active && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
           .Select(p => p.Version)
           .OrderBy(IsLegacyLineage)
           .ThenByDescending(NumericRank)
           .ToList();

    private static bool IsLegacyLineage(string version) =>
        version.StartsWith("UX7", StringComparison.OrdinalIgnoreCase);

    private static (int Major, int Minor) NumericRank(string version)
    {
        var digits = version.Split(' ').LastOrDefault() ?? version;
        var parts = digits.Split('.');
        _ = int.TryParse(parts.ElementAtOrDefault(0), out var major);
        _ = int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        return (major, minor);
    }

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
                p.TryGetProperty("category", out var c) ? c.GetString() : null,
                p.TryGetProperty("design", out var d) && d.ValueKind == JsonValueKind.Object ? ReadPart(d) : null));
        }

        return result;
    }

    private static RegisteredPart ReadPart(JsonElement e)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (e.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in props.EnumerateObject())
                properties[p.Name] = p.Value.GetString() ?? string.Empty;
        }

        var children = new List<RegisteredPart>();
        if (e.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
        {
            foreach (var k in kids.EnumerateArray()) children.Add(ReadPart(k));
        }

        return new RegisteredPart(
            e.TryGetProperty("part", out var part) ? part.GetString() ?? "" : "",
            e.TryGetProperty("alias", out var alias) ? alias.GetString() : null,
            e.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "",
            e.TryGetProperty("count", out var count) ? count.GetString() ?? "1" : "1",
            e.TryGetProperty("extraChildrenAllowed", out var extra) && extra.GetBoolean(),
            properties,
            children);
    }
}
