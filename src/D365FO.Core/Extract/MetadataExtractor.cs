using System.Xml.Linq;
using D365FO.Core.Index;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace D365FO.Core.Extract;

/// <summary>
/// Walks a D365FO <c>PackagesLocalDirectory</c>-style tree and produces an
/// <see cref="ExtractBatch"/> per model. The layout we expect:
/// <code>
/// &lt;packages-root&gt;/&lt;Package&gt;/&lt;Model&gt;/AxTable/*.xml
/// &lt;packages-root&gt;/&lt;Package&gt;/&lt;Model&gt;/AxClass/*.xml
/// &lt;packages-root&gt;/&lt;Package&gt;/&lt;Model&gt;/AxEdt/*.xml
/// &lt;packages-root&gt;/&lt;Package&gt;/&lt;Model&gt;/AxEnum/*.xml
/// &lt;packages-root&gt;/&lt;Package&gt;/&lt;Model&gt;/AxMenuItem*/*.xml
/// &lt;packages-root&gt;/&lt;Package&gt;/&lt;Model&gt;/AxLabelFile/*.xml
/// </code>
/// AOT XML uses namespaced elements inconsistently; we resolve by local-name
/// to be schema-version tolerant. Unknown elements are ignored rather than
/// failing the whole pass — extraction is best-effort by design.
/// </summary>
public sealed class MetadataExtractor
{
    private readonly ILogger _log;

    public MetadataExtractor(ILogger? log = null) => _log = log ?? NullLogger.Instance;

    public IEnumerable<ExtractBatch> ExtractAll(
        string packagesRoot,
        IReadOnlyCollection<string>? labelLanguages = null,
        IReadOnlyCollection<string>? customModelPatterns = null)
    {
        if (string.IsNullOrWhiteSpace(packagesRoot))
            throw new ArgumentException("packagesRoot required", nameof(packagesRoot));
        if (!Directory.Exists(packagesRoot))
            throw new DirectoryNotFoundException($"Packages root not found: {packagesRoot}");

        var matcher = new ModelMatcher(customModelPatterns ?? Array.Empty<string>());

        foreach (var packageDir in EnumerateDirectories(packagesRoot))
        {
            foreach (var modelDir in EnumerateDirectories(packageDir))
            {
                // Skip anything that does not look like a model folder.
                if (!HasAnyAotSubfolder(modelDir)) continue;
                var model = Path.GetFileName(modelDir)!;
                ExtractBatch batch;
                try
                {
                    batch = ExtractModel(modelDir, model, labelLanguages, matcher.IsMatch(model));
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Skipping model {Model}: {Msg}", model, ex.Message);
                    continue;
                }
                yield return batch;
            }
        }
    }

