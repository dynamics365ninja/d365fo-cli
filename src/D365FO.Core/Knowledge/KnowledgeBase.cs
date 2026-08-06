// <copyright file="KnowledgeBase.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace D365FO.Core.Knowledge;

/// <summary>One retrievable knowledge topic — a single <c>skills/_source/*.md</c> document.</summary>
public sealed record KnowledgeTopic(
    string Id,
    string Description,
    string? AppliesWhen,
    string Body,
    string? Covers = null)
{
    /// <summary>The <c>##</c>-level sections of <see cref="Body"/>, in document order.</summary>
    public IReadOnlyList<KnowledgeSection> Sections => KnowledgeBase.SplitSections(Body);

    /// <summary>Approximate token cost of returning the whole body (~4 chars/token).</summary>
    public int ApproxTokens => (Body.Length + 3) / 4;
}

/// <summary>A <c>##</c>-delimited slice of a topic, so callers can pull one section instead of the whole doc.</summary>
public sealed record KnowledgeSection(string Heading, string Text)
{
    public int ApproxTokens => (Text.Length + 3) / 4;
}

/// <summary>One scored hit from <see cref="KnowledgeBase.Search"/>.</summary>
public sealed record KnowledgeHit(string TopicId, string Description, string Heading, int Score, string Excerpt);

/// <summary>
/// The CLI's answer to upstream <c>d365fo-mcp-server</c>'s <c>get_knowledge</c> tool.
///
/// Upstream keeps its X++/D365FO knowledge as a server-side topic store queried at
/// runtime. This repo already maintains the same knowledge as the single-source
/// skill corpus in <c>skills/_source/*.md</c> (emitted to the Copilot / Anthropic /
/// d365fo-cli variants by <c>scripts/emit-skills.ps1</c>), verified against a live
/// D365FO dev VM. Rather than forking a second copy of that content, this class
/// embeds those exact files into the assembly and serves them, so:
/// <list type="bullet">
/// <item><description>an agent that cannot load the skill files (bare terminal, MCP
/// client without skill support) can still ask for a topic on demand, and</description></item>
/// <item><description>there is still exactly one place to correct a fact — the
/// <c>skills/_source</c> document.</description></item>
/// </list>
/// Retrieval is section-aware: <see cref="Search"/> ranks individual <c>##</c>
/// sections so a caller pays for the paragraph it needs rather than a 400-line doc.
/// </summary>
public static class KnowledgeBase
{
    private const string ResourcePrefix = "D365FO.Core.Knowledge.";

    private static readonly Lazy<IReadOnlyList<KnowledgeTopic>> TopicsLazy = new(LoadTopics);

    /// <summary>All embedded topics, ordered by id.</summary>
    public static IReadOnlyList<KnowledgeTopic> Topics => TopicsLazy.Value;

