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
            // Skip FormAdaptor companion packages (e.g. *_FormAdaptor, *FormAdaptor).
            // These are generated shims containing no real AOT metadata and
            // the upstream d365fo-mcp-server ignores them as well.
            if (IsFormAdaptorPackage(Path.GetFileName(packageDir))) continue;
            var descriptors = LoadPackageDescriptors(packageDir);
            foreach (var modelDir in EnumerateDirectories(packageDir))
            {
                // Skip anything that does not look like a model folder.
                if (!HasAnyAotSubfolder(modelDir)) continue;
                if (IsFormAdaptorPackage(Path.GetFileName(modelDir))) continue;
                var model = Path.GetFileName(modelDir)!;
                ExtractBatch batch;
                try
                {
                    batch = ExtractModel(modelDir, model, labelLanguages, matcher.IsMatch(model));
                    if (descriptors.TryGetValue(model, out var d))
                        batch = batch with { Publisher = d.Publisher, Layer = d.Layer, Dependencies = d.Dependencies };
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

    private static Dictionary<string, (string? Publisher, string? Layer, IReadOnlyList<string> Dependencies)> LoadPackageDescriptors(string packageDir)
    {
        var result = new Dictionary<string, (string?, string?, IReadOnlyList<string>)>(StringComparer.OrdinalIgnoreCase);
        var descDir = Path.Combine(packageDir, "Descriptor");
        if (!Directory.Exists(descDir)) return result;
        foreach (var file in Directory.EnumerateFiles(descDir, "*.xml", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var doc = XDocument.Load(file, LoadOptions.None);
                var root = doc.Root;
                if (root is null) continue;
                var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
                var publisher = Local(root, "Publisher");
                var layer = Local(root, "Layer");
                // ModuleReferences contains <string>Foo</string> entries. The
                // exact wrapper element name varies across versions (ModuleReferences
                // / ModelReferences), so we match by local name.
                var deps = root.Descendants()
                    .Where(e => e.Name.LocalName is "ModuleReferences" or "ModelReferences")
                    .SelectMany(e => e.Elements())
                    .Where(e => e.Name.LocalName == "string" || e.Name.LocalName == "ModuleReference")
                    .Select(e => (e.Value ?? string.Empty).Trim())
                    .Where(s => s.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                result[name] = (publisher, layer, deps);
            }
            catch { /* malformed descriptor — ignore */ }
        }
        return result;
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
        var forms = ReadAll(Path.Combine(modelRoot, "AxForm"), ParseForm);
        var menuItems = new List<ExtractedMenuItem>();
        foreach (var kind in new[] { "AxMenuItemDisplay", "AxMenuItemAction", "AxMenuItemOutput" })
            menuItems.AddRange(ReadAll(Path.Combine(modelRoot, kind), (doc, path) => ParseMenuItem(doc, path, kind)));

        // Object extensions: each AxXxxExtension/*.xml represents one extension
        // whose root element name is e.g. "CustTable.FleetExtension" (Name element).
        var extensions = new List<ExtractedObjectExtension>();
        foreach (var (dir, kind) in new[] {
            ("AxTableExtension", "Table"),
            ("AxFormExtension", "Form"),
            ("AxEdtExtension", "Edt"),
            ("AxEnumExtension", "Enum"),
            ("AxViewExtension", "View"),
            ("AxMapExtension", "Map"),
        })
        {
            var extDir = Path.Combine(modelRoot, dir);
            if (!Directory.Exists(extDir)) continue;
            foreach (var f in Directory.EnumerateFiles(extDir, "*.xml", SearchOption.TopDirectoryOnly))
            {
                var ext = ParseObjectExtension(kind, f);
                if (ext is not null) extensions.Add(ext);
            }
        }

        var labels = new List<ExtractedLabel>();
        var labelsDir = Path.Combine(modelRoot, "AxLabelFile");
        if (Directory.Exists(labelsDir))
        {
            // Modern D365 layout: AxLabelFile/LabelResources/<lang>/<Name>.<lang>.label.txt
            // (the AxLabelFile/*.xml manifests only declare supported languages).
            // Legacy layout kept the .label.txt next to the manifest.
            foreach (var txt in Directory.EnumerateFiles(labelsDir, "*.label.txt", SearchOption.AllDirectories))
            {
                foreach (var entry in ReadLabelTxtFromPath(txt, labelLanguages))
                    labels.Add(entry);
            }

            // Inline <AxLabel> entries inside the XML manifest (rare).
            foreach (var manifest in Directory.EnumerateFiles(labelsDir, "*.xml", SearchOption.TopDirectoryOnly))
                labels.AddRange(ParseLabelManifestInline(manifest, labelLanguages));
        }

        var coc = classes.SelectMany(DetectCoc).ToList();
        var subscribers = classes.SelectMany(DetectSubscribers).ToList();

        var roles = ReadAll(Path.Combine(modelRoot, "AxSecurityRole"), ParseSecurityRole);
        var duties = ReadAll(Path.Combine(modelRoot, "AxSecurityDuty"), ParseSecurityDuty);
        var privileges = ReadAll(Path.Combine(modelRoot, "AxSecurityPrivilege"), ParseSecurityPrivilege);

        var queries = ReadAll(Path.Combine(modelRoot, "AxQuery"), ParseQuery);
        var simpleQueries = ReadAll(Path.Combine(modelRoot, "AxQuerySimple"), ParseQuery);
        if (simpleQueries.Count > 0) queries = queries.Concat(simpleQueries).ToList();
        var views = ReadAll(Path.Combine(modelRoot, "AxView"), ParseView);
        var dataEntities = ReadAll(Path.Combine(modelRoot, "AxDataEntityView"), ParseDataEntity);
        var reports = new List<ExtractedReport>();
        reports.AddRange(ReadAll(Path.Combine(modelRoot, "AxReport"), (d, f) => ParseReport(d, f, "Rdl")));
        reports.AddRange(ReadAll(Path.Combine(modelRoot, "AxReportSsrs"), (d, f) => ParseReport(d, f, "Ssrs")));
        var services = ReadAll(Path.Combine(modelRoot, "AxService"), ParseService);
        var serviceGroups = ReadAll(Path.Combine(modelRoot, "AxServiceGroup"), ParseServiceGroup);
        var workflowTypes = ReadAll(Path.Combine(modelRoot, "AxWorkflowType"), ParseWorkflowType);

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
            Labels: labels)
        {
            Forms = forms,
            Extensions = extensions,
            EventSubscribers = subscribers,
            Roles = roles,
            Duties = duties,
            Privileges = privileges,
            Queries = queries,
            Views = views,
            DataEntities = dataEntities,
            Reports = reports,
            Services = services,
            ServiceGroups = serviceGroups,
            WorkflowTypes = workflowTypes,
        };
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root);
        }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// FormAdaptor packages are generated shims (e.g. <c>ApplicationSuiteFormAdaptor</c>,
    /// <c>Dimensions_FormAdaptor</c>). They contain no real AOT metadata and are
    /// excluded from the index — matching the upstream d365fo-mcp-server behavior.
    /// </summary>
    public static bool IsFormAdaptorPackage(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name.EndsWith("FormAdaptor", StringComparison.OrdinalIgnoreCase) ||
         name.EndsWith("_FormAdaptor", StringComparison.OrdinalIgnoreCase));

    private static bool HasAnyAotSubfolder(string dir)
    {
        foreach (var s in new[] {
            "AxTable", "AxClass", "AxEdt", "AxEnum", "AxLabelFile", "AxForm",
            "AxTableExtension", "AxFormExtension", "AxEdtExtension", "AxEnumExtension",
            "AxSecurityRole", "AxSecurityDuty", "AxSecurityPrivilege",
            "AxMenuItemDisplay", "AxMenuItemAction", "AxMenuItemOutput",
            "AxQuery", "AxQuerySimple", "AxView", "AxDataEntityView",
            "AxReport", "AxReportSsrs", "AxService", "AxServiceGroup", "AxWorkflowType",
        })
            if (Directory.Exists(Path.Combine(dir, s))) return true;
        return false;
    }

    private List<T> ReadAll<T>(string dir, Func<XDocument, string, T?> parser) where T : class
    {
        if (!Directory.Exists(dir)) return new List<T>();
        var files = Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly).ToArray();
        if (files.Length == 0) return new List<T>();

        var bag = new System.Collections.Concurrent.ConcurrentBag<T>();
        var opts = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount };
        Parallel.ForEach(files, opts, file =>
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(file, LoadOptions.None);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "skip malformed {File}", file);
                return;
            }
            try
            {
                var parsed = parser(doc, file);
                if (parsed is not null) bag.Add(parsed);
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "parser failed on {File}", file);
            }
        });
        return bag.ToList();
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

        var relations = new List<ExtractedTableRelation>();
        var relContainer = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Relations");
        if (relContainer is not null)
        {
            foreach (var re in relContainer.Elements().Where(x => x.Name.LocalName.StartsWith("AxTableRelation", StringComparison.Ordinal)))
            {
                var related = Local(re, "RelatedTable");
                if (string.IsNullOrEmpty(related)) continue;
                relations.Add(new ExtractedTableRelation(
                    Local(re, "Name"),
                    related!,
                    Local(re, "Cardinality"),
                    Local(re, "RelationshipType")));
            }
        }

        var indexes = new List<ExtractedTableIndex>();
        var idxContainer = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Indexes");
        if (idxContainer is not null)
        {
            foreach (var ie in idxContainer.Elements().Where(x => x.Name.LocalName.StartsWith("AxTableIndex", StringComparison.Ordinal)))
            {
                var iname = Local(ie, "Name");
                if (string.IsNullOrEmpty(iname)) continue;
                var allowDup = !string.Equals(Local(ie, "AllowDuplicates"), "No", StringComparison.OrdinalIgnoreCase);
                var altKey = string.Equals(Local(ie, "AlternateKey"), "Yes", StringComparison.OrdinalIgnoreCase);
                var idxFieldsContainer = ie.Elements().FirstOrDefault(x => x.Name.LocalName == "Fields");
                var idxFields = new List<string>();
                if (idxFieldsContainer is not null)
                {
                    foreach (var fe in idxFieldsContainer.Elements().Where(x => x.Name.LocalName.StartsWith("AxTableIndex", StringComparison.Ordinal)))
                    {
                        var df = Local(fe, "DataField");
                        if (!string.IsNullOrEmpty(df)) idxFields.Add(df!);
                    }
                }
                indexes.Add(new ExtractedTableIndex(iname!, allowDup, altKey, idxFields));
            }
        }

        var deleteActions = new List<ExtractedTableDeleteAction>();
        var daContainer = root.Elements().FirstOrDefault(x => x.Name.LocalName == "DeleteActions");
        if (daContainer is not null)
        {
            foreach (var de in daContainer.Elements().Where(x => x.Name.LocalName.StartsWith("AxTableDeleteAction", StringComparison.Ordinal)))
            {
                var related = Local(de, "RelatedTable");
                if (string.IsNullOrEmpty(related)) continue;
                deleteActions.Add(new ExtractedTableDeleteAction(
                    Local(de, "Name"),
                    related!,
                    Local(de, "DeleteAction")));
            }
        }

        var methods = ExtractMethods(root);

        return new ExtractedTable(name, label, file, fields)
        {
            Relations = relations,
            Indexes = indexes,
            DeleteActions = deleteActions,
            Methods = methods,
        };
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

        var (methods, methodSources) = ExtractMethodsWithSources(root);

        var attributes = new List<ExtractedClassAttribute>();
        foreach (var a in ParseAttributes(decl))
            attributes.Add(new ExtractedClassAttribute(null, a.Name, a.Args));
        foreach (var (mname, src) in methodSources)
        {
            foreach (var a in ParseAttributes(src))
                attributes.Add(new ExtractedClassAttribute(mname, a.Name, a.Args));
        }

        return new ExtractedClass(name, extends, isAbstract, isFinal, file, methods, decl)
        {
            Attributes = attributes,
        };
    }

    private static IReadOnlyList<ExtractedMethod> ExtractMethods(XElement root)
    {
        var (methods, _) = ExtractMethodsWithSources(root);
        return methods;
    }

    private static (IReadOnlyList<ExtractedMethod> Methods, List<(string Name, string Source)> Sources) ExtractMethodsWithSources(XElement root)
    {
        var methods = new List<ExtractedMethod>();
        var sources = new List<(string, string)>();
        // Pick the *first* <Methods> container that is a direct child of the
        // root or of a SourceCode/Methods block. AxForm contains nested
        // <Methods> under DataSources etc.; we want only the top-level one.
        var methodsContainer = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Methods")
            ?? root.Descendants().FirstOrDefault(x =>
                x.Name.LocalName == "Methods" &&
                x.Parent is { } p && p.Name.LocalName == "SourceCode");
        if (methodsContainer is null) return (methods, sources);

        foreach (var me in Children(methodsContainer, "Method"))
        {
            var mname = Local(me, "Name");
            if (string.IsNullOrEmpty(mname)) continue;
            var source = Local(me, "Source") ?? string.Empty;
            var signature = ExtractSignatureLine(source);
            var returnType = InferReturnType(signature);
            var isStatic = signature.Contains(" static ", StringComparison.Ordinal);
            methods.Add(new ExtractedMethod(mname!, signature, returnType, isStatic));
            sources.Add((mname!, source));
        }
        return (methods, sources);
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

    /// <summary>
    /// Read a single <c>*.label.txt</c> file. The language and logical file
    /// name are derived from the filename: <c>&lt;File&gt;.&lt;lang&gt;.label.txt</c>.
    /// </summary>
    private static IEnumerable<ExtractedLabel> ReadLabelTxtFromPath(string txt, IReadOnlyCollection<string>? langs)
    {
        var fileName = Path.GetFileName(txt); // e.g. SysLabel.en-us.label.txt
        var nameNoExt = fileName.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase)
            ? fileName.Substring(0, fileName.Length - ".label.txt".Length)
            : Path.GetFileNameWithoutExtension(fileName);
        var dotIdx = nameNoExt.LastIndexOf('.');
        if (dotIdx < 0) yield break;
        var labelFile = nameNoExt.Substring(0, dotIdx);
        var language = nameNoExt.Substring(dotIdx + 1);
        if (!LangPasses(langs, language)) yield break;

        foreach (var entry in ReadLabelTxt(txt, labelFile, language))
            yield return entry;
    }

    /// <summary>
    /// Parse inline <c>&lt;AxLabel&gt;</c> entries from an AxLabelFile manifest.
    /// Legacy shape; modern D365 uses LabelResources/&lt;lang&gt;/*.label.txt instead.
    /// </summary>
    private static IEnumerable<ExtractedLabel> ParseLabelManifestInline(string file, IReadOnlyCollection<string>? langs)
    {
        XDocument doc;
        try { doc = XDocument.Load(file, LoadOptions.None); }
        catch { yield break; }
        if (doc.Root is null) yield break;

        var logicalName = Local(doc.Root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        const string prefix = "AxLabelFile_";
        if (logicalName.StartsWith(prefix, StringComparison.Ordinal))
            logicalName = logicalName.Substring(prefix.Length);

        foreach (var loc in Children(doc.Root, "Labels"))
        {
            var language = Local(loc, "Language") ?? "en-us";
            if (!LangPasses(langs, language)) continue;
            foreach (var entry in loc.Descendants().Where(x => x.Name.LocalName == "AxLabel"))
            {
                var key = Local(entry, "Name");
                if (string.IsNullOrEmpty(key)) continue;
                yield return new ExtractedLabel(logicalName, language, key!, Local(entry, "Label"));
            }
        }
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

    private static string ExtractSignatureLine(string source)
    {
        // Find the first non-empty, non-attribute line — that's the actual
        // method signature. Attribute lines start with '[' and may span
        // multiple lines (e.g. [FormEventHandler(\n    formStr(X),\n ...)]).
        using var reader = new StringReader(source);
        int attrDepth = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var t = line.TrimStart();
            if (t.Length == 0) continue;
            if (attrDepth > 0)
            {
                foreach (var ch in t) { if (ch == '[') attrDepth++; else if (ch == ']') attrDepth--; }
                if (attrDepth > 0) continue;
                continue;
            }
            if (t.StartsWith('['))
            {
                attrDepth = 0;
                foreach (var ch in t) { if (ch == '[') attrDepth++; else if (ch == ']') attrDepth--; }
                continue;
            }
            return t.Trim();
        }
        return string.Empty;
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

    // Matches [ExtensionOf(classStr(Name))], [ExtensionOf(tableStr(Name))],
    // [ExtensionOf(formStr(Name))], [ExtensionOf(formDataSourceStr(Form, DS))], etc.
    // For data-source / control variants the *first* identifier is the target
    // form/table — that's what downstream queries key on.
    private static readonly System.Text.RegularExpressions.Regex ExtensionOfRx = new(
        @"\[\s*ExtensionOf\s*\(\s*(?<kind>\w+)Str\s*\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<ExtractedCoc> DetectCoc(ExtractedClass c)
    {
        string? target = null;
        if (!string.IsNullOrEmpty(c.Declaration))
        {
            var m = ExtensionOfRx.Match(c.Declaration!);
            if (m.Success) target = m.Groups["name"].Value;
        }
        // Fallback: naming convention + no base class.
        if (string.IsNullOrEmpty(target)
            && string.IsNullOrEmpty(c.Extends)
            && c.Name.EndsWith("_Extension", StringComparison.Ordinal))
        {
            target = InferTargetFromExtensionName(c.Name);
        }
        if (string.IsNullOrEmpty(target)) yield break;
        foreach (var m in c.Methods)
            yield return new ExtractedCoc(target!, m.Name, c.Name);
    }

    // -------- attributes & event subscribers --------

    private static readonly System.Text.RegularExpressions.Regex AttrRx = new(
        @"\[\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:\(\s*(?<args>[^\]]*)\))?\s*\]",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<(string Name, string Args)> ParseAttributes(string source)
    {
        if (string.IsNullOrEmpty(source)) yield break;
        foreach (System.Text.RegularExpressions.Match m in AttrRx.Matches(source))
        {
            var n = m.Groups["name"].Value;
            // Filter out obvious non-attributes: X++ does not have `[Hint]`-style
            // annotations inside method bodies, but `[i]` array indexers do appear.
            // Accept only PascalCase identifiers and attribute names ending in
            // "Handler", "Of", "SubscribesTo", "Attribute". Keep it permissive —
            // we later filter again when turning attributes into subscribers.
            if (n.Length < 2 || !char.IsUpper(n[0])) continue;
            yield return (n, m.Groups["args"].Value);
        }
    }

    // First two *Str(...) identifiers, e.g. formStr(CustTable) or
    // formDataSourceStr(CustTable, CustTable).
    private static readonly System.Text.RegularExpressions.Regex StrArgRx = new(
        @"(?<kind>\w+)Str\s*\(\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)(?:\s*,\s*(?<extra>[A-Za-z_][A-Za-z0-9_]*))?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    // FormEventType.Initialized, DataEventType.Inserting, etc.
    private static readonly System.Text.RegularExpressions.Regex EventTypeRx = new(
        @"(?<group>FormEventType|DataEventType|FormDataSourceEventType|FormControlEventType)\.(?<type>[A-Za-z_][A-Za-z0-9_]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static IEnumerable<ExtractedEventSubscriber> DetectSubscribers(ExtractedClass c)
    {
        foreach (var attr in c.Attributes)
        {
            if (attr.MethodName is null) continue; // method-level only
            var (kind, src, mem, et) = ClassifyHandler(attr.AttributeName, attr.RawArgs);
            if (kind is null || src is null) continue;
            yield return new ExtractedEventSubscriber(c.Name, attr.MethodName, kind, src, mem, et);
        }
    }

    private static (string? Kind, string? Source, string? Member, string? EventType) ClassifyHandler(string attrName, string args)
    {
        string? kind = attrName switch
        {
            "FormEventHandler" => "Form",
            "FormDataSourceEventHandler" => "FormDataSource",
            "FormControlEventHandler" => "FormControl",
            "DataEventHandler" => "Table",
            "SubscribesTo" => "Delegate",
            _ => null,
        };
        if (kind is null) return (null, null, null, null);

        var m = StrArgRx.Match(args ?? string.Empty);
        if (!m.Success) return (null, null, null, null);
        var source = m.Groups["name"].Value;
        var member = m.Groups["extra"].Success && m.Groups["extra"].Length > 0
            ? m.Groups["extra"].Value
            : null;

        string? eventType = null;
        var em = EventTypeRx.Match(args ?? string.Empty);
        if (em.Success) eventType = em.Groups["type"].Value;

        // [SubscribesTo(classStr(X), delegateStr(Y))] — Y arrives as a second
        // *Str(...) match; StrArgRx only returns the first, so look again.
        if (kind == "Delegate")
        {
            var matches = StrArgRx.Matches(args ?? string.Empty);
            if (matches.Count >= 2)
                member = matches[1].Groups["name"].Value;
        }
        return (kind, source, member, eventType);
    }

    // -------- forms --------

    private static ExtractedForm? ParseForm(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var datasources = new List<ExtractedFormDataSource>();
        var dsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "DataSources");
        if (dsContainer is not null)
        {
            foreach (var ds in dsContainer.Elements().Where(x => x.Name.LocalName.StartsWith("AxFormDataSource", StringComparison.Ordinal)))
            {
                var dsName = Local(ds, "Name");
                if (string.IsNullOrEmpty(dsName)) continue;
                datasources.Add(new ExtractedFormDataSource(dsName!, Local(ds, "Table"))
                {
                    JoinSource = Local(ds, "JoinSource"),
                });
            }
        }
        // <Design> sits directly under <AxForm>; pattern hints live there.
        var design = root.Elements().FirstOrDefault(x => x.Name.LocalName == "Design");
        string? pattern = null, patternVersion = null, style = null, titleDs = null;
        if (design is not null)
        {
            pattern = Local(design, "Pattern");
            patternVersion = Local(design, "PatternVersion");
            style = Local(design, "Style");
            titleDs = Local(design, "TitleDataSource");
        }
        return new ExtractedForm(name, file, datasources)
        {
            Pattern = pattern,
            PatternVersion = patternVersion,
            Style = style,
            TitleDataSource = titleDs,
        };
    }

    // -------- object extensions (AxTableExtension, AxFormExtension, ...) --------

    private static ExtractedObjectExtension? ParseObjectExtension(string kind, string file)
    {
        XDocument doc;
        try { doc = XDocument.Load(file, LoadOptions.None); }
        catch { return null; }
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        // Extension names follow "<Target>.<Suffix>" convention, e.g. "CustTable.Fleet".
        var dot = name.IndexOf('.');
        var target = dot > 0 ? name.Substring(0, dot) : name;
        return new ExtractedObjectExtension(kind, target, name, file);
    }

    // -------- security --------

    private static ExtractedSecurityRole? ParseSecurityRole(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var label = Local(root, "Label");
        var duties = CollectNames(root, "Duties");
        var privileges = CollectNames(root, "Privileges");
        return new ExtractedSecurityRole(name, label, duties, privileges);
    }

    private static ExtractedSecurityDuty? ParseSecurityDuty(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var label = Local(root, "Label");
        var privileges = CollectNames(root, "Privileges");
        return new ExtractedSecurityDuty(name, label, privileges);
    }

    private static ExtractedSecurityPrivilege? ParseSecurityPrivilege(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var label = Local(root, "Label");
        var eps = new List<ExtractedSecurityEntryPoint>();
        var epc = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "EntryPoints");
        if (epc is not null)
        {
            foreach (var ep in epc.Elements())
            {
                var obj = Local(ep, "ObjectName");
                if (string.IsNullOrEmpty(obj)) continue;
                eps.Add(new ExtractedSecurityEntryPoint(
                    obj!,
                    Local(ep, "ObjectType"),
                    Local(ep, "ObjectChildName"),
                    Local(ep, "AccessLevel")));
            }
        }
        return new ExtractedSecurityPrivilege(name, label, eps);
    }

    private static List<string> CollectNames(XElement root, string containerName)
    {
        var names = new List<string>();
        var c = root.Descendants().FirstOrDefault(x => x.Name.LocalName == containerName);
        if (c is null) return names;
        foreach (var child in c.Elements())
        {
            var n = Local(child, "Name");
            if (!string.IsNullOrEmpty(n)) names.Add(n!);
        }
        return names;
    }

    // -------- queries --------

    private static ExtractedQuery? ParseQuery(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var ds = new List<ExtractedQueryDataSource>();
        CollectQueryDataSources(root, parent: null, ds);
        return new ExtractedQuery(name, file, ds);
    }

    private static void CollectQueryDataSources(XElement parentEl, string? parent, List<ExtractedQueryDataSource> acc)
    {
        var container = parentEl.Elements().FirstOrDefault(x => x.Name.LocalName == "DataSources");
        if (container is null) return;
        foreach (var ds in container.Elements().Where(x => x.Name.LocalName.StartsWith("AxQuerySimpleDataSource", StringComparison.Ordinal)
                                                        || x.Name.LocalName.StartsWith("AxQueryDataSource", StringComparison.Ordinal)))
        {
            var dsName = Local(ds, "Name");
            if (string.IsNullOrEmpty(dsName)) continue;
            acc.Add(new ExtractedQueryDataSource(
                dsName!,
                Local(ds, "Table"),
                Local(ds, "JoinMode"),
                parent));
            CollectQueryDataSources(ds, dsName, acc);
        }
    }

    // -------- views --------

    private static ExtractedView? ParseView(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var label = Local(root, "Label");
        var query = Local(root, "Query");
        var fields = new List<ExtractedViewField>();
        var fieldsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fields");
        if (fieldsContainer is not null)
        {
            foreach (var fe in fieldsContainer.Elements())
            {
                var fn = Local(fe, "Name");
                if (string.IsNullOrEmpty(fn)) continue;
                fields.Add(new ExtractedViewField(fn!, Local(fe, "DataSource"), Local(fe, "DataField")));
            }
        }
        return new ExtractedView(name, label, query, file, fields);
    }

    // -------- data entities --------

    private static ExtractedDataEntity? ParseDataEntity(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var fields = new List<ExtractedDataEntityField>();
        var fieldsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fields");
        if (fieldsContainer is not null)
        {
            foreach (var fe in fieldsContainer.Elements())
            {
                var fn = Local(fe, "Name");
                if (string.IsNullOrEmpty(fn)) continue;
                var mand = string.Equals(Local(fe, "IsMandatory"), "Yes", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(Local(fe, "Mandatory"), "Yes", StringComparison.OrdinalIgnoreCase);
                var ro = string.Equals(Local(fe, "AllowEdit"), "No", StringComparison.OrdinalIgnoreCase)
                      || string.Equals(Local(fe, "IsReadOnly"), "Yes", StringComparison.OrdinalIgnoreCase);
                fields.Add(new ExtractedDataEntityField(fn!, Local(fe, "DataSource"), Local(fe, "DataField"), mand, ro));
            }
        }
        return new ExtractedDataEntity(
            name,
            Local(root, "PublicEntityName"),
            Local(root, "PublicCollectionName"),
            Local(root, "DataManagementStagingTable"),
            Local(root, "Query"),
            Local(root, "Label"),
            file,
            fields);
    }

    // -------- reports --------

    private static ExtractedReport? ParseReport(XDocument doc, string file, string kind)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var datasets = new List<ExtractedReportDataSet>();
        var dsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "DataSets");
        if (dsContainer is not null)
        {
            foreach (var ds in dsContainer.Elements())
            {
                var dsName = Local(ds, "Name");
                if (string.IsNullOrEmpty(dsName)) continue;
                // Query-backed DS has <Query> child; RDP has <DataSetProvider>/<Class>.
                var dsKind = Local(ds, "DataSetType")
                    ?? (!string.IsNullOrEmpty(Local(ds, "Query")) ? "Query"
                        : !string.IsNullOrEmpty(Local(ds, "Class")) ? "ReportDataProvider" : null);
                var target = Local(ds, "Query") ?? Local(ds, "Class");
                datasets.Add(new ExtractedReportDataSet(dsName!, dsKind, target));
            }
        }
        return new ExtractedReport(name, kind, file, datasets);
    }

    // -------- services --------

    private static ExtractedService? ParseService(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var cls = Local(root, "Class");
        var ops = new List<ExtractedServiceOperation>();
        var opsContainer = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Operations");
        if (opsContainer is not null)
        {
            foreach (var op in opsContainer.Elements())
            {
                var opName = Local(op, "Name");
                if (string.IsNullOrEmpty(opName)) continue;
                ops.Add(new ExtractedServiceOperation(opName!, Local(op, "Method") ?? Local(op, "MethodName")));
            }
        }
        return new ExtractedService(name, cls, file, ops);
    }

    private static ExtractedServiceGroup? ParseServiceGroup(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        var members = new List<string>();
        var container = root.Descendants().FirstOrDefault(x => x.Name.LocalName == "Services");
        if (container is not null)
        {
            foreach (var s in container.Elements())
            {
                var sn = Local(s, "Service") ?? Local(s, "Name");
                if (!string.IsNullOrEmpty(sn)) members.Add(sn!);
            }
        }
        return new ExtractedServiceGroup(name, file, members);
    }

    // -------- workflow types --------

    private static ExtractedWorkflowType? ParseWorkflowType(XDocument doc, string file)
    {
        var root = doc.Root;
        if (root is null) return null;
        var name = Local(root, "Name") ?? Path.GetFileNameWithoutExtension(file);
        return new ExtractedWorkflowType(
            name,
            Local(root, "Category"),
            Local(root, "DocumentClass") ?? Local(root, "Document"),
            file);
    }
}
