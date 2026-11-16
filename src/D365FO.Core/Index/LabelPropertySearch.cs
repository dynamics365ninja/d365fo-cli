using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace D365FO.Core.Index;

/// <summary>A metadata property whose value is the searched label reference.</summary>
/// <param name="Kind">The AOT kind, named as its root element without <c>Ax</c> (<c>Table</c>, <c>Form</c>, <c>MenuItemDisplay</c>).</param>
/// <param name="Name">The object.</param>
/// <param name="Model">The model that declares it.</param>
/// <param name="Element">
/// Where in the object, through its named nodes: <c>Fields/AccountNum</c>, <c>Design/TabGeneral/OkButton</c>.
/// Null for a property of the object itself.
/// </param>
/// <param name="Property">The property that holds the label: <c>Label</c>, <c>HelpText</c>, <c>Text</c>, <c>Caption</c>, …</param>
/// <param name="Via"><c>index</c> (a <c>Label</c> column) or <c>disk</c> (the object's XML).</param>
public sealed record LabelPropertyHit(string Kind, string Name, string Model, string? Element, string Property, string Via);

/// <summary>Where a label search over metadata properties looked.</summary>
/// <param name="FromIndex">Models answered from the index's <c>Label</c> columns only.</param>
/// <param name="FromDisk">Models whose XML was read, so every property was searched.</param>
/// <param name="FilesScanned">XML files opened on disk.</param>
/// <param name="Note">What the index answer leaves out, and how to read the rest.</param>
public sealed record LabelPropertyCoverage(
    IReadOnlyList<string> FromIndex,
    IReadOnlyList<string> FromDisk,
    int FilesScanned,
    string? Note);