    /// <summary>Look up a topic by exact id, then by unique prefix/substring of the id.</summary>
    public static KnowledgeTopic? Get(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var needle = id.Trim();
        var exact = Topics.FirstOrDefault(t => string.Equals(t.Id, needle, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var partial = Topics.Where(t => t.Id.Contains(needle, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : null;
    }

    /// <summary>Ids whose name is close to <paramref name="id"/> — for a "did you mean" hint on a miss.</summary>
    public static IReadOnlyList<string> Suggest(string? id, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(id)) return Topics.Select(t => t.Id).Take(limit).ToList();
        var terms = Tokenize(id);
        return Topics
            .Select(t => (t.Id, Score: terms.Count(term => t.Id.Contains(term, StringComparison.OrdinalIgnoreCase))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Id, StringComparer.Ordinal)
            .Take(limit)
            .Select(x => x.Id)
            .ToList();
    }

    /// <summary>
    /// Rank <c>##</c> sections across every topic against a free-text query. A term
    /// counts more in a heading than in body text, and the topic description is
    /// folded in so intent-style queries ("how do I add a field to a table") still
    /// land on the right document.
    /// </summary>
    public static IReadOnlyList<KnowledgeHit> Search(string query, int limit = 10, string? topicId = null)
    {
        var terms = Tokenize(query);
        if (terms.Count == 0) return Array.Empty<KnowledgeHit>();

        var scope = topicId is null ? Topics : Topics.Where(t => t.Id.Contains(topicId, StringComparison.OrdinalIgnoreCase));
        var hits = new List<KnowledgeHit>();

        foreach (var topic in scope)
        {
            var descHits = terms.Count(t => topic.Description.Contains(t, StringComparison.OrdinalIgnoreCase));
            var idHits = terms.Count(t => topic.Id.Contains(t, StringComparison.OrdinalIgnoreCase));
            foreach (var section in topic.Sections)
            {
                var score = idHits * 4 + descHits * 2;
                foreach (var term in terms)
                {
                    if (section.Heading.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 6;
                    score += Math.Min(CountOccurrences(section.Text, term), 5);
                }
                if (score <= 0) continue;
                hits.Add(new KnowledgeHit(topic.Id, topic.Description, section.Heading, score, Excerpt(section.Text, terms)));
            }
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.TopicId, StringComparer.Ordinal)
            .Take(Math.Max(1, limit))
            .ToList();
    }

    /// <summary>Split a topic body on <c>##</c> headings. Text before the first <c>##</c> becomes the "(intro)" section.</summary>
    internal static IReadOnlyList<KnowledgeSection> SplitSections(string body)
    {
        var sections = new List<KnowledgeSection>();
        var lines = body.Replace("\r\n", "\n").Split('\n');
        var heading = "(intro)";
        var buffer = new StringBuilder();
        var inFence = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal)) inFence = !inFence;

            // A '##' inside a fenced block is a shell comment, not a heading.
            if (!inFence && line.StartsWith("## ", StringComparison.Ordinal))
            {
                var text = buffer.ToString().Trim();
                if (text.Length > 0) sections.Add(new KnowledgeSection(heading, text));
                heading = line[3..].Trim();
                buffer.Clear();
                continue;
            }
            buffer.Append(line).Append('\n');
        }

        var tail = buffer.ToString().Trim();
        if (tail.Length > 0) sections.Add(new KnowledgeSection(heading, tail));
        return sections;
    }

    private static IReadOnlyList<KnowledgeTopic> LoadTopics()
    {
        var asm = typeof(KnowledgeBase).Assembly;
        var topics = new List<KnowledgeTopic>();

        foreach (var resource in asm.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) ||
                !resource.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            using var stream = asm.GetManifestResourceStream(resource);
            if (stream is null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var raw = reader.ReadToEnd();

            var fallbackId = resource[ResourcePrefix.Length..^".md".Length];
            topics.Add(Parse(raw, fallbackId));
        }

        return topics.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Parse the same frontmatter <c>scripts/emit-skills.py</c> reads (<c>id</c>,
    /// <c>description</c>, <c>appliesWhen</c>, <c>covers</c>). List-valued keys such as
    /// <c>applyTo</c> are Copilot-only and deliberately ignored here.
    /// </summary>
    internal static KnowledgeTopic Parse(string raw, string fallbackId)
    {
        var text = raw.Replace("\r\n", "\n");
        string id = fallbackId, description = "", appliesWhen = "", covers = "";
        var body = text;

        if (text.StartsWith("---\n", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var front = text[4..end];
                body = text[(end + 4)..].TrimStart('\n');
                foreach (var line in front.Split('\n'))
                {
                    var m = Regex.Match(line, @"^(id|description|appliesWhen|covers)\s*:\s*(.+)$");
                    if (!m.Success) continue;
                    var value = m.Groups[2].Value.Trim().Trim('"').Trim('\'');
                    switch (m.Groups[1].Value)
                    {
                        case "id": id = value; break;
                        case "description": description = value; break;
                        case "appliesWhen": appliesWhen = value; break;
                        case "covers": covers = value; break;
                    }
                }
            }
        }

        return new KnowledgeTopic(
            id,
            description,
            appliesWhen.Length > 0 ? appliesWhen : null,
            body.Trim(),
            covers.Length > 0 ? covers : null);
    }

    private static List<string> Tokenize(string query) =>
        Regex.Matches(query ?? string.Empty, @"[A-Za-z0-9_+#]{3,}")
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "how", "does", "can", "you", "use", "using", "that",
        "this", "what", "when", "should", "from", "into", "are", "was", "not", "but",
    };

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    /// <summary>A ~400-char window around the first query-term hit, so search output stays cheap.</summary>
    private static string Excerpt(string text, IReadOnlyList<string> terms)
    {
        const int Window = 400;
        var at = -1;
        foreach (var term in terms)
        {
            var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && (at < 0 || i < at)) at = i;
        }
        if (at < 0) at = 0;

        var start = Math.Max(0, at - Window / 4);
        var length = Math.Min(Window, text.Length - start);
        var slice = text.Substring(start, length).Trim();
        if (start > 0) slice = "…" + slice;
        if (start + length < text.Length) slice += "…";
        return slice;
    }
}
