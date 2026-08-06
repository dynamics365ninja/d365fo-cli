using System.Text;
using System.Text.RegularExpressions;
using D365FO.Core.Knowledge;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Eval;

/// <summary>One row of the coverage taxonomy: is this leaf taught, proven and buildable?</summary>
/// <param name="Group">"family" (an AOT object type) or "capability" (a <c>generate</c> subcommand).</param>
/// <param name="Id">Root element for a family, subcommand name for a capability.</param>
/// <param name="KnowledgeTopics">Topic ids that name this leaf, with the literal that matched.</param>
/// <param name="EvalCases">Case ids that produce this leaf and have a reviewed (non-pending) golden.</param>
/// <param name="Tool">A <c>generate</c> subcommand emits this leaf.</param>
public sealed record CoverageLeaf(
    string Group,
    string Id,
    string Label,
    IReadOnlyList<string> KnowledgeTopics,
    IReadOnlyList<string> EvalCases,
    bool Tool,
    string? ToolNote)
{
    public bool Knowledge => KnowledgeTopics.Count > 0;

    public bool Eval => EvalCases.Count > 0;

    /// <summary>The predecessor's definition of done: knowledge teaches ∧ eval proves ∧ tool builds.</summary>
    public bool Complete => Knowledge && Eval && Tool;

    /// <summary>Compact status string: <c>KET</c>, with a dash for each missing leg.</summary>
    public string Status => $"{(Knowledge ? 'K' : '-')}{(Eval ? 'E' : '-')}{(Tool ? 'T' : '-')}";
}

public sealed record CoverageReport(
    IReadOnlyList<CoverageLeaf> Families,
    IReadOnlyList<CoverageLeaf> Capabilities,
    int TotalLeaves,
    int CompleteLeaves)
{
    public IEnumerable<CoverageLeaf> All => Families.Concat(Capabilities);
}

