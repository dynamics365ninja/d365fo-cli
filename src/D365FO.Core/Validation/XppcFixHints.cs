// <copyright file="XppcFixHints.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>A ranked fix suggestion for one compiler message.</summary>
/// <param name="RuleId">Stable id of the matched rule (e.g. <c>XPPC-UNKNOWN-IDENTIFIER</c>).</param>
/// <param name="Hint">The advice shown to the agent/user.</param>
/// <param name="Score">Match score; higher is a more specific match. See <see cref="XppcFixHints"/>.</param>
/// <param name="Knowledge">Knowledge topic id (<c>d365fo knowledge get &lt;id&gt;</c>) with the background, when one applies.</param>
public sealed record XppcFixHint(string RuleId, string Hint, int Score, string? Knowledge = null);

/// <summary>
/// Scored matcher for xppc compiler messages, replacing the ordered
/// <c>if (message.Contains(...))</c> chain this file's <see cref="XppcDiagnostics.FixHint"/>
/// used to carry. That chain produced false positives the same way upstream
/// <c>d365fo-mcp-server</c>'s did before its own error-hint scoring fix: the first
/// loosely-worded rule to match won, so
/// <list type="bullet">
/// <item><description><c>"The label @SYS1234 does not exist"</c> matched the generic
/// <i>unknown identifier</i> rule (which tests <c>"does not exist"</c>) before ever
/// reaching the label rule, and</description></item>
/// <item><description>any message merely containing the word <c>"label"</c> —
/// <c>"Control 'Label' must be bound"</c> — was answered with label-creation advice.</description></item>
/// </list>
/// Here every rule declares the tokens it <b>requires</b>, tokens that <b>disqualify</b>
/// it, and a specificity weight. All rules are evaluated, the best-scoring one wins,
/// and ties are broken by weight then rule id — so adding a rule can no longer silently
/// shadow an existing one by sitting higher in the file.
/// </summary>
public static class XppcFixHints
{
    /// <summary>
    /// One hint rule. <paramref name="AllOf"/> must all be present (regex, matched
    /// case-insensitively against the message); <paramref name="NoneOf"/> must all be
    /// absent. <paramref name="AnyOf"/>, when non-empty, requires at least one match
    /// and each additional match adds to the score.
    /// </summary>
    internal sealed record Rule(
        string Id,
        string[] AllOf,
        string[] AnyOf,
        string[] NoneOf,
        int Weight,
        string Hint,
        string? Knowledge = null);

