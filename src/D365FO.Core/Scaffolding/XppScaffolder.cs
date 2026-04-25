using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Generates AOT-shaped XML for new D365FO objects. Outputs are intentionally
/// minimal; the point is to scaffold a compile-safe skeleton that Visual
/// Studio / the workspace tooling can pick up. All generators return the XML
/// as <see cref="XDocument"/> so the caller can validate, format, or round-trip
/// before writing to disk (see <see cref="ScaffoldFileWriter"/>).
/// </summary>
public static class XppScaffolder
{
    public static XDocument Table(
        string name,
        string? label = null,
        IEnumerable<TableFieldSpec>? fields = null,
        TablePattern pattern = TablePattern.None,
        TableStorage storage = TableStorage.RegularTable,
        IEnumerable<string>? primaryKeyFields = null)
    {
        // Resolve effective field list: caller-supplied wins; otherwise use the
        // pattern preset (if any). When neither is supplied, emit nothing —
        // the AOT will not compile a table with zero fields, but that is the
        // correct error for the caller, not something we silently paper over.
        var supplied = (fields ?? Enumerable.Empty<TableFieldSpec>()).ToList();
        var effectiveFields = supplied.Count > 0
            ? supplied
            : TablePatternPresets.DefaultFieldsFor(pattern).ToList();

        var fieldEls = effectiveFields.Select(f =>
        {
            var el = new XElement("AxTableField",
                new XElement("Name", f.Name),
                new XElement("ExtendedDataType", f.Edt ?? "Name"));
            if (!string.IsNullOrEmpty(f.Label)) el.Add(new XElement("Label", f.Label));
            if (f.Mandatory) el.Add(new XElement("Mandatory", "Yes"));
            return el;
        });

        // Pick the primary-key / alternate-key index. Order of preference:
        //   1. caller-supplied --primary-key list (must reference real fields).
        //   2. all mandatory fields from the pattern preset (typical D365FO shape).
        //   3. first field as a fallback so BPCheckAlternateKeyAbsent never trips.
        var pkNames = (primaryKeyFields ?? Enumerable.Empty<string>())
            .Where(n => !string.IsNullOrWhiteSpace(n) &&
                        effectiveFields.Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (pkNames.Count == 0)
        {
            pkNames = effectiveFields.Where(f => f.Mandatory).Select(f => f.Name).ToList();
        }
        if (pkNames.Count == 0 && effectiveFields.Count > 0)
        {
            pkNames = new List<string> { effectiveFields[0].Name };
        }

        XElement? indexesEl = null;
        if (pkNames.Count > 0)
        {
            indexesEl = new XElement("Indexes",
                new XElement("AxTableIndex",
                    new XElement("Name", "PrimaryIdx"),
                    new XElement("AlternateKey", "Yes"),
                    new XElement("AllowDuplicates", "No"),
                    new XElement("Fields",
                        pkNames.Select(n => new XElement("AxTableIndexField",
                            new XElement("DataField", n))))));
        }

        // TableGroup / TableType: only emit when the caller asked for them.
        // An absent element means the AOT default applies (Miscellaneous /
        // Regular) — we never want to flip a default by accident.
        var tableGroup = TablePatternPresets.TableGroupFor(pattern);
        var tableType  = storage == TableStorage.RegularTable ? null : TablePatternPresets.TableTypeFor(storage);

        return new XDocument(
            new XElement("AxTable",
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                tableGroup is null ? null : new XElement("TableGroup", tableGroup),
                tableType  is null ? null : new XElement("TableType",  tableType),
                new XElement("Fields", fieldEls),
                new XElement("FieldGroups",
                    new XElement("AxTableFieldGroup",
                        new XElement("Name", "AutoReport"))),
                indexesEl));
    }

    public static XDocument Class(string name, string? extends = null, bool isFinal = true)
    {
        var decl = isFinal ? "public final class" : "public class";
        var extendsClause = string.IsNullOrEmpty(extends) ? string.Empty : $" extends {extends}";
        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", name),
                extends is null ? null : new XElement("Extends", extends),
                new XElement("SourceCode",
                    new XElement("Declaration",
                        $"{decl} {name}{extendsClause}\n{{\n}}"))));
    }

    public static XDocument CocExtension(string targetClass, params string[] wrappedMethods)
    {
        var name = targetClass + "_Extension";
        var methodEls = wrappedMethods.Select(m => new XElement("Method",
            new XElement("Name", m),
            new XElement("Source",
                $"public void {m}()\n{{\n    next {m}();\n    // extension logic here\n}}\n")));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", name),
                new XElement("SourceCode",
                    new XElement("Declaration",
                        $"[ExtensionOf(classStr({targetClass}))]\nfinal class {name}\n{{\n}}")),
                new XElement("Methods", methodEls)));
    }

    /// <summary>
    /// Scaffolds a pattern-correct <c>AxForm</c>. Returns the rendered XML as
    /// a string (preserving the exact element ordering expected by the AOT)
    /// so the caller can hand it to <see cref="ScaffoldFileWriter.Write(string, string, bool)"/>.
    /// Mirrors upstream MCP <c>generate_smart_form</c>.
    /// </summary>
    /// <param name="formName">AOT form name (also used for <c>classDeclaration</c>).</param>
    /// <param name="dataSourceTable">Primary datasource table (optional).</param>
    /// <param name="pattern">D365FO form pattern; defaults to <see cref="FormPattern.SimpleList"/>.</param>
    /// <param name="caption">Optional caption / label string.</param>
    /// <param name="gridFields">Field names rendered as grid / detail columns.</param>
    /// <param name="sections">Sections for <c>TableOfContents</c> / <c>Dialog</c> / <c>Workspace</c>.</param>
    /// <param name="linesTable">Lines datasource table for <see cref="FormPattern.DetailsTransaction"/>.</param>
    public static string Form(
        string formName,
        string? dataSourceTable = null,
        FormPattern pattern = FormPattern.SimpleList,
        string? caption = null,
        IReadOnlyList<string>? gridFields = null,
        IReadOnlyList<FormSectionSpec>? sections = null,
        string? linesTable = null)
    {
        var opt = new FormTemplateOptions
        {
            FormName     = formName,
            DsName       = dataSourceTable,
            DsTable      = dataSourceTable,
            Caption      = caption,
            GridFields   = gridFields ?? Array.Empty<string>(),
            Sections     = sections ?? Array.Empty<FormSectionSpec>(),
            LinesDsName  = linesTable,
            LinesDsTable = linesTable,
        };
        return FormPatternTemplates.Build(pattern, opt);
    }

    /// <summary>
    /// Legacy <c>SimpleList</c> scaffolder kept for backwards compatibility.
    /// Prefer <see cref="Form"/> with an explicit <see cref="FormPattern"/>.
    /// </summary>
    [Obsolete("Use XppScaffolder.Form(name, table, FormPattern.SimpleList, ...) instead.")]
    public static XDocument SimpleList(string formName, string dataSourceTable)
    {
        return new XDocument(
            new XElement("AxForm",
                new XElement("Name", formName),
                new XElement("DataSources",
                    new XElement("AxFormDataSource",
                        new XElement("Name", dataSourceTable),
                        new XElement("Table", dataSourceTable))),
                new XElement("Design",
                    new XElement("Pattern", "SimpleList"),
                    new XElement("PatternVersion", "1.0"))));
    }

    /// <summary>
    /// Scaffolds a minimal <c>AxDataEntityView</c> — data entity with a
    /// single table datasource and public OData names derived from the table
    /// by convention (<c>&lt;Table&gt;Entity</c>, collection plural).
    /// </summary>
    public static XDocument DataEntity(
        string entityName,
        string table,
        string? publicEntityName = null,
        string? publicCollectionName = null,
        IEnumerable<EntityFieldSpec>? fields = null)
    {
        var pubEntity = string.IsNullOrEmpty(publicEntityName) ? entityName : publicEntityName;
        var pubColl = string.IsNullOrEmpty(publicCollectionName) ? pubEntity + "s" : publicCollectionName;

        var fieldEls = (fields ?? Enumerable.Empty<EntityFieldSpec>()).Select(f =>
            new XElement("AxDataEntityViewField",
                new XElement("Name", f.Name),
                new XElement("DataField", f.DataField ?? f.Name),
                new XElement("DataSource", table),
                f.IsMandatory ? new XElement("IsMandatory", "Yes") : null));

        return new XDocument(
            new XElement("AxDataEntityView",
                new XElement("Name", entityName),
                new XElement("PublicEntityName", pubEntity),
                new XElement("PublicCollectionName", pubColl),
                new XElement("DataManagementEnabled", "Yes"),
                new XElement("IsPublic", "Yes"),
                new XElement("DataSources",
                    new XElement("AxQuerySimpleRootDataSource",
                        new XElement("Name", table),
                        new XElement("Table", table))),
                new XElement("Fields", fieldEls)));
    }

    /// <summary>
    /// Scaffolds a Table/Form/Edt/Enum extension. Name follows the D365FO
    /// convention <c>&lt;Target&gt;.&lt;Suffix&gt;</c> (dot-separated).
    /// </summary>
    public static XDocument Extension(string kind, string targetName, string suffix)
    {
        var elementName = kind switch
        {
            "Table" => "AxTableExtension",
            "Form" => "AxFormExtension",
            "Edt" => "AxEdtExtension",
            "Enum" => "AxEnumExtension",
            _ => throw new ArgumentException($"Unsupported extension kind: {kind}", nameof(kind)),
        };

        return new XDocument(
            new XElement(elementName,
                new XElement("Name", $"{targetName}.{suffix}")));
    }

    /// <summary>
    /// Scaffolds an event-handler class (SubscribesTo on a form/table/class
    /// delegate). Body is a <c>next</c>-free stub; handlers intentionally
    /// don't chain like CoC.
    /// </summary>
    public static XDocument EventHandler(
        string className,
        string sourceKind,
        string sourceObject,
        string eventName,
        string handlerMethod = "OnEvent")
    {
        var attr = sourceKind switch
        {
            "Form" => $"FormEventHandler(formStr({sourceObject}), FormEventType::{eventName})",
            "FormDataSource" => $"FormDataSourceEventHandler(formDataSourceStr({sourceObject}), FormDataSourceEventType::{eventName})",
            "Table" => $"DataEventHandler(tableStr({sourceObject}), DataEventType::{eventName})",
            "Class" => $"SubscribesTo(classStr({sourceObject}), delegateStr({sourceObject}, {eventName}))",
            _ => $"SubscribesTo({sourceKind}, {sourceObject}, {eventName})",
        };

        var src =
            $"public static class {className}\n{{\n" +
            $"    [{attr}]\n" +
            $"    public static void {handlerMethod}(XppPrePostArgs args)\n" +
            "    {\n        // handler logic here\n    }\n}}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", src))));
    }

    /// <summary>Scaffolds an <c>AxSecurityPrivilege</c> with a single entry point.</summary>
    public static XDocument Privilege(
        string name, string entryPointName, string entryPointKind,
        string? entryPointObject = null, string? access = "Read", string? label = null)
    {
        return new XDocument(
            new XElement("AxSecurityPrivilege",
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("EntryPoints",
                    new XElement("AxSecurityEntryPointReference",
                        new XElement("Name", entryPointName),
                        new XElement("ObjectName", entryPointObject ?? entryPointName),
                        new XElement("ObjectType", entryPointKind),
                        new XElement("AccessLevel", access ?? "Read")))));
    }

    /// <summary>Scaffolds an <c>AxSecurityDuty</c> grouping given privileges.</summary>
    public static XDocument Duty(string name, IEnumerable<string> privileges, string? label = null)
    {
        return new XDocument(
            new XElement("AxSecurityDuty",
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("PrivilegeReferences",
                    privileges.Select(p =>
                        new XElement("AxSecurityPrivilegeReference",
                            new XElement("Name", p))))));
    }

    /// <summary>
    /// Scaffolds an <c>AxSecurityRole</c> that aggregates duties and/or
    /// privileges. D365FO best practice is to prefer duties, but a role may
    /// reference privileges directly for narrow use-cases.
    /// </summary>
    public static XDocument Role(
        string name,
        IEnumerable<string>? duties = null,
        IEnumerable<string>? privileges = null,
        string? label = null,
        string? description = null)
    {
        var dutyRefs = (duties ?? Enumerable.Empty<string>())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => new XElement("AxSecurityDutyReference", new XElement("Name", d)))
            .ToList();
        var privRefs = (privileges ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new XElement("AxSecurityPrivilegeReference", new XElement("Name", p)))
            .ToList();

        return new XDocument(
            new XElement("AxSecurityRole",
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                string.IsNullOrEmpty(description) ? null : new XElement("Description", description),
                dutyRefs.Count == 0 ? null : new XElement("Duties", dutyRefs),
                privRefs.Count == 0 ? null : new XElement("Privileges", privRefs)));
    }

    /// <summary>
    /// Add duty / privilege references to an existing <c>AxSecurityRole</c>
    /// document. Idempotent: duplicate refs are not appended. Returns
    /// <c>true</c> when the document was modified.
    /// </summary>
    public static bool AddToRole(
        XDocument roleDoc,
        IEnumerable<string>? duties = null,
        IEnumerable<string>? privileges = null)
    {
        ArgumentNullException.ThrowIfNull(roleDoc);
        var root = roleDoc.Root ?? throw new ArgumentException("Role document has no root.", nameof(roleDoc));
        if (root.Name.LocalName != "AxSecurityRole")
            throw new ArgumentException($"Expected <AxSecurityRole>, got <{root.Name.LocalName}>.", nameof(roleDoc));

        var changed = false;
        changed |= AppendRefs(root, "Duties", "AxSecurityDutyReference", duties);
        changed |= AppendRefs(root, "Privileges", "AxSecurityPrivilegeReference", privileges);
        return changed;
    }

    private static bool AppendRefs(XElement root, string containerName, string itemName, IEnumerable<string>? values)
    {
        var items = (values ?? Enumerable.Empty<string>())
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        if (items.Count == 0) return false;

        var container = root.Element(containerName);
        if (container is null)
        {
            container = new XElement(containerName);
            root.Add(container);
        }

        var existing = new HashSet<string>(
            container.Elements(itemName).Select(e => e.Element("Name")?.Value ?? "")
                     .Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.OrdinalIgnoreCase);

        var changed = false;
        foreach (var v in items)
        {
            if (existing.Add(v))
            {
                container.Add(new XElement(itemName, new XElement("Name", v)));
                changed = true;
            }
        }
        return changed;
    }
}

