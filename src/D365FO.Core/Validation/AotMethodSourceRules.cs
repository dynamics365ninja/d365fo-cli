using System.Xml;
using System.Xml.Linq;

namespace D365FO.Core.Validation;

/// <summary>
/// XML014 — an AxClass method's AOT name does not describe exactly one X++ method declaration
/// in its source. The metadata provider rejects this shape even though the XML is well-formed.
/// </summary>
/// <remarks>
/// <para>
/// Only method <em>headers</em> are read: the masked source is walked at brace depth zero,
/// attribute blocks are skipped, the text up to the first <c>{</c> is the header, and the last
/// identifier before its <c>(</c> is the declared name. The body is then skipped by brace
/// matching. Nothing inside a body is ever looked at, so statements such as
/// <c>throw error(…)</c> or <c>else if (…)</c> and nested local functions cannot count as
/// declarations, and no modifier list has to be kept complete (<c>display</c>, <c>edit</c>,
/// <c>server</c>, <c>client</c>, dotted .NET return types all fall out naturally).
/// </para>
/// <para>
/// A first version matched <c>word word(</c> on every line and flagged 17,424 of 67,403
/// shipped <c>AxClass</c> files, all of which compile. This shape flags none of them. Methods
/// written as a macro invocation (<c>#ParmMethod(JobId)</c>) are skipped: what they declare is
/// only known after preprocessing.
/// </para>
/// </remarks>
public static class AotMethodSourceRules
{
    /// <summary>An AxClass Method node has zero/multiple declarations or a declaration with another name.</summary>
    public const string RuleMethodSourceMismatch = "XML014";

    /// <summary>Appends AxClass method-source consistency violations found in <paramref name="xml"/>.</summary>
    public static void Check(string xml, List<XppViolation> violations)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException)
        {
            return;
        }

        if (document.Root?.Name.LocalName != "AxClass")
        {
            return;
        }

        foreach (var method in document.Root.Descendants("Method"))
        {
            var name = method.Element("Name")?.Value.Trim();
            var source = method.Element("Source")?.Value;
            if (string.IsNullOrEmpty(name) || string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var declarations = DeclaredMethodNames(BlankComments(source));
            if (declarations is null)
            {
                continue;
            }

            var line = (method as IXmlLineInfo)?.LineNumber;

            if (declarations.Count != 1)
            {
                violations.Add(new XppViolation(
                    RuleMethodSourceMismatch,
                    "error",
                    line,
                    $"AxClass/Methods/Method/{name}",
                    $"The <Method> node '{name}' must contain exactly one X++ method declaration, but contains {declarations.Count}. Split each X++ method into its own <Method> node."));
                continue;
            }

            // X++ identifiers are case-insensitive, and shipped classes compile with a case-only
            // difference (e.g. RetailSrsReportDataProviderChannelBase.InsertChannelsToTmpTable).
            if (!string.Equals(name, declarations[0], StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new XppViolation(
                    RuleMethodSourceMismatch,
                    "error",
                    line,
                    $"AxClass/Methods/Method/{name}",
                    $"The X++ method name '{declarations[0]}' does not match the AOT <Name> '{name}'. Rename one so both names match."));
            }
        }
    }

    /// <summary>
    /// Names of the top-level method declarations in <paramref name="masked"/> (comments and
    /// string contents already blanked). A braced block whose header has no <c>(</c> is not a
    /// method declaration and is not counted. Returns <c>null</c> when the source cannot be
    /// judged statically: a preprocessor directive outside a body (e.g. <c>#ParmMethod(JobId)</c>,
    /// which expands to the whole method) or unbalanced braces.
    /// </summary>
    internal static List<string>? DeclaredMethodNames(string masked)
    {
        var names = new List<string>();
        var i = 0;
        while (true)
        {
            i = SkipTrivia(masked, i);
            if (i >= masked.Length)
            {
                break;
            }

            var bodyStart = masked.IndexOf('{', i);
            var header = bodyStart < 0 ? masked[i..] : masked[i..bodyStart];
            if (header.Contains('#') || header.Contains('}'))
            {
                return null;
            }

            if (bodyStart < 0)
            {
                break;
            }

            var declared = NameFromHeader(header);
            if (declared.Length > 0)
            {
                names.Add(declared);
            }

            i = SkipBlock(masked, bodyStart);
        }

        return names;
    }

    /// <summary>
    /// <see cref="XppLexer.Mask"/> keeps comment delimiters; a header walk needs the whole
    /// comment gone so a leading <c>/// &lt;summary&gt;</c> block reads as whitespace.
    /// </summary>
    private static string BlankComments(string source)
    {
        var scan = XppLexer.Scan(source);
        var chars = scan.Masked.ToCharArray();
        foreach (var span in scan.Spans)
        {
            if (span.Kind == XppSpanKind.String)
            {
                continue;
            }

            for (var k = span.Start; k < span.End && k < chars.Length; k++)
            {
                if (!char.IsWhiteSpace(chars[k]))
                {
                    chars[k] = ' ';
                }
            }
        }

        return new string(chars);
    }

    /// <summary>Skips whitespace and <c>[…]</c> attribute blocks.</summary>
    private static int SkipTrivia(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i]))
            {
                i++;
            }
            else if (s[i] == '[')
            {
                var depth = 0;
                for (; i < s.Length; i++)
                {
                    if (s[i] == '[') depth++;
                    else if (s[i] == ']' && --depth == 0) { i++; break; }
                }
            }
            else
            {
                break;
            }
        }

        return i;
    }

    /// <summary>Returns the offset just past the <c>}</c> matching the <c>{</c> at <paramref name="open"/>.</summary>
    private static int SkipBlock(string s, int open)
    {
        var depth = 0;
        for (var i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i + 1;
        }

        return s.Length;
    }

    private static string NameFromHeader(string header)
    {
        var paren = header.IndexOf('(');
        if (paren < 0)
        {
            return string.Empty;
        }

        var end = paren;
        while (end > 0 && char.IsWhiteSpace(header[end - 1])) end--;
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(header[start - 1]) || header[start - 1] == '_')) start--;
        return header[start..end];
    }
}
