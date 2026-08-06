// <copyright file="RuleCanon.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text;
using System.Text.RegularExpressions;

namespace D365FO.Core.Knowledge;

/// <summary>One canonical rule block, lifted from the topic that owns it.</summary>
/// <param name="Id">The canon id, as written in <c>&lt;!-- canon:&lt;id&gt; --&gt;</c>.</param>
/// <param name="TopicId">The <c>skills/_source</c> topic the block lives in.</param>
/// <param name="Text">The block body, verbatim.</param>
public sealed record RuleCanonBlock(string Id, string TopicId, string Text);

/// <summary>
/// The X++ rule canon, single-sourced from <c>skills/_source</c>.
///
/// The same rules used to be written out three times — in the agent system prompt, in the
/// MCP server's instructions, and in the skill files — which is how a rule gets corrected in
/// one place and silently kept wrong in the other two (audit finding K1). Here the rule text
/// lives in the topic that explains it, fenced by
/// <c>&lt;!-- canon:&lt;id&gt; --&gt;</c> … <c>&lt;!-- /canon --&gt;</c>, and every consumer
/// composes its output from these blocks.
///
/// The blocks are read from the <b>already embedded</b> topic corpus
/// (<see cref="KnowledgeBase"/>) rather than from a generated side-artifact, so drift between
/// the canon and the topics is not merely detected — it cannot be represented.
/// </summary>
public static class RuleCanon
{
    private static readonly Regex Fence = new(
        @"<!--\s*canon:(?<id>[a-z0-9-]+)\s*-->\r?\n(?<body>.*?)\r?\n<!--\s*/canon\s*-->",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Lazy<IReadOnlyList<RuleCanonBlock>> BlocksLazy = new(Load);

    /// <summary>Every canon block in the corpus, ordered by id.</summary>
    public static IReadOnlyList<RuleCanonBlock> Blocks => BlocksLazy.Value;

    /// <summary>The block with this id, or null when the corpus declares none.</summary>
    public static RuleCanonBlock? Find(string id) =>
        Blocks.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// The text of the block with this id. Throws when it is missing: a consumer asking for a
    /// canon id that no topic declares would otherwise ship a system prompt with a silently
    /// empty rule section, which is exactly the failure this class exists to prevent.
    /// </summary>
    public static string Require(string id) =>
        Find(id)?.Text ?? throw new InvalidOperationException(
            $"No canon block '{id}' in skills/_source. Declared ids: {string.Join(", ", Blocks.Select(b => b.Id))}.");

    /// <summary>
    /// A compact digest of the whole canon, for consumers that publish one block of guidance
    /// rather than a structured document — the MCP <c>initialize</c> instructions.
    /// </summary>
    public static string Digest(IEnumerable<(string Id, string Heading)> sections)
    {
        var sb = new StringBuilder();
        foreach (var (id, heading) in sections)
        {
            sb.Append("## ").AppendLine(heading).AppendLine();
            sb.AppendLine(Require(id)).AppendLine();
        }
        return sb.ToString().TrimEnd() + "\n";
    }

    private static IReadOnlyList<RuleCanonBlock> Load()
    {
        var blocks = new List<RuleCanonBlock>();
        foreach (var topic in KnowledgeBase.Topics)
        {
            foreach (Match m in Fence.Matches(topic.Body))
            {
                blocks.Add(new RuleCanonBlock(m.Groups["id"].Value, topic.Id, m.Groups["body"].Value.Trim()));
            }
        }

        var duplicate = blocks.GroupBy(b => b.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Canon id '{duplicate.Key}' is declared by more than one topic ({string.Join(", ", duplicate.Select(b => b.TopicId))}). " +
                "A rule has exactly one home.");
        }

        return blocks.OrderBy(b => b.Id, StringComparer.Ordinal).ToList();
    }
}