public sealed record EntityFieldSpec(string Name, string? DataField, bool IsMandatory);

public sealed record TableFieldSpec(string Name, string? Edt, string? Label, bool Mandatory);

/// <summary>
/// Writes a scaffolded XML document atomically: a .tmp sibling is written and
/// then moved onto the target path. Any pre-existing file is kept as .bak
/// unless the <c>overwrite</c> flag is false (in which case the operation
/// fails before touching disk).
/// </summary>
public static class ScaffoldFileWriter
{
    public sealed record WriteResult(string Path, long Bytes, string? BackupPath);

    public static WriteResult Write(XDocument doc, string path, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        return WriteCore(doc.ToString(SaveOptions.None), path, overwrite, declarationOnSaveFromXDoc: true, doc);
    }

    /// <summary>
    /// Writes a pre-rendered XML string atomically. Used by
    /// <see cref="FormPatternTemplates"/> which produces formatted AOT XML
    /// directly (preserving exact element ordering required by D365FO).
    /// </summary>
    public static WriteResult Write(string xml, string path, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(xml);
        return WriteCore(xml, path, overwrite, declarationOnSaveFromXDoc: false, null);
    }

    private static WriteResult WriteCore(string xml, string path, bool overwrite, bool declarationOnSaveFromXDoc, XDocument? doc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string? backup = null;
        if (File.Exists(full))
        {
            if (!overwrite)
                throw new IOException($"Target exists; pass --overwrite to replace: {full}");
            backup = full + ".bak";
            if (File.Exists(backup)) File.Delete(backup);
            File.Move(full, backup);
        }

        var tmp = full + ".tmp";
        if (declarationOnSaveFromXDoc && doc is not null)
        {
            using var fs = File.Create(tmp);
            doc.Save(fs);
        }
        else
        {
            File.WriteAllText(tmp, xml);
        }
        File.Move(tmp, full);
        var bytes = new FileInfo(full).Length;
        return new WriteResult(full, bytes, backup);
    }
}
