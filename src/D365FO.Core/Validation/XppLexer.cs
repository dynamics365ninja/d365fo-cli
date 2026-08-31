// <copyright file="XppLexer.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Validation;

/// <summary>Kind of a masked region.</summary>
public enum XppSpanKind
{
    /// <summary>A string literal (either quote style, verbatim or not).</summary>
    String,

    /// <summary>A <c>//</c> comment running to end of line.</summary>
    LineComment,

    /// <summary>A <c>/* … */</c> block comment.</summary>
    BlockComment,
}

/// <summary>One masked region of the source.</summary>
/// <param name="Start">Offset of the first character of the span (the opening delimiter).</param>
/// <param name="End">Offset one past the last character of the span.</param>
/// <param name="Kind">What the span is.</param>
/// <param name="Quote">For strings: the quote character used.</param>
/// <param name="Verbatim">For strings: true when the literal was introduced with <c>@</c>.</param>
public sealed record XppSpan(int Start, int End, XppSpanKind Kind, char? Quote = null, bool Verbatim = false);

/// <summary>Result of an <see cref="XppLexer.Scan"/>.</summary>
/// <param name="Masked">Source with literal/comment CONTENT replaced by spaces; same length, same newlines.</param>
/// <param name="Spans">The regions that were masked.</param>
public sealed record XppScan(string Masked, IReadOnlyList<XppSpan> Spans);

/// <summary>
/// Shared X++ lexer-lite: the single place that knows where a string literal or a
/// comment starts and ends. Port of the upstream MCP server's <c>src/utils/xppLexer.ts</c>.
///
/// Every keyword/regex scan should run against a *masked* copy of the source so that a
/// keyword inside a literal cannot fire a rule. Upstream measured, on 7,649 shipped AOT
/// files, that a masker recognising only double-quoted strings produced every
/// error-severity false positive its validator had:
/// <code>
///   strFind(text, ',', 1, len)        → FN001 "expects 4 arguments" (the ',' counted)
///   '????????-????-…' (a GUID mask)   → CS001 "?? is C#"
///   ' LEFT JOIN %2 T2 ON …' (SQL)     → SEL007 "left join is not X++"
/// </code>
///
/// X++ literal rules this implements (verified upstream against xppc 7.0.7996.33):
/// <list type="bullet">
/// <item><c>"…"</c> and <c>'…'</c> are both string literals; <c>\</c> escapes the next character.</item>
/// <item><c>@"…"</c> / <c>@'…'</c> are verbatim: the backslash is an ordinary character, the
/// literal may span lines, and it ends at the next matching quote.</item>
/// <item><c>//</c> to end of line, and <c>/* … */</c> block comments (<c>###</c> and <c>///</c>
/// are ordinary text to the lexer; <c>///</c> is a doc comment and callers that care about it
/// check the prefix themselves).</item>
/// </list>
///
/// Masking preserves the length and every newline, so line numbers and offsets taken from the
/// masked text address the original source. Delimiters (the quotes, the <c>//</c> and
/// <c>/*</c>) survive; only the CONTENT becomes spaces, which is what lets a rule still see
/// that a call had a string argument at all.
///
/// This is deliberately not a tokenizer and not a parser: rules stay regex-based.
/// </summary>
public static class XppLexer
{
    /// <summary>Scan X++ source, returning the masked copy plus the spans that were masked.</summary>
    public static XppScan Scan(string code)
    {
        var output = code.ToCharArray();
        var spans = new List<XppSpan>();
        int n = code.Length;
        int i = 0;

        while (i < n)
        {
            char c = code[i];
            char c2 = i + 1 < n ? code[i + 1] : '\0';

            // Line comment — content to end of line.
            if (c == '/' && c2 == '/')
            {
                int start = i;
                i += 2;
                while (i < n && code[i] != '\n')
                {
                    output[i] = ' ';
                    i++;
                }

                spans.Add(new XppSpan(start, i, XppSpanKind.LineComment));
                continue;
            }

            // Block comment — content up to the closing marker (both markers blanked as
            // before, so a stray */ cannot look like an operator).
            if (c == '/' && c2 == '*')
            {
                int start = i;
                i += 2;
                while (i < n && !(code[i] == '*' && i + 1 < n && code[i + 1] == '/'))
                {
                    if (code[i] != '\n')
                    {
                        output[i] = ' ';
                    }

                    i++;
                }

                if (i < n)
                {
                    output[i] = ' ';
                    if (i + 1 < n)
                    {
                        output[i + 1] = ' ';
                    }

                    i += 2;
                }

                spans.Add(new XppSpan(start, Math.Min(i, n), XppSpanKind.BlockComment));
                continue;
            }

            // Verbatim string: @"…" or @'…' — no escape processing.
            if (c == '@' && (c2 == '"' || c2 == '\''))
            {
                char quote = c2;
                int start = i;
                i += 2; // past @ and the opening quote
                while (i < n && code[i] != quote)
                {
                    if (code[i] != '\n')
                    {
                        output[i] = ' ';
                    }

                    i++;
                }

                if (i < n)
                {
                    i++; // closing quote stays
                }

                spans.Add(new XppSpan(start, i, XppSpanKind.String, quote, Verbatim: true));
                continue;
            }

            // Ordinary string: "…" or '…' — backslash escapes the next character.
            if (c == '"' || c == '\'')
            {
                char quote = c;
                int start = i;
                i++; // opening quote stays
                while (i < n && code[i] != quote)
                {
                    if (code[i] == '\\')
                    {
                        output[i] = ' ';
                        if (i + 1 < n && code[i + 1] != '\n')
                        {
                            output[i + 1] = ' ';
                        }

                        i += 2;
                        continue;
                    }

                    if (code[i] != '\n')
                    {
                        output[i] = ' ';
                    }

                    i++;
                }

                if (i < n)
                {
                    i++; // closing quote stays
                }

                spans.Add(new XppSpan(start, i, XppSpanKind.String, quote));
                continue;
            }

            i++;
        }

        return new XppScan(new string(output), spans);
    }

    /// <summary>
    /// Masked copy of the source: literal and comment content replaced by spaces,
    /// delimiters and newlines preserved, length unchanged.
    /// </summary>
    public static string Mask(string code) => Scan(code).Masked;

    /// <summary>True when <paramref name="offset"/> falls inside a string literal or a comment.</summary>
    public static bool IsMasked(IReadOnlyList<XppSpan> spans, int offset)
        => spans.Any(s => offset >= s.Start && offset < s.End);
}