    /// <summary>
    /// Rules are ordered by descending <c>Weight</c> for readability only — scoring,
    /// not file order, decides the winner.
    /// </summary>
    internal static readonly Rule[] Rules =
    [
        // --- highly specific: a named subsystem is unambiguously identified -------
        new("XPPC-EXTENSIONOF-INTRINSIC",
            AllOf: [@"does not denote a class"], AnyOf: [], NoneOf: [], Weight: 10,
            Hint: "[ExtensionOf] intrinsic mismatch — use tableStr() for tables, classStr() for classes, formStr() for forms.",
            Knowledge: "coc-extension-authoring"),

        new("XPPC-LABEL-MISSING",
            AllOf: [@"\blabel\b"],
            AnyOf: [@"@[A-Za-z]{3}\d+", @"does not exist", @"not exist", @"\bunknown\b", @"could not be found", @"\binvalid\b"],
            // "Label" as a *property* name, or a control's Label — nothing to create.
            NoneOf: [@"\bproperty\b", @"\bcontrol\b", @"must be (bound|set)"],
            Weight: 9,
            Hint: "Label id missing. Find it with `d365fo search label \"<text>\"` or create it via `d365fo label create`.",
            Knowledge: "label-translation"),

        new("XPPC-FINAL-NOT-WRAPPABLE",
            AllOf: [@"\bfinal\b"], AnyOf: [@"\bextend", @"\bwrap", @"\bderive"], NoneOf: [], Weight: 9,
            Hint: "The base class/method is final — CoC needs [Wrappable(true)] on the method, or use an event handler instead.",
            Knowledge: "coc-extension-authoring"),

        new("XPPC-METHOD-MISSING",
            AllOf: [], AnyOf: [@"is not a valid method", @"method .* not found", @"no such method", @"does not contain a definition for"],
            NoneOf: [], Weight: 8,
            Hint: "Method missing on the type. Check the real method list with `d365fo get class <name>` or `d365fo get table <name>`.",
            Knowledge: "x++-class-authoring"),

        new("XPPC-ARITY-MISMATCH",
            AllOf: [], AnyOf: [@"number of arguments", @"argument count", @"no overload .* takes"], NoneOf: [], Weight: 8,
            Hint: "Argument count mismatch — `d365fo validate references` reports the indexed signature arity before the compiler does.",
            Knowledge: "x++-class-authoring"),

        new("XPPC-STALE-SYMBOLS",
            AllOf: [], AnyOf: [@"has not been successfully compiled since it was last changed", @"do a full build"],
            NoneOf: [], Weight: 8,
            Hint: "Stale incremental-build symbols — rebuild the whole package (`d365fo build --full`) before trusting further errors."),

        new("XPPC-MODEL-REFERENCE",
            AllOf: [], AnyOf: [@"is not referenced", @"missing (a )?reference", @"add a reference to"], NoneOf: [], Weight: 8,
            Hint: "The owning model is not referenced by the compiling model. Add the dependency in the model descriptor — check it with `d365fo models deps <model>`.",
            Knowledge: "model-dependency-and-coupling"),

        // --- medium: identifier-shaped, but only after the label rule can't claim it
        new("XPPC-UNKNOWN-IDENTIFIER",
            AllOf: [],
            AnyOf: [@"unknown type", @"could not be found", @"does not exist", @"is not declared", @"undefined (symbol|variable)"],
            // A missing label is also "does not exist" — let XPPC-LABEL-MISSING take those.
            NoneOf: [@"\blabel\b"],
            Weight: 6,
            Hint: "The identifier does not exist in metadata. Verify it with `d365fo search any <name>` / `d365fo validate references` — never guess names."),

        new("XPPC-TYPE-MISMATCH",
            AllOf: [], AnyOf: [@"cannot (be )?convert", @"type mismatch", @"is not compatible with"], NoneOf: [], Weight: 6,
            Hint: "Type mismatch — check the field/EDT type with `d365fo get table <name>` or `d365fo get edt <name>` rather than assuming str/int."),

        // --- low: syntax catch-alls, deliberately last-resort ---------------------
        new("XPPC-MISSING-SEMICOLON",
            AllOf: [@"';' expected"], AnyOf: [], NoneOf: [], Weight: 4,
            Hint: "Missing semicolon — check the statement at the reported line/column."),

        new("XPPC-SYNTAX",
            AllOf: [@"\bexpected\b"], AnyOf: [@"but found", @"unexpected"], NoneOf: [@"';' expected"], Weight: 2,
            Hint: "Syntax error — re-check X++ syntax at the reported position (common after editing CDATA method bodies).",
            Knowledge: "xpp-statement-and-type-rules"),
    ];

    /// <summary>
    /// Score every rule against <paramref name="message"/> and return the matches,
    /// best first. Empty when nothing matches — an unrecognised message gets no hint
    /// rather than the nearest-looking one.
    /// </summary>
    public static IReadOnlyList<XppcFixHint> Match(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return Array.Empty<XppcFixHint>();

        var hits = new List<XppcFixHint>();
        foreach (var rule in Rules)
        {
            if (rule.NoneOf.Any(p => IsMatch(message, p))) continue;
            if (!rule.AllOf.All(p => IsMatch(message, p))) continue;

            var anyHits = rule.AnyOf.Count(p => IsMatch(message, p));
            if (rule.AnyOf.Length > 0 && anyHits == 0) continue;
            if (rule.AllOf.Length == 0 && rule.AnyOf.Length == 0) continue;

            // Weight dominates; extra AllOf/AnyOf matches break ties between rules of
            // equal weight in favour of the one that matched more of the message.
            var score = (rule.Weight * 10) + rule.AllOf.Length + anyHits;
            hits.Add(new XppcFixHint(rule.Id, rule.Hint, score, rule.Knowledge));
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The single best hint for a message, or <c>null</c> when no rule matches.</summary>
    public static XppcFixHint? Best(string? message) => Match(message).FirstOrDefault();

    private static bool IsMatch(string message, string pattern) =>
        Regex.IsMatch(message, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
