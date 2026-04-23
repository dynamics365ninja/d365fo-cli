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

    private static XElement? FindDescendantLocal(XElement root, string localName)
        => root.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);
}
