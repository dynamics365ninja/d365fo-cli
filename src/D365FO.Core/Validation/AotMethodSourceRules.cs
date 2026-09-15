using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>
/// XML014 — an AxClass method's AOT name does not describe exactly one X++ method declaration
/// in its source. The metadata provider rejects this shape even though the XML is well-formed.
/// </summary>
public static class AotMethodSourceRules
{
    /// <summary>An AxClass Method node has zero/multiple declarations or a declaration with another name.</summary>
    public const string RuleMethodSourceMismatch = "XML014";

    private static readonly Regex MethodDeclaration = new(
        @"^\s*(?:(?:public|protected|private|internal|final|static|abstract)\s+)*[A-Za-z_]\w*(?:::\w+)?(?:\s*\[\s*\])?\s+([A-Za-z_]\w*)\s*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

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
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(source))
            {
                continue;
            }

            var maskedSource = XppLexer.Mask(source);
            var declarations = MethodDeclaration.Matches(maskedSource)
                .Cast<Match>()
                .Where(match =>
                {
                    var lineStart = maskedSource.LastIndexOf('\n', match.Index) + 1;
                    if (lineStart < 0)
                    {
                        lineStart = 0;
                    }

                    var lineEnd = maskedSource.IndexOf('\n', match.Index);
                    if (lineEnd < 0)
                    {
                        lineEnd = maskedSource.Length;
                    }

                    if (lineStart > lineEnd)
                    {
                        return true;
                    }

                    var line = maskedSource.Substring(lineStart, lineEnd - lineStart).TrimStart();
                    return !line.StartsWith("next ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("super ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("return ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("if ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("while ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("for ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("switch ", StringComparison.OrdinalIgnoreCase)
                        && !line.StartsWith("case ", StringComparison.OrdinalIgnoreCase);
                })
                .Select(match => match.Groups[1].Value)
                .Where(methodName => !string.IsNullOrEmpty(methodName))
                .ToList();
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

            if (!string.Equals(name, declarations[0], StringComparison.Ordinal))
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
}