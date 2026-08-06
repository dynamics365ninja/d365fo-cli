using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>One structured X++ compiler finding parsed from an xppc log line.</summary>
public sealed record XppcDiagnostic(
    string Severity,
    string? Kind,
    string? Model,
    string? Object,
    string? Member,
    int? Line,
    int? Column,
    string Message)
{
    /// <summary>Known-fix hint for the message, when a <see cref="XppcFixHints"/> rule matches.</summary>
    public string? Hint => XppcDiagnostics.FixHint(Message);

    /// <summary>Id of the matched hint rule, so a corpus/eval run can cluster by cause rather than by message text.</summary>
    public string? HintRule => XppcFixHints.Best(Message)?.RuleId;

    /// <summary>Knowledge topic backing the hint — feed to <c>d365fo knowledge get &lt;id&gt;</c>.</summary>
    public string? Knowledge => XppcFixHints.Best(Message)?.Knowledge;
}

/// <summary>
/// Parser for xppc.exe compiler output — port of the upstream MCP server's
/// structured-diagnostics feature in <c>build_d365fo_project</c>.
///
/// xppc log lines have the form (standalone/UDE mode):
///   Compile Error: Class Method dynamics://MyModel/MyClass/myMethod: [(28,27),(28,28)]: ';' expected.
/// i.e. &lt;severity&gt;: &lt;element kind&gt; dynamics://&lt;model&gt;/&lt;object&gt;[/&lt;member&gt;]: [(line,col)[,(line,col)]]: &lt;message&gt;
/// </summary>
public static class XppcDiagnostics
{
    /// <summary>
    /// Severity prefixes xppc actually emits. The first five were ported from the
    /// upstream MCP server; <c>MetadataProvider Error</c>, <c>Unspecified Fatal
    /// Error</c> and <c>TaskListItem Information</c> were added after the L3 build
    /// oracle's first real run against a D365FO installation produced eight
    /// diagnostics this parser silently discarded — a compile that reported nothing
    /// wrong while the log said <c>Errors: 8</c>.
    /// </summary>
    private const string Severities =
        "Compile Fatal Error|Compile Error|Compile Warning|Generation Warning|Best Practice Warning" +
        "|MetadataProvider Error|Metadata Error|Metadata Warning|Unspecified Fatal Error" +
        "|FormPatternValidation Fatal Error|FormPatternValidation Error|TaskListItem Information" +
        "|ExternalReference Warning|Generation Error|Generation Information";

