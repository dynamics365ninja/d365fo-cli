using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace D365FO.Core.Eval;

public sealed record XmlGoldenChange(string Path, string? Expected, string? Actual);

public sealed record XmlGoldenDiff(
    IReadOnlyList<string> Missing,
    IReadOnlyList<string> Extra,
    IReadOnlyList<XmlGoldenChange> Changed)
{
    public bool IsMatch => Missing.Count == 0 && Extra.Count == 0 && Changed.Count == 0;

    /// <summary>No difference — for scorecards whose golden dimension was not the thing being measured.</summary>
    public static XmlGoldenDiff Empty { get; } = new([], [], []);
}

/// <summary>
/// Normalizes an AOT-XML <see cref="XElement"/> tree into an
/// order-independent <c>path → value</c> map, and diffs two such maps.
/// There is no existing golden-XML-diff utility anywhere in this repo (the
/// current "golden" tests assert structurally against a live
/// <see cref="XElement"/> tree instead) — this is a fresh, minimal
/// implementation, modeled on the sibling d365fo-mcp-server repo's
/// <c>src/eval/oracle/{normalize,diff}.ts</c>.
///
/// Repeated sibling elements (e.g. multiple <c>&lt;AxTableField&gt;</c> under
/// <c>&lt;Fields&gt;</c>) are keyed by their own <c>&lt;Name&gt;</c> child or
/// <c>DataField</c> attribute/child where present, so collection reordering
/// does not register as a diff. Falls back to positional index when no
/// identifying child is present.
/// </summary>
public static class XmlGolden
{
    public static IReadOnlyDictionary<string, string> Normalize(XElement root, IReadOnlyList<string>? ignore = null)
    {
        var patterns = CompileIgnore(ignore);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, root.Name.LocalName, map, patterns);
        return map;
    }

    public static XmlGoldenDiff Diff(XElement expected, XElement actual, IReadOnlyList<string>? ignore = null)
    {
        var e = Normalize(expected, ignore);
        var a = Normalize(actual, ignore);

        var missing = e.Keys.Except(a.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var extra = a.Keys.Except(e.Keys).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var changed = e.Keys.Intersect(a.Keys)
            .Where(k => e[k] != a[k])
            .OrderBy(k => k, StringComparer.Ordinal)
            .Select(k => new XmlGoldenChange(k, e[k], a[k]))
            .ToList();

        return new XmlGoldenDiff(missing, extra, changed);
    }

    private static void Walk(XElement el, string path, Dictionary<string, string> map, IReadOnlyList<Regex> ignore)
    {
        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            var attrPath = $"{path}/@{attr.Name.LocalName}";
            if (!IsIgnored(attrPath, ignore)) map[attrPath] = attr.Value;
        }

        var children = el.Elements().ToList();
        if (children.Count == 0)
        {
            var value = el.Value.Trim();
            if (value.Length > 0 && !IsIgnored(path, ignore)) map[path] = value;
            return;
        }

        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                Walk(items[0], $"{path}/{group.Key}", map, ignore);
                continue;
            }

            for (var i = 0; i < items.Count; i++)
            {
                var key = items[i].Element("Name")?.Value
                          ?? items[i].Attribute("DataField")?.Value
                          ?? items[i].Element("DataField")?.Value
                          ?? i.ToString();
                Walk(items[i], $"{path}/{group.Key}[{key}]", map, ignore);
            }
        }
    }

    private static bool IsIgnored(string path, IReadOnlyList<Regex> patterns)
        => patterns.Any(p => p.IsMatch(path));

    /// <summary>
    /// Compiles simple glob patterns (<c>*</c> = any run of characters,
    /// <c>**</c> = same, spanning <c>/</c>) into regexes. There is no
    /// existing glob helper in this repo to reuse for XML paths specifically.
    /// </summary>
    private static IReadOnlyList<Regex> CompileIgnore(IReadOnlyList<string>? patterns)
    {
        if (patterns is null || patterns.Count == 0) return Array.Empty<Regex>();
        return patterns.Select(p =>
        {
            var escaped = Regex.Escape(p).Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*");
            return new Regex("^" + escaped + "$", RegexOptions.Compiled);
        }).ToList();
    }
}