    public ExtractBatch ExtractModel(
        string modelRoot,
        string modelName,
        IReadOnlyCollection<string>? labelLanguages = null,
        bool isCustom = false)
    {
        var tables = ReadAll(Path.Combine(modelRoot, "AxTable"), ParseTable);
        var classes = ReadAll(Path.Combine(modelRoot, "AxClass"), ParseClass);
        var edts = ReadAll(Path.Combine(modelRoot, "AxEdt"), ParseEdt);
        var enums = ReadAll(Path.Combine(modelRoot, "AxEnum"), ParseEnum);
        var menuItems = new List<ExtractedMenuItem>();
        foreach (var kind in new[] { "AxMenuItemDisplay", "AxMenuItemAction", "AxMenuItemOutput" })
            menuItems.AddRange(ReadAll(Path.Combine(modelRoot, kind), (doc, path) => ParseMenuItem(doc, path, kind)));

        var labels = new List<ExtractedLabel>();
        var labelsDir = Path.Combine(modelRoot, "AxLabelFile");
        if (Directory.Exists(labelsDir))
        {
            foreach (var labelFile in Directory.EnumerateFiles(labelsDir, "*.xml", SearchOption.TopDirectoryOnly))
                labels.AddRange(ParseLabelFile(labelFile, labelLanguages));
        }

        var coc = classes
            .Where(c => c.Extends is null && c.Name.EndsWith("_Extension", StringComparison.Ordinal))
            .SelectMany(c => c.Methods.Select(m => new ExtractedCoc(
                TargetClass: InferTargetFromExtensionName(c.Name),
                TargetMethod: m.Name,
                ExtensionClass: c.Name)))
            .Where(x => !string.IsNullOrEmpty(x.TargetClass))
            .ToList();

        return new ExtractBatch(
            Model: modelName,
            Publisher: null,
            Layer: null,
            IsCustom: isCustom,
            Tables: tables,
            Classes: classes,
            Edts: edts,
            Enums: enums,
            MenuItems: menuItems,
            CocExtensions: coc,
            Labels: labels);
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static bool HasAnyAotSubfolder(string dir)
    {
        foreach (var s in new[] { "AxTable", "AxClass", "AxEdt", "AxEnum", "AxLabelFile" })
            if (Directory.Exists(Path.Combine(dir, s))) return true;
        return false;
    }

    private List<T> ReadAll<T>(string dir, Func<XDocument, string, T?> parser) where T : class
    {
        var list = new List<T>();
        if (!Directory.Exists(dir)) return list;
        foreach (var file in Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file, LoadOptions.None);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "skip malformed {File}", file);
                continue;
            }
            try
            {
                var parsed = parser(doc, file);
                if (parsed is not null) list.Add(parsed);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "parser failed on {File}", file);
            }
        }
        return list;
    }

    // ---- parsers (schema-version tolerant, local-name lookups) ----

    private static string? Local(XElement? e, string name) =>
        e?.Elements().FirstOrDefault(x => x.Name.LocalName == name)?.Value;

    private static IEnumerable<XElement> Children(XElement e, string name) =>
        e.Elements().Where(x => x.Name.LocalName == name);

    private static ExtractedTable? ParseTable(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var label = Local(root, "Label");
        var fields = new List<ExtractedTableField>();
        var fieldsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fields");
        if (fieldsContainer is not null)
        {
            foreach (var fe in fieldsContainer.Elements().Where(x => x.Name.LocalName.StartsWith("AxTableField", StringComparison.Ordinal)))
            {
                var fname = Local(fe, "Name");
                if (string.IsNullOrEmpty(fname)) continue;
                var edt = Local(fe, "ExtendedDataType");
                var ftype = Local(fe, "Type") ?? (string.IsNullOrEmpty(edt) ? null : "ExtendedDataType");
                var flabel = Local(fe, "Label");
                var mand = string.Equals(Local(fe, "Mandatory"), "Yes", StringComparison.OrdinalIgnoreCase);
                fields.Add(new ExtractedTableField(fname!, ftype, edt, flabel, mand));
            }
        }
        return new ExtractedTable(name, label, file, fields);
    }

    private static ExtractedClass? ParseClass(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var extends = Local(root, "Extends");
        var decl = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "SourceCode")
                    ?.Elements().FirstOrDefault(x => x.Name.LocalName == "Declaration")?.Value ?? string.Empty;
        var isAbstract = decl.Contains(" abstract ", StringComparison.Ordinal);
        var isFinal = decl.Contains(" final ", StringComparison.Ordinal);

        var methods = new List<ExtractedMethod>();
        var methodsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Methods");
        if (methodsContainer is not null)
        {
            foreach (var me in Children(methodsContainer, "Method"))
            {
                var mname = Local(me, "Name");
                if (string.IsNullOrEmpty(mname)) continue;
                var source = Local(me, "Source") ?? string.Empty;
                var signature = ExtractFirstLine(source);
                var returnType = InferReturnType(signature);
                var isStatic = signature.Contains(" static ", StringComparison.Ordinal);
                methods.Add(new ExtractedMethod(mname!, signature, returnType, isStatic));
            }
        }
        return new ExtractedClass(name, extends, isAbstract, isFinal, file, methods);
    }

    private static ExtractedEdt? ParseEdt(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var extends = Local(root, "Extends");
        var baseType = root.Name.LocalName.StartsWith("AxEdt", StringComparison.Ordinal)
            ? root.Name.LocalName.Substring("AxEdt".Length)
            : null;
        var label = Local(root, "Label");
        int? stringSize = int.TryParse(Local(root, "StringSize"), out var s) ? s : null;
        return new ExtractedEdt(name, extends, baseType, label, stringSize);
    }

    private static ExtractedEnum? ParseEnum(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var label = Local(root, "Label");
        var values = new List<ExtractedEnumValue>();
        var container = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "EnumValues");
        if (container is not null)
        {
            foreach (var v in Children(container, "AxEnumValue"))
            {
                var vname = Local(v, "Name");
                if (string.IsNullOrEmpty(vname)) continue;
                int? val = int.TryParse(Local(v, "Value"), out var i) ? i : null;
                values.Add(new ExtractedEnumValue(vname!, val, Local(v, "Label")));
            }
        }
        return new ExtractedEnum(name, label, values);
    }

    private static ExtractedMenuItem? ParseMenuItem(XDocument doc, string file, string kindDir)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var obj = Local(root, "Object");
        var objType = Local(root, "ObjectType");
        var label = Local(root, "Label");
        var kind = kindDir.Replace("AxMenuItem", "", StringComparison.Ordinal); // Display/Action/Output
        return new ExtractedMenuItem(name, kind, obj, objType, label);
    }

    private static IEnumerable<ExtractedLabel> ParseLabelFile(string file, IReadOnlyCollection<string>? langs)
    {
        // D365 label files come in two shapes:
        //   1. XML manifest at AxLabelFile/<Name>.xml that declares languages
        //   2. sibling .txt files `<Name>.<language>.label.txt` that carry
        //      the actual key=value payload (one entry per line, optionally
        //      preceded by a ;-comment).
        // We walk both so indexed labels match what Visual Studio resolves.

        XDocument? doc = null;
        try { doc = XDocument.Load(file, LoadOptions.None); } catch { }

        string logicalName = doc?.Root is { } xr ? (Local(xr, "Name") ?? Path.GetFileNameWithoutExtension(file))
                                                 : Path.GetFileNameWithoutExtension(file);

        // Strip the AxLabelFile_ prefix some shipments use ("AxLabelFile_SysLabel").
        const string prefix = "AxLabelFile_";
        if (logicalName.StartsWith(prefix, StringComparison.Ordinal))
            logicalName = logicalName.Substring(prefix.Length);

        // (1) inline <AxLabel> entries (rare but supported)
        if (doc?.Root is { } inlineRoot)
        {
            foreach (var loc in Children(inlineRoot, "Labels"))
            {
                var language = Local(loc, "Language") ?? "en-us";
                if (!LangPasses(langs, language)) continue;
                var entries = loc.Descendants().Where(x => x.Name.LocalName == "AxLabel");
                foreach (var entry in entries)
                {
                    var key = Local(entry, "Name");
                    if (string.IsNullOrEmpty(key)) continue;
                    yield return new ExtractedLabel(logicalName, language, key!, Local(entry, "Label"));
                }
            }
        }

        // (2) sibling .label.txt files — D365's canonical format.
        var dir = Path.GetDirectoryName(file)!;
        foreach (var txt in Directory.EnumerateFiles(dir, "*.label.txt", SearchOption.TopDirectoryOnly))
        {
            var fileName = Path.GetFileName(txt); // e.g. SysLabel.en-us.label.txt
            var nameNoExt = fileName.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - ".label.txt".Length)
                : Path.GetFileNameWithoutExtension(fileName);
            var dotIdx = nameNoExt.LastIndexOf('.');
            if (dotIdx < 0) continue;
            var labelFile = nameNoExt.Substring(0, dotIdx);
            var language = nameNoExt.Substring(dotIdx + 1);
            if (!LangPasses(langs, language)) continue;

            // Only index labels that belong to the manifest we're reading.
            if (!string.Equals(labelFile, logicalName, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(logicalName) &&
                doc is not null)
                continue;

            foreach (var entry in ReadLabelTxt(txt, labelFile, language))
                yield return entry;
        }
    }

    private static bool LangPasses(IReadOnlyCollection<string>? langs, string language) =>
        langs is null || langs.Count == 0 || langs.Contains(language, StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<ExtractedLabel> ReadLabelTxt(string path, string labelFile, string language)
    {
        // File format: "KEY=Value\n" with optional ";"-comments and BOM. Values
        // may contain '=' so we split on the first occurrence only. Keys are
        // case-sensitive in D365 (labels map directly to AOT resource ids).
        using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var key = trimmed.Substring(0, eq).TrimEnd();
            var value = trimmed.Substring(eq + 1);
            if (string.IsNullOrEmpty(key)) continue;
            yield return new ExtractedLabel(labelFile, language, key, value);
        }
    }

    private static string ExtractFirstLine(string source)
    {
        var idx = source.IndexOfAny(new[] { '\r', '\n' });
        var first = idx < 0 ? source : source[..idx];
        return first.Trim();
    }

    private static string? InferReturnType(string signature)
    {
        // Heuristic: "public void foo(...)" -> "void"; skip access modifiers.
        var tokens = signature.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var modifiers = new HashSet<string>(StringComparer.Ordinal) { "public", "protected", "private", "static", "final", "abstract", "client", "server" };
        foreach (var tok in tokens)
        {
            if (modifiers.Contains(tok)) continue;
            if (tok.Contains('(')) return null; // method name without explicit return type
            return tok;
        }
        return null;
    }

    private static string InferTargetFromExtensionName(string extName)
    {
        // "CustTable_Extension" -> "CustTable"
        const string suffix = "_Extension";
        return extName.EndsWith(suffix, StringComparison.Ordinal)
            ? extName[..^suffix.Length]
            : string.Empty;
    }
}