/// <summary>
/// Where-used for a label reference in METADATA: the <c>Label</c>, <c>HelpText</c>, <c>Text</c>,
/// <c>Caption</c> … properties that hold <c>@File:Key</c>, as opposed to X++ code. Most label
/// references are there, not in method bodies, so a search over code alone misses them.
/// </summary>
/// <remarks>
/// Two sources. The index keeps the <c>Label</c> property of most object kinds
/// (<see cref="MetadataRepository.IndexedLabelProperties"/>), so every model is answered from it in
/// well under a second. Every other property is only in the XML, which is read for the models the
/// caller names (the default scope: custom and source-indexed models), never for the whole
/// installation: tens of thousands of standard files would turn a one-second answer into minutes.
/// A file is opened as text and parsed only when the label's text occurs in it.
/// </remarks>
public static class LabelPropertySearch
{
    /// <summary>Folders with no label-bearing property outside code, which the method scan reads.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AxClass", "AxLabelFile", "AxResource",
    };

    /// <summary>Root kinds whose code the method-body search already reads.</summary>
    private static readonly HashSet<string> CodeSearchedKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Class", "Table", "Form",
    };

    /// <param name="repo">Index to read.</param>
    /// <param name="labelToken">The label reference (<c>@SYS12345</c> or <c>@File:Key</c>).</param>
    /// <param name="tokenRegex">The label as a whole token (see <see cref="MethodSourceSearch.TokenRegex"/>).</param>
    /// <param name="diskModels">Models whose XML is read. Every other model is answered from the index.</param>
    /// <param name="modelFilter">One model, or null for every model.</param>
    /// <param name="limit">Most hits returned.</param>
    public static (IReadOnlyList<LabelPropertyHit> Hits, bool Truncated, LabelPropertyCoverage Coverage) Find(
        MetadataRepository repo, string labelToken, Regex tokenRegex,
        IReadOnlyCollection<string> diskModels, string? modelFilter, int limit)
    {
        var hits = new List<LabelPropertyHit>();
        bool truncated = false;
        int scanned = 0;

        var locations = repo.GetModelLocations();
        var allModels = locations.Select(l => l.Model)
            .Where(m => modelFilter is null || string.Equals(m, modelFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var folders = locations
            .Where(l => l.SamplePath is not null && diskModels.Contains(l.Model, StringComparer.OrdinalIgnoreCase))
            .Where(l => modelFilter is null || string.Equals(l.Model, modelFilter, StringComparison.OrdinalIgnoreCase))
            .Select(l => (l.Model, Folder: IndexPrune.ModelFolderOf(l.SamplePath!)))
            .Where(l => l.Folder is not null && Directory.Exists(l.Folder))
            .ToList();
        var readFromDisk = new HashSet<string>(folders.Select(f => f.Model), StringComparer.OrdinalIgnoreCase);

        foreach (var (model, folder) in folders)
        {
            if (truncated) break;
            foreach (var file in EnumerateXml(folder!))
            {
                if (hits.Count >= limit) { truncated = true; break; }
                string text;
                try { text = File.ReadAllText(file); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                scanned++;
                // Most files do not mention the label at all: a text test skips the XML parse.
                if (text.IndexOf(labelToken, StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (var hit in ScanXml(text, model, tokenRegex))
                {
                    if (hits.Count >= limit) { truncated = true; break; }
                    hits.Add(hit);
                }
            }
        }

        // The index answers the Label property of every other model, and of models whose folder
        // is not on this machine.
        foreach (var use in repo.FindIndexedLabelUses(labelToken))
        {
            if (readFromDisk.Contains(use.Model)) continue;
            if (modelFilter is not null && !string.Equals(use.Model, modelFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (hits.Count >= limit) { truncated = true; break; }
            hits.Add(new LabelPropertyHit(use.Kind, use.Name, use.Model, use.Element, use.Property, "index"));
        }

        var fromIndex = allModels.Where(m => !readFromDisk.Contains(m))
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        var fromDisk = readFromDisk.OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
        var note = fromIndex.Count == 0
            ? null
            : $"For {fromIndex.Count} model(s) only the indexed Label properties were searched ("
              + MetadataRepository.IndexedLabelProperties + "). HelpText, form control Text, form Caption and "
              + "other properties are read from the XML only for the models in `fromDisk`: name one with "
              + "--model (MCP: model) to read its XML too.";
        return (hits, truncated, new LabelPropertyCoverage(fromIndex, fromDisk, scanned, note));
    }

    private static IEnumerable<string> EnumerateXml(string modelFolder)
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(modelFolder, "Ax*").ToList(); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }
        foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            if (SkippedFolders.Contains(Path.GetFileName(dir))) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files) yield return f;
        }
    }

    /// <summary>
    /// Every leaf element of one AOT XML whose value holds the label as a token. Code of classes,
    /// tables and forms is left to the method-body search, so a hit is not reported twice; code of
    /// every other kind (data entity, query, view and map methods) is reported here as <c>Source</c>.
    /// </summary>
    internal static IEnumerable<LabelPropertyHit> ScanXml(string xml, string model, Regex tokenRegex)
    {
        XElement root;
        try { root = XDocument.Parse(xml).Root!; }
        catch (XmlException) { yield break; }
        if (root is null) yield break;

        var rootName = root.Name.LocalName;
        var kind = KindOf(rootName);
        var name = Child(root, "Name") ?? "";
        var skipCode = CodeSearchedKinds.Contains(kind);

        foreach (var el in root.Descendants())
        {
            if (el.HasElements) continue;
            if (!tokenRegex.IsMatch(el.Value)) continue;
            var inCode = el.Ancestors().Any(a => a.Name.LocalName == "SourceCode");
            if (inCode && skipCode) continue;
            yield return new LabelPropertyHit(kind, name, model, ElementPath(root, el), el.Name.LocalName, "disk");
        }
    }

    /// <summary>
    /// <c>AxTable</c> → <c>Table</c>, <c>AxEdtString</c> → <c>Edt</c> (the index's name for every EDT
    /// type), <c>AxMenuItemDisplay</c> → <c>MenuItemDisplay</c>.
    /// </summary>
    internal static string KindOf(string rootName)
    {
        var kind = rootName.StartsWith("Ax", StringComparison.Ordinal) ? rootName[2..] : rootName;
        return kind.StartsWith("Edt", StringComparison.Ordinal) && !kind.EndsWith("Extension", StringComparison.Ordinal)
            ? "Edt"
            : kind;
    }

    /// <summary>
    /// The path from the object to the element's parent: the section directly under the root
    /// (<c>Fields</c>, <c>Design</c>), then each named node (<c>AccountNum</c>, <c>OkButton</c>).
    /// </summary>
    internal static string? ElementPath(XElement root, XElement el)
    {
        var parts = new List<string>();
        for (var a = el.Parent; a is not null && a != root; a = a.Parent)
        {
            if (a.Parent == root) { parts.Add(a.Name.LocalName); continue; }
            var n = Child(a, "Name");
            if (!string.IsNullOrEmpty(n)) parts.Add(n);
        }
        if (parts.Count == 0) return null;
        parts.Reverse();
        return string.Join('/', parts);
    }

    private static string? Child(XElement e, string localName) =>
        e.Elements().FirstOrDefault(c => c.Name.LocalName == localName)?.Value;
}
