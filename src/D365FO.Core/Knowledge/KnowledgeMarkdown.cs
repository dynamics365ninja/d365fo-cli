// <copyright file="KnowledgeMarkdown.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Knowledge;

/// <summary>
/// One run of a topic body: either prose or a fenced block, tagged with the <c>##</c> section
/// it sits in so a finding points at a place a human can open.
/// </summary>
/// <param name="Heading">The enclosing <c>##</c> heading, or <c>(intro)</c>.</param>
/// <param name="Language">Fence language, lowercased; empty for prose. A bare fence is <c>text</c>.</param>
/// <param name="Text">The block's content, without the fence markers.</param>
public sealed record KnowledgeBlock(string Heading, string Language, string Text)
{
    /// <summary>True when this run came from a fenced code block.</summary>
    public bool IsFence => Language.Length > 0;

    /// <summary>Location label used in audit findings — <c>§heading · ```xpp</c>.</summary>
    public string Field => $"§{Heading} · {(IsFence ? "```" + Language : "prose")}";
}

/// <summary>
/// Splits a knowledge topic body into prose runs and fenced blocks. Shared by the reference
/// extractor and the example gate so both agree on what counts as code and where it lives.
/// </summary>
public static class KnowledgeMarkdown
{
    /// <summary>Split <paramref name="body"/> in document order. Empty runs are dropped.</summary>
    public static IReadOnlyList<KnowledgeBlock> Blocks(string body)
    {
        var blocks = new List<KnowledgeBlock>();
        var lines = (body ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var heading = "(intro)";
        var buffer = new List<string>();
        string? fenceLang = null;

        void Flush(string language)
        {
            var text = string.Join("\n", buffer);
            buffer.Clear();
            if (text.Trim().Length > 0) blocks.Add(new KnowledgeBlock(heading, language, text));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (fenceLang is null)
                {
                    Flush("");
                    var lang = line[3..].Trim().ToLowerInvariant();
                    fenceLang = lang.Length == 0 ? "text" : lang;
                }
                else
                {
                    Flush(fenceLang);
                    fenceLang = null;
                }
                continue;
            }

            if (fenceLang is null && line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush("");
                heading = line[3..].Trim();
                continue;
            }

            buffer.Add(line);
        }

        // An unterminated fence is a corpus defect; treat the tail as that fence's content so
        // the example gate sees it rather than silently dropping it.
        Flush(fenceLang ?? "");
        return blocks;
    }
}
