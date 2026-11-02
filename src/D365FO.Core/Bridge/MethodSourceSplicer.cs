// <copyright file="MethodSourceSplicer.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text;
using System.Text.RegularExpressions;
using D365FO.Core.Validation;

namespace D365FO.Core.Bridge;

/// <summary>
/// Puts new statements between the outermost braces of an existing method's <c>&lt;Source&gt;</c>
/// and keeps everything around them: the doc comment, the attributes, the signature and the
/// braces themselves (issue #231).
/// </summary>
/// <remarks>
/// <para>
/// <c>&lt;Method&gt;&lt;Source&gt;</c> in the AOT holds the whole method, but <c>modify method
/// --body</c> is documented as "statements only". The engine used to write the statements as the
/// whole source, so a caller who followed the help got <c>ok:true</c> and a method with no
/// signature. This class is the documented contract.
/// </para>
/// <para>
/// The brace pair is found on the <see cref="XppLexer"/>-masked source, so a brace inside a
/// string literal, a <c>//</c> or <c>/* */</c> comment or a doc comment never counts. Anything it
/// cannot place unambiguously is refused rather than written.
/// </para>
/// </remarks>
internal static class MethodSourceSplicer
{
    /// <summary>Outcome of a splice or of an input check. <see cref="Error"/> is null on success.</summary>
    internal sealed record Result(string? Source, string? Error, string? Code, string? Hint)
    {
        public static Result Ok(string source) => new(source, null, null, null);

        public static Result Fail(string code, string error, string hint) => new(null, error, code, hint);
    }

    /// <summary>Error code for a source whose body cannot be located.</summary>
    internal const string BodyNotFound = "METHOD_BODY_NOT_FOUND";

