// <copyright file="BpMonikerCatalog.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace D365FO.Core.Knowledge;

/// <summary>One Best-Practice rule moniker, as the installation declares it.</summary>
/// <param name="Name">Exact moniker spelling. Case matters to xppbp and to a suppression file.</param>
/// <param name="Canonical">
/// True when some model's <c>AxRuleSet/BPRules.xml</c> lists it. This is the ONLY field that
/// answers "is this a real BP rule": the message extraction also yields strings belonging to the
/// upgrade and form-conversion tooling, which are not rules.
/// </param>
/// <param name="Message">The rule's message template, with <c>{0}</c>-style placeholders. Null when the install ships none.</param>
/// <param name="Description">Longer text, where the rule author wrote one.</param>
public sealed record BpMoniker(
    string Name,
    bool Canonical,
    string? Message = null,
    string? Description = null);

/// <summary>The catalog as stored on disk.</summary>
/// <param name="CapturedAt">When the snapshot was taken.</param>
/// <param name="PackagesPath">Installation it was taken from.</param>
/// <param name="Monikers">Every entry, canonical or not.</param>
public sealed record BpMonikerSnapshot(
    DateTimeOffset CapturedAt,
    string? PackagesPath,
    IReadOnlyList<BpMoniker> Monikers);

/// <summary>
/// Answers "is this a real BP moniker", "which rule covers this scenario", and "render me a
/// suppression block" from names extracted out of a real D365FO install.
/// </summary>
/// <remarks>
/// <para>
/// Every moniker has been guessed wrong at least once. A PascalCase name that reads exactly like
/// a rule — <c>BPCheckNamingConventions</c> — is not one, while
/// <c>BPErrorPrivilegeNotCoveredByDuty</c> is; nothing about the spelling tells them apart, and a
/// suppression naming a moniker that does not exist suppresses nothing while looking deliberate.
/// So this never infers: a name is real because an installation listed it, or it is not in the
/// catalog.
/// </para>
/// <para>
/// Not every real moniker starts with <c>BP</c> — the shipped suppression files use
/// <c>MetadataExtensionNamingWithExtensionOnly</c> among others — so a prefix test is not a
/// substitute for a lookup.
/// </para>
/// </remarks>
public static class BpMonikerCatalog
{
    /// <summary>Point at a snapshot captured from this instance's own D365FO version.</summary>
    public const string PathEnvVar = "D365FO_BP_CATALOG_PATH";

    private static readonly Lazy<BpMonikerSnapshot> Loaded = new(Load, isThreadSafe: true);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The active snapshot: the file named by <see cref="PathEnvVar"/>, else the shipped one.</summary>
    public static BpMonikerSnapshot Snapshot => Loaded.Value;

    private static BpMonikerSnapshot Load()
    {
        var overridePath = D365FoSettings.Resolve(PathEnvVar);
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            try
            {
                var json = File.ReadAllText(overridePath!);
                var parsed = JsonSerializer.Deserialize<BpMonikerSnapshot>(json, JsonOptions);
                if (parsed is not null) return parsed;
            }
            catch
            {
                // An unreadable override falls back to the shipped snapshot rather than leaving
                // the caller with no catalog at all — but see EmptySnapshot on why "empty" is
                // never silently treated as "this moniker does not exist".
            }
        }

        return LoadEmbedded() ?? EmptySnapshot;
    }

    private static BpMonikerSnapshot? LoadEmbedded()
    {
        var assembly = typeof(BpMonikerCatalog).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("bp-monikers.json", StringComparison.OrdinalIgnoreCase));
        if (resource is null) return null;

        using var stream = assembly.GetManifestResourceStream(resource);
        if (stream is null) return null;
        try
        {
            return JsonSerializer.Deserialize<BpMonikerSnapshot>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static BpMonikerSnapshot EmptySnapshot { get; } =
        new(DateTimeOffset.MinValue, null, Array.Empty<BpMoniker>());

    /// <summary>Is the catalog populated at all? An empty one cannot refute a moniker.</summary>
    public static bool IsPopulated => Snapshot.Monikers.Count > 0;

    /// <summary>The entry for <paramref name="name"/>, or null. Exact match — case included.</summary>
    /// <remarks>
    /// Deliberately case-SENSITIVE. xppbp and the suppression reader match the moniker exactly,
    /// so reporting <c>bperrorprivilegenotcoveredbyduty</c> as valid would hand back a name that
    /// suppresses nothing. A near-miss on case is surfaced through <see cref="Search"/> instead.
    /// </remarks>
    public static BpMoniker? Find(string name) =>
        Snapshot.Monikers.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));

    /// <summary>Entries whose name or message mentions every whitespace-separated term.</summary>
    /// <remarks>
    /// Terms are ANDed and matched case-insensitively against name, message and description. A
    /// scenario is described in words the rule name does not contain ("privilege not in a duty"),
    /// which is why the message text is searched and not only the name.
    /// </remarks>
    public static IReadOnlyList<BpMoniker> Search(string query, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<BpMoniker>();

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(BpMoniker m)
        {
            var haystack = m.Name + " " + (m.Message ?? "") + " " + (m.Description ?? "");
            return terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        return Snapshot.Monikers
            .Where(Matches)
            // Canonical rules first: a caller searching for a rule wants rules, and the
            // non-canonical entries are resource strings that merely came along for the ride.
            .OrderByDescending(m => m.Canonical)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, 200))
            .ToList();
    }

    /// <summary>Names differing from <paramref name="name"/> only by case — the likeliest typo.</summary>
    public static IReadOnlyList<string> CaseVariants(string name) =>
        Snapshot.Monikers
            .Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(m.Name, name, StringComparison.Ordinal))
            .Select(m => m.Name)
            .ToList();

    /// <summary>
    /// A <c>&lt;Diagnostic&gt;</c> block for a model's <c>*_BPSuppressions.xml</c>.
    /// </summary>
    /// <remarks>
    /// The element order and names are taken from the shipped
    /// <c>AxIgnoreDiagnosticList/&lt;Model&gt;_BPSuppressions.xml</c> files, not invented:
    /// DiagnosticType, Severity, Path, Moniker, Message, Justification. The <c>Path</c> is a
    /// <c>dynamics://</c> URI naming the exact element the rule fired on.
    /// </remarks>
    public static string SuppressionBlock(
        string moniker, string path, string? message = null, string? justification = null, string severity = "Warning")
    {
        static string Escape(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        var text = message ?? Find(moniker)?.Message ?? moniker;
        var why = justification ?? "TODO: say why this rule does not apply here.";

        return string.Join(Environment.NewLine,
            "<Diagnostic>",
            "\t<DiagnosticType>BestPractices</DiagnosticType>",
            $"\t<Severity>{Escape(severity)}</Severity>",
            $"\t<Path>{Escape(path)}</Path>",
            $"\t<Moniker>{Escape(moniker)}</Moniker>",
            $"\t<Message>{Escape(text)}</Message>",
            $"\t<Justification>{Escape(why)}</Justification>",
            "</Diagnostic>");
    }

    /// <summary>Serialise a snapshot for <c>bp-moniker extract</c>.</summary>
    public static string ToJson(BpMonikerSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, JsonOptions);
}