/// <summary>
/// The K ∧ E ∧ T coverage taxonomy, ported from the sibling d365fo-mcp-server
/// repo (audit finding R7): a leaf counts as done only when the knowledge corpus
/// <b>teaches</b> it, an eval case <b>proves</b> it against a reviewed golden, and
/// a <c>generate</c> subcommand <b>builds</b> it. Any one of the three alone is
/// the failure mode the audit kept finding — a family the tool emits that nothing
/// checks (report, workflow), or knowledge asserting an API the tool never
/// exercises.
/// </summary>
/// <remarks>
/// Every leg is <em>derived</em>, never declared: families come from
/// <see cref="ObjectTypeRegistry"/>, capabilities from <see cref="GenerateSurface"/>,
/// E from the case catalog, K from the embedded corpus. A hand-maintained
/// coverage list would drift the same way the four type registries did.
/// </remarks>
public static class EvalCoverage
{
    public static CoverageReport Build(IReadOnlyList<EvalCase> cases, IReadOnlyList<KnowledgeTopic> topics)
    {
        // A pending golden proves nothing: the case has no reviewed oracle yet.
        var proven = cases.Where(c => !c.GoldenPending).ToList();

        var families = ObjectTypeRegistry.All
            .Where(t => t.ExistsInStandardAot)
            .OrderBy(t => t.RootElement, StringComparer.Ordinal)
            .Select(t => new CoverageLeaf(
                Group: "family",
                Id: t.RootElement,
                Label: t.Kind,
                KnowledgeTopics: TopicsNaming(topics, t.RootElement),
                EvalCases: proven
                    .Where(c => c.TargetArtifactTypes.Contains(t.RootElement, StringComparer.OrdinalIgnoreCase))
                    .Select(c => c.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList(),
                Tool: t.GenerateCommand is not null,
                ToolNote: t.GenerateCommand is null ? null : "generate " + t.GenerateCommand))
            .ToList();

        var capabilities = GenerateSurface.All
            .Select(cap => new CoverageLeaf(
                Group: "capability",
                Id: cap.Name,
                Label: cap.Summary,
                KnowledgeTopics: TopicsNamingCapability(topics, cap),
                EvalCases: proven
                    .Where(c => InvokesCapability(c, cap.Name))
                    .Select(c => c.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToList(),
                // A registered subcommand builds by definition; the column exists so a
                // capability row reads the same way a family row does.
                Tool: true,
                ToolNote: cap.Deprecated ? "deprecated alias" : string.Join(", ", cap.Roots)))
            .ToList();

        var all = families.Concat(capabilities).ToList();
        return new CoverageReport(families, capabilities, all.Count, all.Count(l => l.Complete));
    }

    /// <summary>
    /// A case exercises a capability when its canonical args invoke that
    /// subcommand. Agent-only cases (no canonical args) fall back to their tags,
    /// which is how a case that deliberately tests whether an agent reaches a
    /// valid-but-different structure still counts as proof.
    /// </summary>
    private static bool InvokesCapability(EvalCase c, string name)
    {
        if (c.CanonicalArgs is { Count: >= 2 } args)
        {
            return string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase)
                && string.Equals(args[1], name, StringComparison.OrdinalIgnoreCase);
        }

        return c.Tags.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Topics that name anything this case is about — the capability its
    /// canonical args invoke and every artifact type it targets. Each hit
    /// carries the literal that matched, so a proposal built on it can say
    /// <em>why</em> a topic is a candidate instead of asserting relevance.
    /// </summary>
    public static IReadOnlyList<(string TopicId, string Signal)> TopicsForCase(
        EvalCase @case, IReadOnlyList<KnowledgeTopic> topics)
    {
        var hits = new List<(string TopicId, string Signal)>();

        if (@case.CanonicalArgs is { Count: >= 2 } args
            && string.Equals(args[0], "generate", StringComparison.OrdinalIgnoreCase)
            && GenerateSurface.Find(args[1]) is { } cap)
        {
            foreach (var id in TopicsNamingCapability(topics, cap))
                hits.Add((id, $"generate {cap.Name}"));
        }

        foreach (var root in @case.TargetArtifactTypes)
        {
            foreach (var id in TopicsNaming(topics, root))
                hits.Add((id, root));
        }

        return hits
            .GroupBy(h => h.TopicId, StringComparer.Ordinal)
            .Select(g => (TopicId: g.Key, Signal: string.Join(", ", g.Select(h => h.Signal).Distinct(StringComparer.Ordinal))))
            .OrderBy(h => h.TopicId, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> TopicsNaming(IReadOnlyList<KnowledgeTopic> topics, string rootElement) =>
        topics
            .Where(t => Names(t, WordPattern(rootElement)))
            .Select(t => t.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<string> TopicsNamingCapability(IReadOnlyList<KnowledgeTopic> topics, GenerateCapability cap)
    {
        var commandPattern = new Regex(@"\bgenerate\s+" + Regex.Escape(cap.Name) + @"\b", RegexOptions.IgnoreCase);
        return topics
            .Where(t => Names(t, commandPattern))
            .Select(t => t.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Word-bounded so <c>AxTable</c> does not match <c>AxTableExtension</c> —
    /// a substring match would report every extension family as taught by the
    /// table topic and quietly turn the report into a rubber stamp.
    /// </summary>
    private static Regex WordPattern(string literal) => new(@"\b" + Regex.Escape(literal) + @"\b");

    private static bool Names(KnowledgeTopic topic, Regex pattern) =>
        pattern.IsMatch(topic.Body)
        || pattern.IsMatch(topic.Description)
        || (topic.Covers is not null && pattern.IsMatch(topic.Covers));

    /// <summary>
    /// Renders <c>eval/COVERAGE.md</c>. Generated wholesale rather than patched,
    /// so <c>eval coverage --check</c> can diff the file byte for byte the same
    /// way the skills job diffs the emitted skill variants.
    /// </summary>
    public static string RenderMarkdown(CoverageReport report)
    {
        var sb = new StringBuilder();
        sb.Append("<!-- Generated by `d365fo eval coverage --write`. Do not edit by hand. -->\n");
        sb.Append("# Coverage taxonomy — K ∧ E ∧ T\n\n");
        sb.Append("A leaf is *done* only when all three hold:\n\n");
        sb.Append("| Leg | Meaning | Source |\n|---|---|---|\n");
        sb.Append("| **K** | the knowledge corpus teaches it | a `skills/_source/*.md` topic names the AOT type (or the `generate` subcommand) |\n");
        sb.Append("| **E** | an eval case proves it | a case in `eval/cases/` produces it and has a reviewed (non-pending) golden |\n");
        sb.Append("| **T** | the tool builds it | a `generate` subcommand emits it |\n\n");
        sb.Append("Every column is derived — families from `ObjectTypeRegistry`, capabilities from\n");
        sb.Append("`GenerateSurface`, E from the case catalog, K from the embedded corpus — so this\n");
        sb.Append("file cannot claim coverage that no longer exists. Regenerate with\n");
        sb.Append("`d365fo eval coverage --write`; CI runs `--check`.\n\n");

        sb.Append($"**{report.CompleteLeaves} of {report.TotalLeaves} leaves complete.**\n\n");

        sb.Append("## AOT families\n\n");
        sb.Append("| Root element | Kind | K | E | T | Topics | Cases | Built by |\n");
        sb.Append("|---|---|:-:|:-:|:-:|---|---|---|\n");
        foreach (var leaf in report.Families) AppendRow(sb, leaf.Id, leaf.Label, leaf);

        sb.Append("\n## `generate` capabilities\n\n");
        sb.Append("Every subcommand is buildable by definition (T), so the open question per row is\n");
        sb.Append("whether it is taught and proven.\n\n");
        sb.Append("| Subcommand | Emits | K | E | Topics | Cases |\n");
        sb.Append("|---|---|:-:|:-:|---|---|\n");
        foreach (var leaf in report.Capabilities)
        {
            sb.Append($"| `generate {leaf.Id}` | {leaf.ToolNote} | {Mark(leaf.Knowledge)} | {Mark(leaf.Eval)} | ");
            sb.Append($"{Join(leaf.KnowledgeTopics)} | {Join(leaf.EvalCases)} |\n");
        }

        var gaps = report.All.Where(l => !l.Complete && l.Tool).ToList();
        sb.Append("\n## Open gaps\n\n");
        if (gaps.Count == 0)
        {
            sb.Append("None: every leaf the tool can build is both taught and proven.\n");
        }
        else
        {
            sb.Append("Leaves the tool builds but knowledge or evals do not yet cover. Families the tool\n");
            sb.Append("cannot build at all are omitted — they are a generation gap, not a coverage gap.\n\n");
            foreach (var leaf in gaps.OrderBy(l => l.Group, StringComparer.Ordinal).ThenBy(l => l.Id, StringComparer.Ordinal))
            {
                var missing = new List<string>();
                if (!leaf.Knowledge) missing.Add("no topic names it");
                if (!leaf.Eval) missing.Add("no case with a reviewed golden");
                sb.Append($"- **{leaf.Id}** ({leaf.Group}) — {string.Join("; ", missing)}\n");
            }
        }

        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, string id, string label, CoverageLeaf leaf)
    {
        sb.Append($"| `{id}` | {label} | {Mark(leaf.Knowledge)} | {Mark(leaf.Eval)} | {Mark(leaf.Tool)} | ");
        sb.Append($"{Join(leaf.KnowledgeTopics)} | {Join(leaf.EvalCases)} | {leaf.ToolNote ?? "—"} |\n");
    }

    private static string Mark(bool value) => value ? "✅" : "—";

    private static string Join(IReadOnlyList<string> values) =>
        values.Count == 0 ? "—" : string.Join(", ", values.Select(v => "`" + v + "`"));
}