    // Words that can stand directly before '(' in a statement. A header whose last word before
    // '(' is one of these is a statement (if, while select … where (…), catch, using …), not a
    // method declaration.
    private static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "while", "for", "switch", "catch", "using", "where", "select", "exists", "join",
        "notexists", "outer", "return", "new", "throw", "case", "do", "else", "try", "finally",
        "changecompany", "lock", "unchecked", "next", "firstonly", "forupdate", "crosscompany",
        "in", "like", "order", "group", "by", "index", "hint",
    };

    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly Regex MacroLine = new(@"^[ \t]*#", RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex HeaderWord = new(@"\b(abstract|delegate)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replace the text between the outermost braces of <paramref name="source"/> with
    /// <paramref name="body"/>, re-indented one level inside the method. The source's own line
    /// ending (CRLF or LF) and indentation style (tabs or spaces) are kept.
    /// </summary>
    internal static Result Splice(string source, string body, string methodName)
    {
        var blanked = BlankComments(source);

        var open = blanked.IndexOf('{');
        if (open < 0)
        {
            return Result.Fail(BodyNotFound,
                $"Method '{methodName}' has no body: its source has no braces (an abstract, interface or delegate declaration, or a macro-generated method).",
                "There is nothing to put statements into. Change the declaration with `generate`, or edit the method in Visual Studio.");
        }

        // Only a line that starts with '#' (a macro invocation or directive) makes the declaration
        // macro-dependent; a macro used as a parameter default or attribute argument does not.
        var header = blanked[..open];
        if (MacroLine.IsMatch(header))
        {
            return Result.Fail(BodyNotFound,
                $"Method '{methodName}' is declared through a macro, so its body is only known after preprocessing.",
                "Edit this method in Visual Studio.");
        }

        if (HeaderWord.Match(header) is { Success: true } word)
        {
            return Result.Fail(D365FoErrorCodes.BadInput,
                $"Method '{methodName}' is declared '{word.Value.ToLowerInvariant()}', and X++ does not allow a body there.",
                "A body cannot be added to an abstract method or a delegate. Implement it in a derived class, or subscribe to the delegate.");
        }

        var close = MatchingBrace(blanked, open);
        if (close < 0)
        {
            return Result.Fail(BodyNotFound,
                $"The braces of method '{methodName}' do not balance, so its body cannot be located.",
                "Fix the method in Visual Studio first. Nothing was written.");
        }

        for (var k = close + 1; k < blanked.Length; k++)
        {
            if (!char.IsWhiteSpace(blanked[k]))
            {
                return Result.Fail(BodyNotFound,
                    $"The source of method '{methodName}' continues after its closing brace, so which block is the body is ambiguous.",
                    "Each <Method> node should hold one method. Fix it in Visual Studio first. Nothing was written.");
            }
        }

        var newline = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var methodIndent = MethodIndent(source, open, close);
        var innerIndent = methodIndent + IndentUnit(source, open, close, methodIndent);

        var sb = new StringBuilder(source.Length + body.Length + 64);
        sb.Append(source, 0, open + 1);
        sb.Append(newline);
        sb.Append(Reindent(body, innerIndent, newline));
        sb.Append(newline);
        sb.Append(methodIndent);
        sb.Append(source, close, source.Length - close);
        return Result.Ok(sb.ToString());
    }

    /// <summary>
    /// Refuse a <c>--body</c> that is a whole method declaration (doc comment, attributes or a
    /// signature followed by its block) rather than statements. Splicing that would nest one
    /// method inside another.
    /// </summary>
    internal static Result? RefuseDeclarationAsBody(string body, string methodName)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith("///", StringComparison.Ordinal))
            return DeclarationRefusal(methodName, "starts with a /// doc comment");

        var blanked = BlankComments(body);
        var i = SkipWhitespace(blanked, 0);

        // [Attribute] … before a declaration. A container assignment ([a, b] = con;) also starts
        // with '[', so an attribute block counts only when something other than '=' follows it.
        var sawAttribute = false;
        while (i < blanked.Length && blanked[i] == '[')
        {
            var end = MatchingBracket(blanked, i);
            if (end < 0) return null;
            var after = SkipWhitespace(blanked, end + 1);
            if (after < blanked.Length && blanked[after] == '=') return null;
            sawAttribute = true;
            i = after;
        }

        var open = blanked.IndexOf('{', i);
        if (open < 0) return null;

        var header = blanked[i..open];
        if (header.Contains(';') || header.Contains('}'))
            return null;

        var declared = DeclaredName(header);
        if (declared is null)
            return sawAttribute ? DeclarationRefusal(methodName, "starts with an attribute") : null;

        // A local function at the top of a body is legal X++. A whole declaration is the only
        // thing in the text, or carries the method's own name.
        var close = MatchingBrace(blanked, open);
        var onlyThing = close >= 0 && SkipWhitespace(blanked, close + 1) >= blanked.Length;
        if (sawAttribute || onlyThing || string.Equals(declared, methodName, StringComparison.OrdinalIgnoreCase))
            return DeclarationRefusal(methodName, $"starts with the declaration of '{declared}'");

        return null;
    }

    /// <summary>
    /// The method names <paramref name="source"/> declares at its top level, or null when that
    /// cannot be judged statically (a macro-declared method, unbalanced braces).
    /// </summary>
    internal static List<string>? DeclaredMethodNames(string source)
        => AotMethodSourceRules.DeclaredMethodNames(BlankComments(source));

    private static Result DeclarationRefusal(string methodName, string what) =>
        Result.Fail(D365FoErrorCodes.BadInput,
            $"--body {what}: it is a full method declaration, but --body takes the statements between the braces only.",
            $"Pass only the statements, and the doc comment, attributes and signature of '{methodName}' are kept. " +
            "To replace the whole method including its signature, use --source (MCP: `source`) instead of --body.");

    /// <summary>The last word before the first '(' of a header that looks like a declaration, else null.</summary>
    private static string? DeclaredName(string header)
    {
        var paren = header.IndexOf('(');
        if (paren < 0) return null;
        if (header.IndexOf(')', paren) < 0) return null;

        var words = header[..paren].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null; // `if (`, `foo(` …: a statement, not `type name(`

        var name = words[^1];
        if (!Identifier.IsMatch(name) || StatementKeywords.Contains(name)) return null;
        if (words.Take(words.Length - 1).Any(w => StatementKeywords.Contains(w) || w.Contains('=')))
            return null;
        return name;
    }

    private static string BlankComments(string source) => AotMethodSourceRules.BlankComments(source);

    private static int SkipWhitespace(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        return i;
    }

    private static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }

        return -1;
    }

    private static int MatchingBracket(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '[') depth++;
            else if (s[i] == ']' && --depth == 0) return i;
        }

        return -1;
    }

    private static string LeadingWhitespaceOfLine(string s, int offset)
    {
        var lineStart = s.LastIndexOf('\n', Math.Max(0, offset - 1)) + 1;
        var end = lineStart;
        while (end < s.Length && (s[end] == ' ' || s[end] == '\t')) end++;
        return s[lineStart..end];
    }

    private static bool FirstOnLine(string s, int offset)
    {
        for (var k = offset - 1; k >= 0 && s[k] != '\n'; k--)
        {
            if (s[k] != ' ' && s[k] != '\t' && s[k] != '\r') return false;
        }

        return true;
    }

    /// <summary>
    /// The indentation the closing brace gets: that of the old closing brace when it starts its
    /// line, else that of the opening brace's line (the signature line for <c>void f() {</c>).
    /// </summary>
    private static string MethodIndent(string source, int open, int close)
    {
        if (FirstOnLine(source, close)) return LeadingWhitespaceOfLine(source, close);
        return LeadingWhitespaceOfLine(source, open);
    }

    /// <summary>
    /// One indentation step, read from the old body's first indented line, so a tab-indented
    /// method stays tab-indented. Falls back to a tab when the method itself is tab-indented,
    /// else four spaces (the AOT default).
    /// </summary>
    private static string IndentUnit(string source, int open, int close, string methodIndent)
    {
        var inner = source[(open + 1)..close].Replace("\r", string.Empty, StringComparison.Ordinal);
        foreach (var line in inner.Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            var ws = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            if (ws.Length > methodIndent.Length && ws.StartsWith(methodIndent, StringComparison.Ordinal))
                return ws[methodIndent.Length..];
            break;
        }

        return methodIndent.Contains('\t') ? "\t" : "    ";
    }

    /// <summary>
    /// Dedent <paramref name="body"/> by its common indentation, drop blank edge lines, and indent
    /// every line by <paramref name="indent"/>. Lines that begin inside a multi-line verbatim
    /// string are left alone: their leading spaces are part of the literal.
    /// </summary>
    private static string Reindent(string body, string indent, string newline)
    {
        var text = body.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var spans = XppLexer.Scan(text).Spans.Where(s => s.Kind == XppSpanKind.String).ToList();

        var lines = new List<(string Text, bool InLiteral)>();
        var offset = 0;
        foreach (var line in text.Split('\n'))
        {
            var inLiteral = spans.Any(s => offset > s.Start && offset < s.End);
            lines.Add((line, inLiteral));
            offset += line.Length + 1;
        }

        while (lines.Count > 0 && !lines[0].InLiteral && lines[0].Text.Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && !lines[^1].InLiteral && lines[^1].Text.Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

        var common = lines
            .Where(l => !l.InLiteral && l.Text.Trim().Length > 0)
            .Select(l => l.Text[..(l.Text.Length - l.Text.TrimStart(' ', '\t').Length)])
            .Aggregate((string?)null, (acc, ws) => acc is null ? ws : CommonPrefix(acc, ws)) ?? string.Empty;

        var output = lines.Select(l =>
        {
            if (l.InLiteral) return l.Text;
            if (l.Text.Trim().Length == 0) return string.Empty;
            return indent + l.Text[common.Length..];
        });

        return string.Join(newline, output);
    }

    private static string CommonPrefix(string a, string b)
    {
        var n = 0;
        while (n < a.Length && n < b.Length && a[n] == b[n]) n++;
        return a[..n];
    }
}