    /// <summary>
    /// <c>&lt;severity&gt;: [&lt;kind&gt;] dynamics://&lt;a&gt;/&lt;b&gt;[/…]: [(line,col)…]: &lt;message&gt;</c>.
    /// The first URL segment is the model in standalone/UDE logs and the element
    /// kind in the metadata-provider's own diagnostics; the <em>second</em> is the
    /// AOT object name in both, which is what callers join on.
    /// </summary>
    private static readonly Regex DiagLine = new(
        @"^(" + Severities + @"):\s*(?:(.*?)\s+)?dynamics://([^/\s:]+)/([^/\s:]+)(?:/([^\s:]+))?\s*:?\s*\[\((\d+),(\d+)\)(?:,\(\d+,\d+\))?\]\s*:\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>The same, without a source location — how the metadata provider reports shape problems.</summary>
    private static readonly Regex DiagLineNoLocation = new(
        @"^(" + Severities + @"):\s*(?:(.*?)\s+)?dynamics://([^/\s:]+)/([^/\s:]+)(?:/([^\s:]+))?\s*:\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>&lt;severity&gt;: &lt;kind&gt; &lt;Object&gt;/&lt;member&gt;: [(line,col)…]: &lt;message&gt;</c> —
    /// no URL at all, which is how source/XML mismatches are reported.
    /// </summary>
    private static readonly Regex DiagLineBareObject = new(
        @"^(" + Severities + @"):\s*(?:(.*?)\s+)?([A-Za-z_][A-Za-z0-9_]*)/([A-Za-z_][A-Za-z0-9_]*)\s*:\s*\[\((\d+),(\d+)\)(?:,\(\d+,\d+\))?\]\s*:\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>Unspecified Fatal Error: file /Query/Foo</c> — the object is named on the
    /// severity line and the exception text follows on the next lines.
    /// </summary>
    private static readonly Regex FatalFileLine = new(
        @"^(" + Severities + @"):\s*file\s+/([^/\s]+)/(\S+)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// <c>Metadata Error: AxDataEntityView/ConFmVehicleEntity/PrimaryKey: …</c> — the
    /// metadata validator's own shape: MetaModel type, object, then a path to the
    /// offending member, which for a form runs the whole control tree
    /// (<c>Design/Controls/Tab/Controls/…/DataGroup</c>). The type segment must start
    /// with <c>Ax</c>, which is what keeps this from swallowing any message that
    /// happens to contain a slash. Object names may contain dots (an extension object
    /// is <c>Target.Suffix</c>).
    /// </summary>
    private static readonly Regex MetadataMemberLine = new(
        @"^(" + Severities + @"):\s*(Ax[A-Za-z0-9_]*)/([A-Za-z_][A-Za-z0-9_.]*)(?:/([A-Za-z_][A-Za-z0-9_./]*))?\s*:\s*(.*)$",
        RegexOptions.Compiled);

    private static readonly Regex SimpleLine = new(
        @"^(" + Severities + @"):\s*(.+)$",
        RegexOptions.Compiled);

    /// <summary>Stale incremental-build symbols — a full build is needed.</summary>
    public static bool IndicatesStaleSymbols(string logContent) =>
        Regex.IsMatch(logContent, "has not been successfully compiled since it was last changed|Do a Full Build",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// <c>error</c> | <c>warning</c> | <c>information</c>. Only the first stops a
    /// build; <c>TaskListItem Information</c> is a TODO the scaffolder deliberately
    /// left behind, and counting it as a warning would make every generated skeleton
    /// look defective.
    /// </summary>
    private static string Normalize(string severity) =>
        severity.Contains("Error", StringComparison.Ordinal) ? "error"
        : severity.Contains("Warning", StringComparison.Ordinal) ? "warning"
        : "information";

    public static IReadOnlyList<XppcDiagnostic> Parse(string logContent)
    {
        var diagnostics = new List<XppcDiagnostic>();
        foreach (var rawLine in logContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();

            var m = DiagLine.Match(line);
            if (m.Success)
            {
                diagnostics.Add(new XppcDiagnostic(
                    Severity: Normalize(m.Groups[1].Value),
                    Kind: m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value : null,
                    Model: m.Groups[3].Value,
                    Object: m.Groups[4].Value,
                    Member: m.Groups[5].Success && m.Groups[5].Value.Length > 0 ? m.Groups[5].Value : null,
                    Line: int.Parse(m.Groups[6].Value),
                    Column: int.Parse(m.Groups[7].Value),
                    Message: m.Groups[8].Value.Trim()));
                continue;
            }

            var bare = DiagLineBareObject.Match(line);
            if (bare.Success)
            {
                diagnostics.Add(new XppcDiagnostic(
                    Severity: Normalize(bare.Groups[1].Value),
                    Kind: bare.Groups[2].Success && bare.Groups[2].Value.Length > 0 ? bare.Groups[2].Value : null,
                    Model: null,
                    Object: bare.Groups[3].Value,
                    Member: bare.Groups[4].Value,
                    Line: int.Parse(bare.Groups[5].Value),
                    Column: int.Parse(bare.Groups[6].Value),
                    Message: bare.Groups[7].Value.Trim()));
                continue;
            }

            var noLoc = DiagLineNoLocation.Match(line);
            if (noLoc.Success)
            {
                diagnostics.Add(new XppcDiagnostic(
                    Severity: Normalize(noLoc.Groups[1].Value),
                    Kind: noLoc.Groups[2].Success && noLoc.Groups[2].Value.Length > 0 ? noLoc.Groups[2].Value : null,
                    Model: noLoc.Groups[3].Value,
                    Object: noLoc.Groups[4].Value,
                    Member: noLoc.Groups[5].Success && noLoc.Groups[5].Value.Length > 0 ? noLoc.Groups[5].Value : null,
                    Line: null, Column: null,
                    Message: noLoc.Groups[6].Value.Trim()));
                continue;
            }

            var fatalFile = FatalFileLine.Match(line);
            if (fatalFile.Success)
            {
                diagnostics.Add(new XppcDiagnostic(
                    Severity: Normalize(fatalFile.Groups[1].Value),
                    Kind: fatalFile.Groups[2].Value,
                    Model: null,
                    Object: fatalFile.Groups[3].Value,
                    Member: null, Line: null, Column: null,
                    Message: $"{fatalFile.Groups[1].Value} while reading /{fatalFile.Groups[2].Value}/{fatalFile.Groups[3].Value}"));
                continue;
            }

            var metadata = MetadataMemberLine.Match(line);
            if (metadata.Success)
            {
                diagnostics.Add(new XppcDiagnostic(
                    Severity: Normalize(metadata.Groups[1].Value),
                    Kind: metadata.Groups[2].Value,
                    Model: null,
                    Object: metadata.Groups[3].Value,
                    Member: metadata.Groups[4].Success && metadata.Groups[4].Value.Length > 0 ? metadata.Groups[4].Value : null,
                    Line: null, Column: null,
                    Message: metadata.Groups[5].Value.Trim()));
                continue;
            }

            var simple = SimpleLine.Match(line);
            if (simple.Success)
            {
                diagnostics.Add(new XppcDiagnostic(
                    Severity: Normalize(simple.Groups[1].Value),
                    Kind: null, Model: null, Object: null, Member: null, Line: null, Column: null,
                    Message: simple.Groups[2].Value.Trim()));
            }
        }
        return diagnostics;
    }

    /// <summary>
    /// Best known-fix hint for a compiler message, so the agent can correct
    /// everything in one round instead of re-asking. Delegates to the scored
    /// <see cref="XppcFixHints"/> matcher — see that type for why an ordered
    /// substring chain was the wrong shape here.
    /// </summary>
    public static string? FixHint(string message) => XppcFixHints.Best(message)?.Hint;
}
