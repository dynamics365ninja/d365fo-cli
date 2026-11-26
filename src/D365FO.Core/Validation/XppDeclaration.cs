using System.Text.RegularExpressions;

namespace D365FO.Core.Validation;

/// <summary>One parameter of a method declaration.</summary>
public sealed record XppParameter(string Type, string Name);

/// <summary>
/// What the text in front of a method's body says: its modifiers, signature and the
/// attributes that decide whether an extension may bind to it.
/// </summary>
/// <param name="Modifiers">Lower-cased modifiers, <c>delegate</c> included.</param>
/// <param name="ReturnType">The declared return type.</param>
/// <param name="Name">The method's name as declared.</param>
/// <param name="Parameters">The parameters in order, without default values.</param>
/// <param name="Hookable"><c>[Hookable]</c> / <c>(true)</c> → true, <c>(false)</c> → false, absent → null.</param>
/// <param name="Wrappable">The same for <c>[Wrappable]</c>.</param>
/// <param name="ObsoleteError">Null when not obsolete; otherwise the attribute's isError argument.</param>
public sealed record XppDeclaration(
    IReadOnlyList<string> Modifiers,
    string ReturnType,
    string Name,
    IReadOnlyList<XppParameter> Parameters,
    bool? Hookable,
    bool? Wrappable,
    bool? ObsoleteError)
{
    public bool Has(string modifier) => Modifiers.Contains(modifier, StringComparer.OrdinalIgnoreCase);

    public bool IsStatic => Has("static");

    public bool IsDelegate => Has("delegate");

    /// <summary>The declaration as one line, for a finding's text.</summary>
    public string Render()
        => $"{(Modifiers.Count > 0 ? string.Join(' ', Modifiers) + " " : "")}{ReturnType} {Name}(" +
           $"{string.Join(", ", Parameters.Select(p => $"{p.Type} {p.Name}"))})";

    private const string ModifierPattern =
        "public|protected|private|internal|static|final|abstract|display|edit|server|client|delegate";

    /// <summary>
    /// The declaration of <paramref name="name"/> in <paramref name="source"/> — a method's source
    /// as the AOT stores it, or a snippet holding it. Calls (<c>next name(</c>, <c>x.name(</c>,
    /// <c>X::name(</c>) are skipped. Null when no declaration can be read; the callers then say
    /// nothing.
    /// </summary>
    public static XppDeclaration? Parse(string source, string name)
    {
        var masked = XppLexer.Mask(source);
        // `<return type> name(` — a call has an operator, a dot or `next` in that position.
        var pattern = new Regex(
            $@"(?<![\w.:])(?:(?<mod>(?:{ModifierPattern}))\s+)*(?<ret>[A-Za-z_]\w*(?:\s*\[\s*\])?)\s+(?<name>{Regex.Escape(name)})\s*\(",
            RegexOptions.IgnoreCase);
        foreach (Match m in pattern.Matches(masked))
        {
            var ret = m.Groups["ret"].Value;
            if (ret.Equals("next", StringComparison.OrdinalIgnoreCase)
                || ret.Equals("new", StringComparison.OrdinalIgnoreCase)
                || ret.Equals("return", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parameters = ParseParameters(masked, m.Index + m.Length - 1);
            if (parameters is null) return null;

            // The attributes sit between the previous statement or block and the declaration.
            var from = m.Index == 0 ? 0 : masked.LastIndexOfAny([';', '{', '}'], m.Index - 1) + 1;
            var header = masked[from..m.Index];

            return new XppDeclaration(
                m.Groups["mod"].Captures.Select(c => c.Value.ToLowerInvariant()).ToList(),
                ret,
                m.Groups["name"].Value,
                parameters,
                AttributeFlag(header, "Hookable"),
                AttributeFlag(header, "Wrappable"),
                ObsoleteFlag(header));
        }

        return null;
    }

    private static List<XppParameter>? ParseParameters(string masked, int open)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = open + 1;
        for (int i = open; i < masked.Length; i++)
        {
            var ch = masked[i];
            if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']')
            {
                depth--;
                if (depth != 0) continue;
                parts.Add(masked[start..i]);
                var parameters = new List<XppParameter>();
                foreach (var part in parts)
                {
                    var declared = part.Split('=')[0].Trim();
                    if (declared.Length == 0) continue;
                    var p = Regex.Match(declared, @"^([A-Za-z_]\w*)(?:\s+\d+)?\s+([A-Za-z_]\w*)(?:\s*\[[^\]]*\])?$");
                    if (!p.Success) return null;
                    parameters.Add(new XppParameter(p.Groups[1].Value, p.Groups[2].Value));
                }

                return parameters;
            }
            else if (ch == ',' && depth == 1)
            {
                parts.Add(masked[start..i]);
                start = i + 1;
            }
        }

        return null;
    }

    private static bool? AttributeFlag(string header, string name)
    {
        var m = Regex.Match(header, $@"\b{name}(?:Attribute)?(?!\w)\s*(?:\(\s*(true|false)?\s*\))?", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return !m.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ObsoleteFlag(string header)
    {
        var m = Regex.Match(header, @"\bSysObsolete(?:Attribute)?(?!\w)\s*(?:\(([^)]*)\))?", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var args = m.Groups[1].Value.Split(',');
        return args.Length > 1 && args[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
    }
}
