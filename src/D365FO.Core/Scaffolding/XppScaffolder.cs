using System.Xml.Linq;
using System.Xml;
using D365FO.Core.Guardrails;
using D365FO.Core.Journal;

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
    /// <summary>
    /// Scaffolds an <c>AxTable</c> XML skeleton: fields (from <paramref name="fields"/>
    /// or the <paramref name="pattern"/> preset), an alternate-key index, and the
    /// table group/type implied by the pattern and storage.
    /// </summary>
    /// <param name="name">Table name (the AOT <c>&lt;Name&gt;</c> and file stem).</param>
    /// <param name="label">Optional label; emitted as <c>&lt;Label&gt;</c> when set.</param>
    /// <param name="fields">
    /// Explicit field list. Wins over the pattern preset; when null/empty the preset
    /// for <paramref name="pattern"/> is used.
    /// </param>
    /// <param name="pattern">Table pattern preset driving default fields and table group.</param>
    /// <param name="storage">Storage kind; anything other than a regular table stamps <c>&lt;TableType&gt;</c>.</param>
    /// <param name="primaryKeyFields">
    /// Optional field names for the primary/alternate-key index. Names that don't match
    /// an effective field are ignored; falls back to mandatory fields, then the first field.
    /// </param>
    /// <param name="configurationKey">
    /// Optional <c>AxConfigurationKey</c> name gating the whole table. Emitted as
    /// <c>&lt;ConfigurationKey&gt;</c> only when supplied — an absent element means
    /// "not gated", which is the AOT default, so we never stamp one by accident.
    /// </param>
    /// <param name="formRef">
    /// Optional display menu item opened when a user drills into a record of this
    /// table ("Go to main table form"). Emitted as <c>&lt;FormRef&gt;</c> only when
    /// supplied.
    /// </param>
    /// <param name="edtBaseTypeResolver">
    /// Optional callback resolving an EDT name to its primitive base type
    /// (<c>String</c>, <c>Int</c>, <c>Enum</c>, …) — typically backed by the
    /// SQLite index (<c>MetadataRepository.GetEdt(name)?.BaseType</c>). Used to
    /// stamp each <c>&lt;AxTableField&gt;</c> with the concrete
    /// <c>i:type="AxTableField{Suffix}"</c> discriminator. <c>AxTableField</c>
    /// is an abstract base in <c>Microsoft.Dynamics.AX.Metadata.MetaModel</c>;
    /// without the discriminator the metadata reader throws "Cannot create an
    /// abstract class" and the table is invalid (issue #91). When the resolver
    /// is null or returns null, a heuristic over well-known system EDTs is used,
    /// defaulting to <c>AxTableFieldString</c>.
    /// </param>
    public static XDocument Table(
        string name,
        string? label = null,
        IEnumerable<TableFieldSpec>? fields = null,
        TablePattern pattern = TablePattern.None,
        TableStorage storage = TableStorage.RegularTable,
        IEnumerable<string>? primaryKeyFields = null,
        string? configurationKey = null,
        string? formRef = null,
        Func<string, string?>? edtBaseTypeResolver = null)
    {
        // Resolve effective field list: caller-supplied wins; otherwise use the
        // pattern preset (if any). When neither is supplied, emit nothing —
        // the AOT will not compile a table with zero fields, but that is the
        // correct error for the caller, not something we silently paper over.
        var supplied = (fields ?? Enumerable.Empty<TableFieldSpec>()).ToList();
        var effectiveFields = supplied.Count > 0
            ? supplied
            : TablePatternPresets.DefaultFieldsFor(pattern).ToList();

        // Microsoft's metadata reader requires the XMLSchema-instance namespace
        // and a concrete i:type on every polymorphic AxTableField (abstract base).
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var fieldEls = effectiveFields.Select(f =>
        {
            var edtName = f.Edt ?? "Name";
            var suffix  = TableFieldConcreteSuffix(edtName, edtBaseTypeResolver);
            var el = new XElement("AxTableField",
                new XAttribute(XName.Get("type", xsi.NamespaceName), $"AxTableField{suffix}"),
                new XElement("Name", f.Name),
                new XElement("ExtendedDataType", edtName));
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
                // Declare the i: prefix once on the root so every field's
                // i:type discriminator resolves without re-declaring per element.
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                // ConfigurationKey / FormRef join the same scalar-property block as
                // Label/TableGroup/TableType — omitted entirely when not supplied so
                // the AOT default (ungated, no drill-down form) still applies.
                string.IsNullOrEmpty(configurationKey) ? null : new XElement("ConfigurationKey", configurationKey),
                string.IsNullOrEmpty(formRef) ? null : new XElement("FormRef", formRef),
                tableGroup is null ? null : new XElement("TableGroup", tableGroup),
                tableType  is null ? null : new XElement("TableType",  tableType),
                // Standard models pin the clustered index to the PK index for
                // predictable physical ordering (validate xpp XML005).
                indexesEl is null ? null : new XElement("ClusteredIndex", "PrimaryIdx"),
                new XElement("Fields", fieldEls),
                // Visual Studio / the AOT stamps every new table with five default
                // field groups, all initially empty (issue #110). Ground-truthed
                // against shipped standard-model tables on a real AOS
                // (e.g. ApplicationCommon\AgentFeedUserPreference.xml,
                // ApplicationSuite\Foundation\AssetAddition.xml): VS does NOT
                // auto-populate AutoReport (or any of the five) with fields, even
                // once the table has real fields — the developer assigns them
                // later in the AOT designer. AutoIdentification is the only one
                // that carries a property, <AutoPopulate>Yes</AutoPopulate>. We
                // match that shape exactly instead of pre-populating AutoReport.
                //
                // "Overview" and "General" are NOT part of the AOT default
                // scaffold — they exist here only because this CLI's own
                // generated forms need them: every form pattern (SimpleList,
                // SimpleListDetails, DetailsMaster, DetailsTransaction)
                // references them via <DataGroup>, and the metadata reader
                // rejects the form with "Field group 'Overview' does not exist"
                // when the underlying table lacks them (issue #91 follow-up). They
                // are populated with every effective field so the grid/group
                // controls resolve.
                new XElement("FieldGroups",
                    BuildEmptyFieldGroup("AutoReport"),
                    BuildEmptyFieldGroup("AutoLookup"),
                    BuildEmptyFieldGroup("AutoIdentification", autoPopulate: true),
                    BuildEmptyFieldGroup("AutoSummary"),
                    BuildEmptyFieldGroup("AutoBrowse"),
                    BuildFieldGroup("Overview", effectiveFields),
                    BuildFieldGroup("General", effectiveFields)),
                indexesEl));
    }

    /// <summary>
    /// Builds one of the five default, initially-empty <c>AxTableFieldGroup</c>
    /// elements VS/AOT stamps on every new table (AutoReport, AutoLookup,
    /// AutoIdentification, AutoSummary, AutoBrowse). Only AutoIdentification
    /// carries <c>AutoPopulate</c>.
    /// </summary>
    private static XElement BuildEmptyFieldGroup(string name, bool autoPopulate = false) =>
        new XElement("AxTableFieldGroup",
            new XElement("Name", name),
            autoPopulate ? new XElement("AutoPopulate", "Yes") : null,
            new XElement("Fields"));

    private static XElement BuildFieldGroup(string name, IReadOnlyList<TableFieldSpec> fields) =>
        new XElement("AxTableFieldGroup",
            new XElement("Name", name),
            new XElement("Fields",
                fields.Select(f => new XElement("AxTableFieldGroupField",
                    new XElement("DataField", f.Name)))));

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
        => CocExtension(targetClass, targetKind: "class", wrappedMethods);

    /// <param name="targetClass">Name of the CoC target object.</param>
    /// <param name="targetKind">AOT kind of the CoC target: class | table | form | data-entity | map.
    /// Selects the [ExtensionOf] intrinsic — classStr/tableStr/formStr/… — which the
    /// X++ compiler verifies at compile time.</param>
    /// <param name="wrappedMethods">Methods that get a `next` wrapper.</param>
    public static XDocument CocExtension(string targetClass, string targetKind, params string[] wrappedMethods)
    {
        var name = targetClass + "_Extension";
        var intrinsic = targetKind.ToLowerInvariant() switch
        {
            // Data entities are table-like — their CoC uses tableStr too.
            "table" or "view" or "table-extension" or "data-entity" or "entity" => "tableStr",
            "form" => "formStr",
            "map" => "mapStr",
            _ => "classStr",
        };
        var methodEls = wrappedMethods.Select(m => new XElement("Method",
            new XElement("Name", m),
            new XElement("Source",
                $"public void {m}()\n{{\n    next {m}();\n    // extension logic here\n}}\n")));

        // <Methods> MUST be nested inside <SourceCode> (after <Declaration>) —
        // this is the canonical AxClass shape the metadata deserializer expects.
        // When emitted as a sibling of <SourceCode> the AOT/Visual Studio loads
        // the class with no methods at all (it looks "empty"). See issue #65.
        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", name),
                new XElement("SourceCode",
                    new XElement("Declaration",
                        $"[ExtensionOf({intrinsic}({targetClass}))]\nfinal class {name}\n{{\n}}"),
                    new XElement("Methods", methodEls))));
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
    /// <para>
    /// <c>DataManagementEnabled</c> defaults to <c>No</c>: nothing here creates
    /// the DIXF staging table, and <c>Yes</c> with a non-existent
    /// <c>&lt;Name&gt;Staging</c> table fails the very next build. Opting in via
    /// <paramref name="dataManagementEnabled"/> makes the caller responsible for
    /// the staging table existing (create it as its own table).
    /// </para>
    /// </summary>
    public static XDocument DataEntity(
        string entityName,
        string table,
        string? publicEntityName = null,
        string? publicCollectionName = null,
        IEnumerable<EntityFieldSpec>? fields = null,
        bool dataManagementEnabled = false,
        string? dataManagementStagingTable = null)
    {
        var pubEntity = string.IsNullOrEmpty(publicEntityName) ? entityName : publicEntityName;
        var pubColl = string.IsNullOrEmpty(publicCollectionName) ? pubEntity + "s" : publicCollectionName;

        var fieldEls = (fields ?? Enumerable.Empty<EntityFieldSpec>()).Select(f =>
            new XElement("AxDataEntityViewField",
                new XElement("Name", f.Name),
                new XElement("DataField", f.DataField ?? f.Name),
                new XElement("DataSource", table),
                f.IsMandatory ? new XElement("IsMandatory", "Yes") : null));

        var stagingTable = dataManagementEnabled
            ? new XElement("DataManagementStagingTable",
                string.IsNullOrEmpty(dataManagementStagingTable) ? entityName + "Staging" : dataManagementStagingTable)
            : new XElement("DataManagementStagingTable");

        return new XDocument(
            new XElement("AxDataEntityView",
                new XElement("Name", entityName),
                new XElement("PublicEntityName", pubEntity),
                new XElement("PublicCollectionName", pubColl),
                new XElement("DataManagementEnabled", dataManagementEnabled ? "Yes" : "No"),
                stagingTable,
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
    ///
    /// <para><c>AxEdtExtension</c> is an abstract base in the metadata model, exactly like
    /// <c>AxEdt</c>: the concrete subtype (<c>AxEdtStringExtension</c>, …) has to be
    /// pinned via <c>i:type</c> or Visual Studio's reader throws "Cannot create an
    /// abstract class" — and <see cref="ScaffoldFileWriter"/> refuses the write outright.
    /// Emitting the bare abstract root made <c>generate extension edt</c> fail
    /// unconditionally at write time, so the subtype is resolved here from the target
    /// EDT's base type via <paramref name="edtBaseTypeResolver"/> (the same index-backed
    /// resolver <c>generate table</c> uses for field subtypes).</para>
    /// </summary>
    public static XDocument Extension(
        string kind, string targetName, string suffix, Func<string, string?>? edtBaseTypeResolver = null)
    {
        var elementName = kind switch
        {
            "Table" => "AxTableExtension",
            "Form" => "AxFormExtension",
            "Edt" => "AxEdtExtension",
            "Enum" => "AxEnumExtension",
            _ => throw new ArgumentException($"Unsupported extension kind: {kind}", nameof(kind)),
        };

        var root = new XElement(elementName, new XElement("Name", $"{targetName}.{suffix}"));

        if (elementName == "AxEdtExtension")
        {
            XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
            var concrete = $"AxEdt{ConcreteFieldSuffix(targetName, edtBaseTypeResolver)}Extension";
            root.SetAttributeValue(XNamespace.Xmlns + "i", xsi.NamespaceName);
            root.SetAttributeValue(xsi + "type", concrete);
        }

        return new XDocument(root);
    }

    /// <summary>
    /// Scaffolds an <c>AxSecurityDutyExtension</c> — adds privileges to an
    /// EXISTING (often Microsoft-owned) duty without overlaying it. Name follows
    /// the dot-notation convention <c>&lt;BaseDuty&gt;.&lt;Suffix&gt;</c>, same
    /// as table/menu extensions.
    /// </summary>
    public static XDocument SecurityDutyExtension(
        string targetDuty, string suffix, IEnumerable<string>? privileges = null)
    {
        return new XDocument(
            new XElement("AxSecurityDutyExtension",
                new XElement("Name", $"{targetDuty}.{suffix}"),
                new XElement("Privileges",
                    (privileges ?? Enumerable.Empty<string>())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => new XElement("AxSecurityPrivilegeReference", new XElement("Name", p)))),
                new XElement("PropertyModifications")));
    }

    /// <summary>
    /// Scaffolds an <c>AxSecurityRoleExtension</c> — adds duties and/or
    /// privileges to an EXISTING (often Microsoft-owned) role without
    /// overlaying it. Name follows <c>&lt;BaseRole&gt;.&lt;Suffix&gt;</c>.
    /// </summary>
    public static XDocument SecurityRoleExtension(
        string targetRole, string suffix,
        IEnumerable<string>? duties = null, IEnumerable<string>? privileges = null)
    {
        return new XDocument(
            new XElement("AxSecurityRoleExtension",
                new XElement("Name", $"{targetRole}.{suffix}"),
                new XElement("DirectAccessPermissions"),
                new XElement("Duties",
                    (duties ?? Enumerable.Empty<string>())
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .Select(d => new XElement("AxSecurityDutyReference", new XElement("Name", d)))),
                new XElement("Privileges",
                    (privileges ?? Enumerable.Empty<string>())
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => new XElement("AxSecurityPrivilegeReference", new XElement("Name", p)))),
                new XElement("PropertyModifications")));
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
            "    {\n        // handler logic here\n    }\n}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", src))));
    }

    /// <summary>
    /// Scaffolds an <c>AxSecurityPrivilege</c> with an optional entry point and
    /// an optional data-entity permission (for OData/DMF access).
    /// <para>
    /// <c>AxSecurityDataEntityPermission</c> child order matches the Microsoft
    /// metadata serializer (verified against shipped ApplicationCommon
    /// privileges <c>AgentFeedEntityMaintain/View</c>): <c>Grant</c> FIRST,
    /// then <c>Name</c>, <c>Fields</c>, <c>Methods</c> — unlike
    /// <c>AxSecurityEntryPointReference</c>, which is Name-first. The
    /// <c>&lt;Grant&gt;</c> CRUD elements are alphabetical (Correct, Create,
    /// Delete, Read, Update). Non-canonical order re-sorts noisily on first
    /// save in Visual Studio.
    /// </para>
    /// </summary>
    public static XDocument Privilege(
        string name, string? entryPointName, string? entryPointKind,
        string? entryPointObject = null, string? access = "Read", string? label = null,
        string? dataEntity = null, string? dataEntityAccess = "view")
    {
        XElement? dataEntityPermissions = null;
        if (!string.IsNullOrEmpty(dataEntity))
        {
            var maintain = string.Equals(dataEntityAccess, "maintain", StringComparison.OrdinalIgnoreCase);
            var grant = maintain
                ? new XElement("Grant",
                    new XElement("Correct", "Allow"),
                    new XElement("Create", "Allow"),
                    new XElement("Delete", "Allow"),
                    new XElement("Read", "Allow"),
                    new XElement("Update", "Allow"))
                : new XElement("Grant",
                    new XElement("Read", "Allow"));
            dataEntityPermissions = new XElement("DataEntityPermissions",
                new XElement("AxSecurityDataEntityPermission",
                    grant,
                    new XElement("Name", dataEntity),
                    new XElement("Fields"),
                    new XElement("Methods")));
        }

        return new XDocument(
            new XElement("AxSecurityPrivilege",
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                dataEntityPermissions,
                new XElement("EntryPoints",
                    string.IsNullOrEmpty(entryPointName) ? null :
                    new XElement("AxSecurityEntryPointReference",
                        new XElement("Name", entryPointName),
                        new XElement("ObjectName", entryPointObject ?? entryPointName),
                        new XElement("ObjectType", entryPointKind ?? "MenuItemDisplay"),
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

    /// <summary>
    /// Scaffolds an <c>AxReport</c> XML with full dataset / tablix / parameter structure.
    /// Supports multiple datasets (each bound to a DP class), tablix column definitions
    /// derived from <paramref name="spec"/>.<c>Fields</c> / <c>Datasets[i].Fields</c>,
    /// and <c>AxReportParameter</c> elements for report-dialog filters.
    /// Mirrors upstream MCP <c>generate_smart_report</c>. Pair with
    /// <see cref="ReportDp"/> and (when parameters exist) <see cref="ReportContract"/>.
    /// </summary>
    public static XDocument Report(ReportSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var datasets = spec.EffectiveDatasets;

        // --- <ReportParameters> (optional) ---
        XElement? parametersEl = null;
        if (spec.Parameters is { Count: > 0 })
        {
            parametersEl = new XElement("ReportParameters",
                spec.Parameters.Select(p => new XElement("AxReportParameter",
                    new XElement("Name",       p.Name),
                    new XElement("AllowBlank", p.AllowBlank ? "Yes" : "No"),
                    new XElement("DataType",   p.DataType),
                    new XElement("Prompt",     p.Prompt     ? "Yes" : "No"))));
        }

        // --- <Datasets> ---
        var datasetsEl = new XElement("Datasets",
            datasets.Select(ds => new XElement("AxReportDataset",
                new XElement("Name",           ds.Name),
                new XElement("DataProvider",   ds.DpClass),
                new XElement("QueryType",      "DataProvider"),
                new XElement("DynamicFilters", "Yes"))));

        // --- <Designs> with AutoDesignSpecs + one tablix per dataset ---
        var autoNodes = datasets.Select((ds, i) => new XElement("AxReportAutoDesignNode",
            new XElement("Name",    i == 0 ? "AutoDesign" : $"AutoDesign{i + 1}"),
            new XElement("DataSet", ds.Name),
            new XElement("ReportAutoDesignItems",
                new XElement("AxReportAutoDesignDataSet",
                    new XElement("Name",       $"AutoDesignDataSet{i + 1}"),
                    new XElement("DataSet",    ds.Name),
                    new XElement("AutoFields", "Yes")))));

        var tablixItems = datasets.Select((ds, i) => BuildTablix(ds.Name, ds.Fields, i + 1));

        var designEl = new XElement("Designs",
            new XElement("AxReportDesign",
                new XElement("Name", "Report"),
                string.IsNullOrEmpty(spec.Caption) ? null : new XElement("Caption", spec.Caption),
                new XElement("AutoDesignSpecs", autoNodes),
                new XElement("ReportDesignItems", tablixItems)));

        return new XDocument(
            new XElement("AxReport",
                new XElement("Name", spec.Name),
                parametersEl,
                datasetsEl,
                designEl));
    }

    /// <summary>
    /// Builds one <c>AxReportTablix</c> element. When <paramref name="fields"/> is
    /// provided, emits a column hierarchy, a bold header row, and a detail data row.
    /// When empty, produces a minimal tablix shell for manual completion.
    /// </summary>
    private static XElement BuildTablix(string datasetName, IReadOnlyList<string>? fields, int index)
    {
        var name = $"Tablix{index}";

        if (fields is not { Count: > 0 })
        {
            // Minimal shell — developer fills in columns manually.
            return new XElement("AxReportTablix",
                new XElement("Name",        name),
                new XElement("DataSetName", datasetName),
                new XElement("TablixBody",
                    new XElement("TablixColumns"),
                    new XElement("TablixRows")),
                new XElement("TablixColumnHierarchy",
                    new XElement("TablixMembers")),
                new XElement("TablixRowHierarchy",
                    new XElement("TablixMembers",
                        new XElement("TablixMember",
                            new XElement("Group", new XAttribute("Name", "Detail"))))));
        }

        // Column width definitions (2 in per column).
        var columnEls = fields.Select(_ =>
            new XElement("TablixColumn", new XElement("Width", "2in")));

        // Header row — one bold textbox per field.
        var headerCells = fields.Select(f =>
            new XElement("TablixCell",
                new XElement("CellContents",
                    new XElement("Textbox", new XAttribute("Name", $"{name}_{f}_Header"),
                        new XElement("Value", f),
                        new XElement("Style",
                            new XElement("FontWeight", "Bold"),
                            new XElement("BackgroundColor", "#e0e0e0"))))));

        // Detail row — one =Fields!<Field>.Value textbox per field.
        var dataCells = fields.Select(f =>
            new XElement("TablixCell",
                new XElement("CellContents",
                    new XElement("Textbox", new XAttribute("Name", $"{name}_{f}"),
                        new XElement("Value", $"=Fields!{f}.Value")))));

        // Column hierarchy: one static member per column.
        var colMembers = fields.Select(_ => new XElement("TablixMember"));

        return new XElement("AxReportTablix",
            new XElement("Name",        name),
            new XElement("DataSetName", datasetName),
            new XElement("TablixBody",
                new XElement("TablixColumns", columnEls),
                new XElement("TablixRows",
                    new XElement("TablixRow",
                        new XElement("Height", "0.25in"),
                        new XElement("TablixCells", headerCells)),
                    new XElement("TablixRow",
                        new XElement("Height", "0.25in"),
                        new XElement("TablixCells", dataCells)))),
            new XElement("TablixColumnHierarchy",
                new XElement("TablixMembers", colMembers)),
            new XElement("TablixRowHierarchy",
                new XElement("TablixMembers",
                    new XElement("TablixMember"), // static header row
                    new XElement("TablixMember",  // detail data row
                        new XElement("Group", new XAttribute("Name", "Detail"))))));
    }

    /// <summary>
    /// Scaffolds an <c>AxClass</c> implementing <c>SrsReportDataProviderBase</c>
    /// with a <c>[SRSReportDataSet]</c> getter per dataset and a
    /// <c>processReport()</c> override with a <c>QueryRun</c> skeleton.
    /// When <see cref="ReportSpec.Parameters"/> is non-empty, the declaration
    /// includes a typed cast to the companion contract. Companion: <see cref="ReportContract"/>.
    /// </summary>
    public static XDocument ReportDp(ReportSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var dp       = spec.EffectiveDpClass;
        var tmp      = spec.EffectiveTmpTable;
        var datasets = spec.EffectiveDatasets;

        // Member fields for every dataset's temp table.
        var memberDecls = string.Join("\n", datasets.Select(ds =>
            $"    {ds.DpClass + "Tmp"} {char.ToLower(ds.DpClass[0]) + ds.DpClass[1..]}Tmp;"));

        var declaration =
            $"[SRSReportDataContract(\"{spec.ContractClass}\")]\n" +
            $"class {dp} extends SrsReportDataProviderBase\n" +
            "{\n" +
            memberDecls + "\n" +
            "}\n";

        // Build one getter method per dataset.
        var getterMethods = datasets.Select((ds, i) =>
        {
            var dsTmp    = ds.DpClass + "Tmp";
            var dsField  = char.ToLower(ds.DpClass[0]) + ds.DpClass[1..] + "Tmp";
            var dsGetter = "get" + dsTmp;
            var src =
                $"[SRSReportDataSet(\"{ds.Name}\")]\n" +
                $"public {dsTmp} {dsGetter}()\n" +
                "{\n" +
                $"    select {dsField};\n" +
                $"    return {dsField};\n" +
                "}\n";
            return new XElement("Method",
                new XElement("Name",   dsGetter),
                new XElement("Source", src));
        }).ToList();

        // processReport — contract cast only when parameters are defined.
        var contractLine = spec.Parameters is { Count: > 0 }
            ? $"\n    {spec.ContractClass} contract = this.parmDataContract() as {spec.ContractClass};\n"
            : "\n";

        var processReportSrc =
            "public void processReport()\n" +
            "{\n" +
            contractLine +
            "    QueryRun qr = new QueryRun(this.parmQuery());\n" +
            "\n" +
            "    ttsbegin;\n" +
            "    while (qr.next())\n" +
            "    {\n" +
            "        // Retrieve the primary source buffer:\n" +
            "        // MyTable src = qr.get(tableNum(MyTable));\n" +
            "\n" +
            "        // Populate the staging table and insert:\n" +
            "        // " + (datasets[0].DpClass[0] | 0x20) + datasets[0].DpClass[1..] + "Tmp.Field1 = src.Field1;\n" +
            "        // " + (datasets[0].DpClass[0] | 0x20) + datasets[0].DpClass[1..] + "Tmp.insert();\n" +
            "    }\n" +
            "    ttscommit;\n" +
            "}\n";

        getterMethods.Add(new XElement("Method",
            new XElement("Name",   "processReport"),
            new XElement("Source", processReportSrc)));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name",    dp),
                new XElement("Extends", "SrsReportDataProviderBase"),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods", getterMethods))));
    }

    /// <summary>
    /// Scaffolds the companion <c>SrsReportDataContractBase</c> class for
    /// <see cref="ReportDp"/>. Emits one <c>parm*()</c> accessor per entry in
    /// <see cref="ReportSpec.Parameters"/>. Returns <see langword="null"/> when the
    /// spec has no parameters (no contract class needed).
    /// </summary>
    public static XDocument? ReportContract(ReportSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Parameters is not { Count: > 0 }) return null;

        var contractName = spec.ContractClass;

        // Map SSRS DataType to X++ primitive.
        static string XppType(string dt) => dt switch
        {
            "Integer"              => "int",
            "DateTime"             => "utcDateTime",
            "Boolean"              => "boolean",
            "Decimal" or "Float"   => "real",
            _                      => "str",
        };

        var memberDecls = string.Join("\n",
            spec.Parameters.Select(p => $"    {XppType(p.DataType)} {char.ToLower(p.Name[0])}{p.Name[1..]};"));

        var declaration =
            "[DataContractAttribute]\n" +
            $"class {contractName} extends SrsReportDataContractBase\n" +
            "{\n" +
            memberDecls + "\n" +
            "}\n";

        var parmMethods = spec.Parameters.Select(p =>
        {
            var member  = char.ToLower(p.Name[0]) + p.Name[1..];
            var xppType = XppType(p.DataType);
            var src =
                $"[DataMemberAttribute('{p.Name}')]\n" +
                $"public {xppType} parm{p.Name}({xppType} _{member} = {member})\n" +
                "{\n" +
                $"    {member} = _{member};\n" +
                $"    return {member};\n" +
                "}\n";
            return new XElement("Method",
                new XElement("Name",   $"parm{p.Name}"),
                new XElement("Source", src));
        }).ToList();

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name",    contractName),
                new XElement("Extends", "SrsReportDataContractBase"),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods", parmMethods))));
    }

    /// <summary>
    /// Scaffolds an <c>AxEdt</c>. When neither <paramref name="extends"/> nor
    /// <paramref name="baseType"/> is supplied the EDT is created without an extends
    /// clause, which is valid for root EDTs. When <paramref name="baseType"/> is
    /// supplied without <paramref name="extends"/>, a sensible standard parent is
    /// inferred (e.g. <c>String → Name</c>, <c>Int → Integer</c>).
    /// For Enum-type EDTs, supply <paramref name="enumType"/> with the backing X++ enum
    /// name (e.g. <c>NoYes</c>). When omitted, the value is inferred from
    /// <paramref name="extends"/> (e.g. <c>NoYesId -> NoYes</c>). If neither
    /// <paramref name="enumType"/> nor <paramref name="extends"/> is supplied,
    /// no <c>EnumType</c> element is emitted.
    /// </summary>
    public static XDocument Edt(
        string name,
        string? extends = null,
        string? baseType = null,
        int? stringSize = null,
        string? label = null,
        string? enumType = null)
    {
        var effectiveExtends = extends;
        if (string.IsNullOrEmpty(effectiveExtends) && !string.IsNullOrEmpty(baseType))
        {
            effectiveExtends = null; // Root EDTs stay root-level unless --extends is explicit.
        }

        // D365FO's DataContractSerializer uses the i:type attribute to indicate the concrete
        // type (e.g. AxEdtString). Derive the concrete type suffix from --base-type; when only
        // --extends is given, apply a heuristic over well-known system EDTs so common cases
        // work without flags.
        var concreteTypeSuffix = !string.IsNullOrEmpty(baseType)
            ? baseType.ToLowerInvariant() switch
            {
                "int" or "integer"          => "Int",
                "int64"                     => "Int64",
                "real"                      => "Real",
                "date"                      => "Date",
                "utcdatetime" or "datetime" => "UtcDateTime",
                "boolean" or "bool"         => "Enum",
                "time"                      => "Time",
                "guid"                      => "Guid",
                "container"                 => "Container",
                "enum"                      => "Enum",
                _                           => "String",
            }
            : InferConcreteTypeSuffixFromExtends(effectiveExtends);

        // D365FO's metadata reader requires the XMLSchema-instance namespace declaration on
        // the root element (Visual Studio emits it on every AxEdt* file). The i:type
        // attribute specifies the concrete type (e.g. AxEdtString).
        // Element order matters: Name/Extends/Label/StringSize first, then collection
        // elements, then EnumType (when present) to match VS metadata shape.
        // For Enum-type EDTs the backing X++ enum name is required (<EnumType> element).
        // Resolve in this order:
        //   1) explicit --enum-type
        //   2) infer from --extends (NoYesId -> NoYes, otherwise use extends as enum name)
        //   3) no value when neither is supplied
        var effectiveEnumType = concreteTypeSuffix == "Enum"
            ? (!string.IsNullOrEmpty(enumType)
                ? enumType
                : (!string.IsNullOrEmpty(effectiveExtends)
                    ? InferEnumTypeFromExtends(effectiveExtends)
                    : (string.Equals(baseType, "Boolean", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(baseType, "Bool", StringComparison.OrdinalIgnoreCase)
                        ? "NoYes"
                        : null)))
            : null;

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var root = new XElement("AxEdt",
            new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
            new XAttribute(XName.Get("type", xsi.NamespaceName), $"AxEdt{concreteTypeSuffix}"),
            new XElement("Name", name),
            string.IsNullOrEmpty(effectiveExtends) ? null : new XElement("Extends", effectiveExtends),
            string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
            stringSize.HasValue ? new XElement("StringSize", stringSize.Value.ToString()) : null,
            concreteTypeSuffix == "String" ? null : new XElement("ArrayElements"),
            concreteTypeSuffix == "String" ? null : new XElement("Relations"),
            concreteTypeSuffix == "String" ? null : new XElement("TableReferences"),
            string.IsNullOrEmpty(effectiveEnumType) ? null : new XElement("EnumType", effectiveEnumType));
        return new XDocument(root);
    }

    /// <summary>
    /// Infers the backing X++ enum name from a well-known system EDT name used as Extends.
    /// For unknown names, returns null to avoid guessing an invalid enum name.
    /// </summary>
    private static string? InferEnumTypeFromExtends(string? extends) =>
        extends?.ToLowerInvariant() switch
        {
            "noyesid" or "noyes"   => "NoYes",
            "boolean"              => "NoYes",
            _                      => null,
        };

    /// <summary>
    /// Resolve the concrete <c>AxTableField{Suffix}</c> discriminator for a
    /// field's EDT. Prefers the index-backed base type; falls back to a
    /// heuristic over well-known system EDTs (defaulting to <c>String</c>).
    /// </summary>
    private static string TableFieldConcreteSuffix(string edtName, Func<string, string?>? resolver)
        => ConcreteFieldSuffix(edtName, resolver);

    /// <summary>
    /// Resolve the concrete field-subtype suffix (<c>String</c>, <c>Int64</c>, …) for an
    /// EDT. Shared by every polymorphic AOT field family that uses the same suffix
    /// vocabulary — <c>AxTableField*</c>, <c>AxMapField*</c> — so a map field and a table
    /// field on the same EDT can never disagree about its primitive type. Prefers the
    /// index-backed base type; falls back to a heuristic over well-known system EDT names.
    /// </summary>
    internal static string ConcreteFieldSuffix(string edtName, Func<string, string?>? resolver)
    {
        var baseType = resolver?.Invoke(edtName);
        var fromIndex = SuffixFromBaseType(baseType);
        if (fromIndex is not null) return fromIndex;
        return InferConcreteTypeSuffixFromExtends(edtName);
    }

    /// <summary>
    /// Map an EDT primitive base type (as stored in the index — the concrete
    /// <c>AxEdt*</c> root element name minus the prefix) to the matching
    /// <c>AxTableField</c> subtype suffix. Returns null for unknown/empty input
    /// so the caller can fall back to the name heuristic.
    /// </summary>
    private static string? SuffixFromBaseType(string? baseType) =>
        string.IsNullOrWhiteSpace(baseType)
            ? null
            : baseType.ToLowerInvariant() switch
            {
                "string"              => "String",
                "int" or "integer"    => "Int",
                "int64"               => "Int64",
                "real"                => "Real",
                "date"                => "Date",
                "time"                => "Time",
                "utcdatetime"         => "UtcDateTime",
                "enum"                => "Enum",
                "guid"                => "Guid",
                "container"           => "Container",
                _                     => null,
            };

    private static string InferConcreteTypeSuffixFromExtends(string? extends)
    {
        var e = extends?.ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return "String";
        var exact = e switch
        {
            "integer" or "int"                                      => "Int",
            "int64" or "recid"                                      => "Int64",
            "amount" or "amountmst" or "qty" or "weight" or "real"  => "Real",
            "date" or "transdate"                                   => "Date",
            "utcdatetime" or "transdatetime"                        => "UtcDateTime",
            "noyes" or "noyesid" or "boolean"                       => "Enum",
            "timeofday" or "time"                                   => "Time",
            "guid"                                                   => "Guid",
            "container"                                              => "Container",
            _                                                        => null,
        };
        if (exact is not null) return exact;
        // Name-shape fallback for unindexed custom EDTs. Any *DateTime* name is
        // a utcDateTime EDT — an exclusion-based variant of this check upstream
        // mistyped exactly "TransDateTime" (contains both markers) as String.
        if (e.Contains("datetime")) return "UtcDateTime";
        if (e.EndsWith("date")) return "Date";
        return "String";
    }

    /// <summary>Scaffolds an <c>AxEnum</c> with optional values.</summary>
    public static XDocument Enum(
        string name,
        IEnumerable<EnumValueSpec>? values = null,
        bool isExtensible = true,
        string? label = null)
    {
        var enumVals = (values ?? Enumerable.Empty<EnumValueSpec>()).ToList();

        var valEls = enumVals.Select(v =>
        {
            var el = new XElement("AxEnumValue",
                new XElement("Name", v.Name),
                new XElement("Value", v.IntValue.ToString()));
            if (!string.IsNullOrEmpty(v.Label))
                el.Add(new XElement("Label", v.Label));
            return el;
        });

        // D365FO's metadata reader requires the XMLSchema-instance namespace declaration on
        // the root element (it is emitted by Visual Studio on every AxEnum file). IsExtensible
        // is a CLR bool, so the DataContractSerializer expects "true"/"false" — the NoYes-style
        // "Yes"/"No" written previously produced an invalid file VS refused to read.
        // VS's build-time validation additionally rejects IsExtensible=true unless
        // UseEnumValue is explicitly "No" ("UseEnumValue property must be set to 'No' when
        // the IsExtensible property is 'True'."). UseEnumValue is itself a NoYes-style enum
        // property (unlike IsExtensible), so it takes "Yes"/"No", not "true"/"false".
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        return new XDocument(
            new XElement("AxEnum",
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XElement("Name", name),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("IsExtensible", isExtensible ? "true" : "false"),
                isExtensible ? new XElement("UseEnumValue", "No") : null,
                enumVals.Count > 0 ? new XElement("EnumValues", valEls) : null));
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

/// <summary>One dataset within an <c>AxReport</c>, bound to a DP class.</summary>
public sealed record ReportDatasetSpec(
    string Name,
    string DpClass,
    IReadOnlyList<string>? Fields = null);

/// <summary>One SSRS report parameter exposed on the report dialog.</summary>
public sealed record ReportParameterSpec(
    string Name,
    string DataType = "String",
    bool AllowBlank = true,
    bool Prompt = true);

/// <summary>
/// Parameters for <see cref="XppScaffolder.Report"/>, <see cref="XppScaffolder.ReportDp"/>,
/// and <see cref="XppScaffolder.ReportContract"/>.
/// Derived effective names are computed by the <c>Effective*</c> properties when the caller
/// does not supply an explicit override.
/// </summary>
public sealed record ReportSpec(
    string Name,
    string? DpClass = null,
    string? TmpTable = null,
    string? DatasetName = null,
    string? Caption = null,
    IReadOnlyList<ReportDatasetSpec>? Datasets = null,
    IReadOnlyList<string>? Fields = null,
    IReadOnlyList<ReportParameterSpec>? Parameters = null)
{
    public string EffectiveDpClass  => string.IsNullOrWhiteSpace(DpClass)     ? Name + "DP"  : DpClass!;
    public string EffectiveTmpTable => string.IsNullOrWhiteSpace(TmpTable)    ? Name + "Tmp" : TmpTable!;
    public string EffectiveDataset  => string.IsNullOrWhiteSpace(DatasetName) ? Name + "DS"  : DatasetName!;

    /// <summary>
    /// Effective dataset list: either caller-supplied multi-dataset list, or the single
    /// primary dataset derived from <see cref="EffectiveDataset"/> / <see cref="EffectiveDpClass"/>.
    /// </summary>
    public IReadOnlyList<ReportDatasetSpec> EffectiveDatasets =>
        (Datasets is { Count: > 0 })
            ? Datasets
            : [new ReportDatasetSpec(EffectiveDataset, EffectiveDpClass, Fields)];

    /// <summary>Name of the companion DataContract class.</summary>
    public string ContractClass => EffectiveDpClass + "Contract";
}

public sealed record TableFieldSpec(string Name, string? Edt, string? Label, bool Mandatory);

/// <summary>One value within an <c>AxEnum</c>.</summary>
public sealed record EnumValueSpec(string Name, int IntValue, string? Label = null);

/// <summary>
/// Writes a scaffolded XML document atomically: a .tmp sibling is written and
/// then moved onto the target path. Any pre-existing file is kept as .bak
/// unless the <c>overwrite</c> flag is false (in which case the operation
/// fails before touching disk).
/// </summary>
public static class ScaffoldFileWriter
{
    public sealed record WriteResult(string Path, long Bytes, string? BackupPath);

    // AOT root elements that are abstract bases in Microsoft.Dynamics.AX.Metadata.MetaModel.
    // Writing one of these as the document root makes VS metadata reader throw
    // "Cannot create an abstract class" when the file is opened — callers must use the
    // concrete subtype (AxEdtString, AxEdtInt, AxEdtStringExtension, AxQuerySimple, …).
    // AxEdt itself is handled separately by EnsureValidEdtRoot, which explains the
    // concrete subtypes on offer.
    private static readonly HashSet<string> _abstractAxRoots =
        D365FO.Core.ObjectTypes.ObjectTypeRegistry.AbstractRoots();

    private const string XsiNamespace = "http://www.w3.org/2001/XMLSchema-instance";

    // AOT roots whose files are unreadable without the XMLSchema-instance namespace
    // declared on the root element. AxEdt* and AxQuery carry i:type on the root itself,
    // while AxTable / AxView / AxMap carry it on every field (AxTableField, AxViewField,
    // AxMapBaseField are all polymorphic, abstract-based types — see issue #91);
    // AxEnum needs it because Visual Studio's metadata reader rejects the file
    // outright when the declaration is absent (issue #70). Every entry is
    // ground-truthed against shipped standard-model files on a real AOS and lives in
    // ObjectTypeRegistry, not here.
    // Deliberately NOT a blanket rule for every AxXxx root: AxClass/AxMenuItem/…
    // are written without it today and are read back fine.
    private static readonly HashSet<string> _xsiRequiredAxRoots =
        D365FO.Core.ObjectTypes.ObjectTypeRegistry.XsiRequiredRoots();

    // AOT elements that deserialize into a CLR bool, not a NoYes-style enum. The
    // DataContractSerializer reads these with XmlConvert.ToBoolean, so the NoYes
    // spelling ("Yes"/"No") that most AOT properties use produces a file Visual
    // Studio refuses to open (issue #70). Sibling properties like UseEnumValue
    // really are NoYes enums and must NOT be listed here.
    private static readonly HashSet<string> _clrBoolElements = new(StringComparer.Ordinal)
    {
        "IsExtensible",
    };

    // Exactly what XmlConvert.ToBoolean accepts for xs:boolean; "True"/"Yes"/"1 "
    // and friends all throw at deserialization time.
    private static readonly HashSet<string> _xmlBoolLiterals = new(StringComparer.Ordinal)
    {
        "true", "false", "1", "0",
    };

    public static WriteResult Write(XDocument doc, string path, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(doc);
        EnsureConcreteAxRoot(doc.Root);
        EnsureValidEdtRoot(doc.Root);
        EnsureValueShapes(doc.Root);
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
        var root = ParseRootElement(xml);
        EnsureConcreteAxRoot(root);
        EnsureValidEdtRoot(root);
        EnsureValueShapes(root);
        return WriteCore(xml, path, overwrite, declarationOnSaveFromXDoc: false, null);
    }

    public sealed record DeleteResult(string Path, string PreImage);

    /// <summary>
    /// Delete an on-disk AOT object file, capturing its exact pre-image bytes and journaling the
    /// delete (best-effort) so <c>d365fo undo</c> can restore it. Counterpart to <c>Write</c>
    /// for the disk write path's create/delete symmetry — see <see cref="D365FO.Core.Journal.UndoEngine"/>.
    /// </summary>
    public static DeleteResult Delete(string path, string? model = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);

        var cfg = D365FoSettings.FromEnvironment();
        PathGuard.EnsureWithinBoundary(full, new[] { cfg.PackagesPath, cfg.WorkspacePath }.Concat(cfg.CustomPackagesPaths).ToArray());

        if (!File.Exists(full))
            throw new FileNotFoundException($"Target does not exist: {full}", full);

        var preImage = SafeReadAllText(full);
        var preImageHadBom = DetectUtf8Bom(full);
        var (kind, objectName) = InferKindAndName(full);
        var effectiveModel = model ?? InferModel(full);

        RnrProjDelta? delta = null;
        if (effectiveModel is not null)
        {
            var modelFolder = Path.GetDirectoryName(Path.GetDirectoryName(full));
            var axSubfolder = Path.GetFileName(Path.GetDirectoryName(full));
            if (!string.IsNullOrEmpty(modelFolder) && !string.IsNullOrEmpty(axSubfolder))
                delta = RnrProjRegistry.TryRegisterDelete(modelFolder!, axSubfolder!, objectName);
        }

        File.Delete(full);

        try
        {
            var entry = new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: "delete",
                TargetType: JournalTargetType.AotObject,
                Kind: kind,
                ObjectName: objectName,
                SecondaryKey: null,
                Model: effectiveModel,
                Operation: JournalOperation.Delete,
                WritePath: JournalWritePath.Disk,
                TargetPath: full,
                PreImage: preImage,
                IsTombstone: false,
                RnrProjDelta: delta,
                PreImageHadBom: preImageHadBom);
            ModificationJournal.ForIndex().Append(entry);
        }
        catch { /* best-effort */ }

        return new DeleteResult(full, preImage);
    }

    /// <summary>
    /// An abstract root is writable only when the concrete subtype is pinned via
    /// <c>i:type</c> — the same escape hatch the bridge's <c>WriteArtifact</c> honours for
    /// <c>&lt;AxEdt i:type="AxEdtString"&gt;</c>. Without that discriminator the file is
    /// unreadable, so refusing outright is right; with it the document is well-formed and
    /// blocking it would make <c>generate extension edt</c> impossible to satisfy.
    /// </summary>
    private static void EnsureConcreteAxRoot(XElement? root)
    {
        var rootLocalName = root?.Name.LocalName;
        if (rootLocalName is null || !_abstractAxRoots.Contains(rootLocalName)) return;
        // The AxEdt family gets the more specific diagnostic from EnsureValidEdtRoot,
        // which can name the concrete subtypes on offer.
        if (rootLocalName is "AxEdt" or "AxEdtExtension") return;
        if (HasConcreteXsiType(root!, rootLocalName)) return;

        throw new InvalidOperationException(
            $"Refusing to write AOT XML with abstract root element <{rootLocalName}> and no concrete i:type. " +
            "Pin the concrete subtype (e.g. AxEdtStringExtension, AxEdtIntExtension) via the " +
            "XMLSchema-instance type attribute, or use a concrete root element. Visual Studio's " +
            "metadata reader throws \"Cannot create an abstract class\" otherwise.");
    }

    private static void EnsureValidEdtRoot(XElement? root)
    {
        var rootLocalName = root?.Name.LocalName;
        if (rootLocalName is not "AxEdt" and not "AxEdtExtension") return;
        if (HasConcreteXsiType(root!, rootLocalName!)) return;

        throw new InvalidOperationException(
            $"Refusing to write <{rootLocalName}> without a concrete XMLSchema-instance type. " +
            "Set i:type to a concrete subtype (e.g. AxEdtString, AxEdtInt, AxEdtReal, " +
            "AxEdtDate, AxEdtUtcDateTime, AxEdtTime, AxEdtGuid, AxEdtContainer, AxEdtEnum — " +
            "suffixed with 'Extension' for an EDT extension). Visual Studio's metadata reader " +
            "throws \"Cannot create an abstract class\" when type metadata is missing.");
    }

    /// <summary>True when the root pins a concrete <c>Ax*</c> subtype that is not the abstract base itself.</summary>
    private static bool HasConcreteXsiType(XElement root, string rootLocalName)
    {
        var typeValue = root.Attribute(XName.Get("type", XsiNamespace))?.Value;
        return !string.IsNullOrWhiteSpace(typeValue)
               && !string.Equals(typeValue, rootLocalName, StringComparison.Ordinal)
               && typeValue!.StartsWith("Ax", StringComparison.Ordinal);
    }

    /// <summary>
    /// Runtime-free shape checks over the document about to be written: the
    /// XMLSchema-instance declaration the polymorphic AOT roots depend on, and
    /// primitive values whose encoding the metadata reader would reject. Both
    /// run before anything touches disk, in either <c>Write</c> overload.
    /// </summary>
    private static void EnsureValueShapes(XElement? root)
    {
        if (root is null) return;
        EnsureXsiNamespaceDeclared(root);
        EnsureClrBoolValues(root);
    }

    private static void EnsureXsiNamespaceDeclared(XElement root)
    {
        var rootName = root.Name.LocalName;
        var required = _xsiRequiredAxRoots.Contains(rootName) ||
                       rootName.StartsWith("AxEdt", StringComparison.Ordinal);
        if (!required) return;

        // Any prefix bound to the XMLSchema-instance URI counts; Visual Studio
        // always writes "i", but the prefix carries no meaning of its own.
        var declared = root.Attributes()
            .Any(a => a.IsNamespaceDeclaration &&
                      string.Equals(a.Value, XsiNamespace, StringComparison.Ordinal));
        if (declared) return;

        throw new InvalidOperationException(
            $"Refusing to write <{rootName}> without the XMLSchema-instance namespace on the root. " +
            $"Declare xmlns:i=\"{XsiNamespace}\" so the i:type discriminators resolve. " +
            "Visual Studio's metadata reader cannot open the file without it.");
    }

    private static void EnsureClrBoolValues(XElement root)
    {
        foreach (var el in root.DescendantsAndSelf())
        {
            if (!_clrBoolElements.Contains(el.Name.LocalName)) continue;
            if (el.HasElements) continue;

            var value = el.Value;
            if (_xmlBoolLiterals.Contains(value)) continue;

            throw new InvalidOperationException(
                $"Refusing to write AOT XML with <{el.Name.LocalName}>{value}</{el.Name.LocalName}>. " +
                $"{el.Name.LocalName} maps to a CLR bool, so it takes \"true\"/\"false\" — not the " +
                "NoYes spelling \"Yes\"/\"No\" that enum-typed AOT properties use. Visual Studio's " +
                "metadata reader refuses to open the file otherwise.");
        }
    }

    private static XElement? ParseRootElement(string xml)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var sr = new StringReader(xml);
            using var xr = XmlReader.Create(sr, settings);
            return XDocument.Load(xr, LoadOptions.PreserveWhitespace).Root;
        }
        catch
        {
            return null;
        }
    }

    private static WriteResult WriteCore(string xml, string path, bool overwrite, bool declarationOnSaveFromXDoc, XDocument? doc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var full = Path.GetFullPath(path);

        // Prevent directory traversal: output must stay within packages or workspace.
        var cfg = D365FoSettings.FromEnvironment();
        PathGuard.EnsureWithinBoundary(full, new[] { cfg.PackagesPath, cfg.WorkspacePath }.Concat(cfg.CustomPackagesPaths).ToArray());

        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // Captured BEFORE any mutation so the journal entry holds the exact pre-image bytes —
        // independent of the .bak file below, which the write path keeps for immediate manual
        // recovery but is not itself part of the journal's reversible-write contract.
        string? preImage = File.Exists(full) ? SafeReadAllText(full) : null;
        bool? preImageHadBom = File.Exists(full) ? DetectUtf8Bom(full) : null;

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
        try
        {
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
        }
        catch
        {
            // Rollback: restore original from backup if the final move failed.
            if (backup is not null && File.Exists(backup) && !File.Exists(full))
            {
                try { File.Move(backup, full); }
                catch { /* best-effort restore */ }
            }

            // Clean up temp file if it was left behind.
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* best-effort cleanup */ }

            throw;
        }

        var bytes = new FileInfo(full).Length;
        RecordJournalEntry(full, preImage, preImageHadBom);
        return new WriteResult(full, bytes, backup);
    }

    /// <summary>
    /// Best-effort modification-journal append (issue #113) — never lets a journal failure
    /// fail the write it is recording. Fires for EVERY caller of <see cref="Write(XDocument,string,bool)"/>
    /// / <see cref="Write(string,string,bool)"/>: the CLI `generate *` commands, the MCP
    /// `generate_object` tool, and any future disk-scaffold caller — this is the single choke
    /// point the journal's "shared by every write path" design relies on for on-disk writes.
    /// Kind/ObjectName/Model are inferred from the written XML and the D365FO packages layout
    /// convention (&lt;root&gt;\&lt;Model&gt;\&lt;Model&gt;\Ax&lt;Kind&gt;\&lt;Name&gt;.xml) —
    /// best-effort: when the path doesn't match that convention, Model is left null and the
    /// entry is still recorded (disk replay only needs the path + pre-image, not the model).
    /// </summary>
    private static void RecordJournalEntry(string fullPath, string? preImage, bool? preImageHadBom = null)
    {
        try
        {
            var (kind, objectName) = InferKindAndName(fullPath);
            var model = InferModel(fullPath);
            RnrProjDelta? delta = null;
            if (preImage is null && model is not null)
            {
                var modelFolder = Path.GetDirectoryName(Path.GetDirectoryName(fullPath));
                var axSubfolder = Path.GetFileName(Path.GetDirectoryName(fullPath));
                if (!string.IsNullOrEmpty(modelFolder) && !string.IsNullOrEmpty(axSubfolder))
                    delta = RnrProjRegistry.TryRegisterCreate(modelFolder!, axSubfolder!, objectName);
            }

            var entry = new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: "scaffold-write",
                TargetType: JournalTargetType.AotObject,
                Kind: kind,
                ObjectName: objectName,
                SecondaryKey: null,
                Model: model,
                Operation: preImage is null ? JournalOperation.Create : JournalOperation.Update,
                WritePath: JournalWritePath.Disk,
                TargetPath: fullPath,
                PreImage: preImage,
                IsTombstone: preImage is null,
                RnrProjDelta: delta,
                PreImageHadBom: preImageHadBom);

            ModificationJournal.ForIndex().Append(entry);
        }
        catch
        {
            // Best-effort: the journal is a convenience, not a correctness requirement of the
            // write itself. Never let it turn a successful scaffold write into a failure.
        }
    }

    private static string SafeReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return string.Empty; }
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts with a UTF-8 BOM. Recorded alongside the pre-image
    /// so <c>d365fo undo</c> can restore the file byte-for-byte: the pre-image is a decoded string
    /// and the decoder swallows the BOM, so without this the restored AOT XML would be three bytes
    /// shorter than the original that D365FO and Visual Studio wrote.
    /// </summary>
    internal static bool? DetectUtf8Bom(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[3];
            return fs.ReadAtLeast(head, 3, throwOnEndOfStream: false) == 3
                   && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF;
        }
        catch { return null; }
    }

    /// <summary>
    /// Best-effort Kind/Name inference from the just-written XML: Kind from the root element's
    /// local name (leading "Ax" stripped — purely descriptive, not the bridge kind vocabulary,
    /// since disk-only journal entries never need to name a bridge collection), Name from the
    /// root's child &lt;Name&gt; element (the universal AOT convention), falling back to the
    /// file name when either is missing.
    /// </summary>
    private static (string kind, string objectName) InferKindAndName(string fullPath)
    {
        var fallbackName = Path.GetFileNameWithoutExtension(fullPath);
        try
        {
            var root = XElement.Load(fullPath);
            var localName = root.Name.LocalName;
            var kind = localName.StartsWith("Ax", StringComparison.Ordinal) && localName.Length > 2
                ? localName[2..]
                : localName;
            var name = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value;
            return (string.IsNullOrEmpty(kind) ? "object" : kind, string.IsNullOrWhiteSpace(name) ? fallbackName : name);
        }
        catch
        {
            return ("object", fallbackName);
        }
    }

    /// <summary>
    /// Best-effort Model inference from the D365FO packages layout convention
    /// (&lt;root&gt;\&lt;Model&gt;\&lt;Model&gt;\Ax&lt;Kind&gt;\&lt;Name&gt;.xml — see
    /// <c>GenerateInstaller.ResolveInstallPath</c>). Only trusts the inference when the two
    /// model-name path segments actually match; otherwise returns null rather than guessing.
    /// </summary>
    private static string? InferModel(string fullPath)
    {
        try
        {
            var axSubfolderDir = Path.GetDirectoryName(fullPath); // .../<Model>/<Model>/Ax<Kind>
            var modelDir = Path.GetDirectoryName(axSubfolderDir); // .../<Model>/<Model>
            var modelParentDir = Path.GetDirectoryName(modelDir); // .../<Model>
            var inner = modelDir is null ? null : Path.GetFileName(modelDir);
            var outer = modelParentDir is null ? null : Path.GetFileName(modelParentDir);
            return !string.IsNullOrEmpty(inner) && string.Equals(inner, outer, StringComparison.OrdinalIgnoreCase)
                ? inner
                : null;
        }
        catch
        {
            return null;
        }
    }
}
