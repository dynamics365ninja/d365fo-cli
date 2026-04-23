using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace D365FO.Core.Extract;

/// <summary>
/// Reads X++ source embedded in AxClass / AxTable / AxForm XML files. Modern
/// D365FO stores per-method source as CDATA inside
/// <c>&lt;SourceCode&gt;&lt;Methods&gt;&lt;Method&gt;&lt;Source&gt;...&lt;/Source&gt;</c>.
/// The class/table-level declaration lives under <c>&lt;SourceCode&gt;&lt;Declaration&gt;</c>.
/// Older layouts put methods directly under <c>&lt;Methods&gt;</c>, which we also tolerate.
/// </summary>
public static class XppSourceReader
{
    public sealed record SourceBlock(string Kind, string Name, string Body);
    public sealed record SourceResult(string Path, string? Declaration, IReadOnlyList<SourceBlock> Methods);

    public static SourceResult? Read(string xmlPath)
    {
        if (string.IsNullOrWhiteSpace(xmlPath) || !File.Exists(xmlPath)) return null;
        XDocument doc;
        try { doc = XDocument.Load(xmlPath, LoadOptions.None); }
        catch { return null; }
        if (doc.Root is null) return null;

        string? declaration = FindDescendantLocal(doc.Root, "Declaration")?.Value?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(declaration)) declaration = null;

        var methods = new List<SourceBlock>();
        foreach (var m in doc.Root.Descendants().Where(e => e.Name.LocalName == "Method"))
        {
            var name = FindDescendantLocal(m, "Name")?.Value?.Trim() ?? "";
            var src = FindDescendantLocal(m, "Source")?.Value;
            if (string.IsNullOrEmpty(name) || src is null) continue;
            methods.Add(new SourceBlock("Method", name, src));
        }

        return new SourceResult(xmlPath, declaration, methods);
    }

    public static SourceBlock? FindMethod(SourceResult src, string methodName)
        => src.Methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Return a contiguous slice of <paramref name="body"/> by 1-based line
    /// numbers. Clamps to body bounds. <paramref name="fromLine"/> &lt; 1 is
    /// treated as 1, <paramref name="toLine"/> past the end is clamped to the
    /// last line. Returns null when bounds are invalid (<c>from &gt; to</c>).
    /// </summary>
    public static string? Slice(string body, int fromLine, int toLine)
    {
        if (body is null || fromLine > toLine) return null;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        if (fromLine < 1) fromLine = 1;
        if (toLine > lines.Length) toLine = lines.Length;
        if (fromLine > lines.Length) return string.Empty;
        return string.Join("\n", lines.AsEnumerable().Skip(fromLine - 1).Take(toLine - fromLine + 1));
    }

    /// <summary>
    /// Return lines of <paramref name="body"/> that match
    /// <paramref name="pattern"/> (case-insensitive regex) with
    /// <paramref name="contextLines"/> lines of context before and after each
    /// hit. Groups of overlapping hits are coalesced. Returns an empty string
    /// when nothing matches.
    /// </summary>
    public static string AroundPattern(string body, string pattern, int contextLines = 3)
    {
        if (string.IsNullOrEmpty(body) || string.IsNullOrEmpty(pattern)) return string.Empty;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        System.Text.RegularExpressions.Regex rx;
        try
        {
            rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
        var keep = new bool[lines.Length];
        for (int i = 0; i < lines.Length; i++)
        {
            if (!rx.IsMatch(lines[i])) continue;
            var lo = Math.Max(0, i - contextLines);
            var hi = Math.Min(lines.Length - 1, i + contextLines);
            for (int j = lo; j <= hi; j++) keep[j] = true;
        }
        var sb = new System.Text.StringBuilder();
        bool prevKept = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (keep[i])
            {
                sb.Append(i + 1).Append(": ").AppendLine(lines[i]);
                prevKept = true;
            }
            else if (prevKept)
            {
                sb.AppendLine("---");
                prevKept = false;
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static XElement? FindDescendantLocal(XElement root, string localName)
        => root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
}
