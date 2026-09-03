using System;
using System.Collections.Generic;

namespace D365FO.Core.ObjectTypes
{
    /// <summary>
    /// One AOT object type: its XML root element, on-disk folder, MetaModel type,
    /// provider collection, and the policies every writer/reader needs to agree on.
    /// </summary>
    /// <remarks>
    /// Deliberately a plain class with a positional constructor and no records,
    /// <c>init</c> accessors or nullable annotations: this file is compiled into
    /// <c>D365FO.Bridge</c> too, which targets net48 (no <c>IsExternalInit</c>,
    /// nullable disabled). Keep it dependency-free.
    /// </remarks>
    public sealed class ObjectTypeInfo
    {
        public ObjectTypeInfo(
            string kind,
            string rootElement,
            string aotSubfolder,
            string metaModelType,
            string providerCollection,
            string generateCommand,
            string mcpObjectType,
            string namingKind,
            string contractNamespace,
            bool requiresXsiNamespace,
            bool abstractRoot,
            bool existsInStandardAot,
            bool indexed)
        {
            Kind = kind;
            RootElement = rootElement;
            AotSubfolder = aotSubfolder;
            MetaModelType = metaModelType;
            ProviderCollection = providerCollection;
            GenerateCommand = generateCommand;
            McpObjectType = mcpObjectType;
            NamingKind = namingKind;
            ContractNamespace = contractNamespace ?? string.Empty;
            RequiresXsiNamespace = requiresXsiNamespace;
            AbstractRoot = abstractRoot;
            ExistsInStandardAot = existsInStandardAot;
            Indexed = indexed;
        }

        /// <summary>Canonical kind id: lower-case, letters and digits only ("menuitemdisplay").</summary>
        public string Kind { get; }

        /// <summary>Root element of the object's AOT XML ("AxTable").</summary>
        public string RootElement { get; }

        /// <summary>Folder under the model root holding these files. Always equals the root element in a real AOT.</summary>
        public string AotSubfolder { get; }

        /// <summary>
        /// Short MetaModel class name under <c>Microsoft.Dynamics.AX.Metadata.MetaModel</c>.
        /// Differs from <see cref="RootElement"/> where the root is abstract and the
        /// provider needs the concrete subtype (queries: <c>AxQuery</c> → <c>AxQuerySimple</c>).
        /// </summary>
        public string MetaModelType { get; }

        /// <summary>
        /// <c>IMetadataProvider</c> collection name ("Tables"), or null when this build of
        /// the platform exposes no provider channel for the type and it must be written as XML.
        /// </summary>
        public string ProviderCollection { get; }

        /// <summary><c>d365fo generate &lt;name&gt;</c> subcommand that emits this type, or null.</summary>
        public string GenerateCommand { get; }

        /// <summary>Discriminator accepted by the MCP <c>generate_object</c> tool, or null when not exposed.</summary>
        public string McpObjectType { get; }

        /// <summary>Kind string understood by <see cref="ObjectNamingRules"/>.</summary>
        public string NamingKind { get; }

        /// <summary>
        /// XML namespace the type's <c>DataContract</c> declares, and therefore the namespace
        /// its on-disk files must use — empty for most types, but
        /// <c>Microsoft.Dynamics.AX.Metadata.V1</c> for menu items and tiles, <c>V2</c> for
        /// reports and workflow objects, <c>V6</c> for forms. Get it wrong and the metadata
        /// reader rejects the file outright ("Expecting element X from namespace Y"), which is
        /// what shipped for menu items, reports and workflows until this was ground-truthed
        /// against <c>Microsoft.Dynamics.AX.Metadata.dll</c>.
        /// </summary>
        public string ContractNamespace { get; }

        /// <summary>The metadata reader rejects the file unless the XMLSchema-instance namespace is declared on the root.</summary>
        public bool RequiresXsiNamespace { get; }

        /// <summary>Root type is abstract — the concrete subtype must be pinned via <c>i:type</c>.</summary>
        public bool AbstractRoot { get; }

        /// <summary>
        /// The folder is present in a shipped D365FO installation. False marks a name that
        /// looks plausible but matches nothing on any AOS — the failure mode behind audit
        /// finding G1, where <c>AxWorkflowType</c> was read for years and never existed.
        /// </summary>
        public bool ExistsInStandardAot { get; }

        /// <summary>The extractor reads this folder into the index.</summary>
        public bool Indexed { get; }

        public override string ToString() => Kind + " (" + RootElement + ")";
    }

    /// <summary>
    /// The single source of truth for AOT object types: root element, AOT folder,
    /// MetaModel type, provider collection, write policies, and which surfaces expose
    /// each type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Replaces four registries that drifted apart independently (audit finding G2):
    /// the bridge's <c>KindToCollection</c>/<c>KindToTypeName</c>, ~30 hard-coded folder
    /// literals across <c>Commands/Generate/*</c>, <c>ObjectLookup</c>'s read kinds, and
    /// <c>MetadataExtractor</c>'s folder list. The first visible casualty of that drift was
    /// G1: the generator wrote <c>AxWorkflow</c>, the extractor read <c>AxWorkflowType</c>,
    /// and the real folder is <c>AxWorkflowTemplate</c>.
    /// </para>
    /// <para>
    /// Folder names and their existence are ground-truthed against a full census of
    /// <c>PackagesLocalDirectory</c> on a real AOS (2026-08-05, ~80 distinct <c>Ax*</c>
    /// folders across every package). <c>ObjectTypeRegistryAotTests</c> re-runs that census
    /// when <c>D365FO_PACKAGES_PATH</c> points at one.
    /// </para>
    /// <para>
    /// This file is shared-compiled into <c>D365FO.Bridge</c> (net48) — see the remarks on
    /// <see cref="ObjectTypeInfo"/> before adding language features.
    /// </para>
    /// </remarks>
    public static class ObjectTypeRegistry
    {
        /// <summary>
        /// AOT subfolder names as compile-time constants, for the write paths that used to
        /// carry ~30 loose <c>"AxSomething"</c> literals. Using these makes a mistyped or
        /// invented folder a build error instead of a file written where nothing reads it.
        /// <c>ObjectTypeRegistryTests</c> asserts every constant is a registered subfolder.
        /// </summary>
        public static class Folders
        {
            public const string Table = "AxTable";
            public const string Class = "AxClass";
            public const string Edt = "AxEdt";
            public const string Enum = "AxEnum";
            public const string Form = "AxForm";
            public const string View = "AxView";
            public const string Map = "AxMap";
            public const string Query = "AxQuery";
            public const string DataEntityView = "AxDataEntityView";

            public const string TableExtension = "AxTableExtension";
            public const string FormExtension = "AxFormExtension";
            public const string EdtExtension = "AxEdtExtension";
            public const string EnumExtension = "AxEnumExtension";
            public const string SecurityDutyExtension = "AxSecurityDutyExtension";
            public const string SecurityRoleExtension = "AxSecurityRoleExtension";

            public const string MenuItemDisplay = "AxMenuItemDisplay";
            public const string MenuItemAction = "AxMenuItemAction";
            public const string MenuItemOutput = "AxMenuItemOutput";

            public const string Report = "AxReport";
            public const string Service = "AxService";
            public const string ServiceGroup = "AxServiceGroup";

            public const string SecurityRole = "AxSecurityRole";
            public const string SecurityDuty = "AxSecurityDuty";
            public const string SecurityPrivilege = "AxSecurityPrivilege";
            public const string SecurityPolicy = "AxSecurityPolicy";

            public const string WorkflowTemplate = "AxWorkflowTemplate";
            public const string WorkflowApproval = "AxWorkflowApproval";
            public const string WorkflowTask = "AxWorkflowTask";
            public const string WorkflowCategory = "AxWorkflowCategory";

            public const string ConfigurationKey = "AxConfigurationKey";
            public const string LabelFile = "AxLabelFile";
            public const string Resource = "AxResource";
            public const string Tile = "AxTile";
            public const string Menu = "AxMenu";
            public const string FormPart = "AxFormPart";
            public const string CompositeDataEntityView = "AxCompositeDataEntityView";
            public const string AggregateDataEntity = "AxAggregateDataEntity";
            public const string MapExtension = "AxMapExtension";
            public const string MenuExtension = "AxMenuExtension";
        }

        private static readonly ObjectTypeInfo[] _all = BuildAll();

        private static ObjectTypeInfo[] BuildAll()
        {
            return new[]
            {
                // ── Core types the provider can write ──────────────────────────────
                New("table", "AxTable", "Tables", "table", "table", "Table", xsi: true),
                New("class", "AxClass", "Classes", "class", "class", "Class"),
                New("edt", "AxEdt", "Edts", "edt", "edt", "Edt", xsi: true, abstractRoot: true),
                New("enum", "AxEnum", "Enums", "enum", "enum", "Enum", xsi: true),
                New("form", "AxForm", "Forms", "form", "form", "Form", ns: NsV6),
                New("view", "AxView", "Views", "view", null, "View", xsi: true),
                New("map", "AxMap", "Maps", "map", null, "Map", xsi: true),
                // AxQuery is an abstract MetaModel base: every shipped file is
                // <AxQuery xmlns:i=… i:type="AxQuerySimple">, and the reader throws
                // "Cannot create an abstract class" without that discriminator. The folder
                // stays AxQuery (census: 4,989 files; no AxQuerySimple folder exists), and the
                // bridge keeps mapping to the abstract type — WriteArtifact resolves the
                // concrete subtype from the root's i:type, exactly as it does for AxEdt.
                New("query", "AxQuery", "Queries", "query", "query", "Query", xsi: true, abstractRoot: true),
                New("dataentityview", "AxDataEntityView", "DataEntityViews", "entity", null, "Entity"),

                // ── Extensions ─────────────────────────────────────────────────────
                New("tableextension", "AxTableExtension", "TableExtensions", "extension", null, "TableExtension"),
                New("formextension", "AxFormExtension", "FormExtensions", "extension", null, "FormExtension", ns: NsV6),
                // Concrete, despite the name symmetry with the abstract AxEdt: the assembly
                // declares no AxEdt*Extension subtypes, so an i:type here would name nothing.
                New("edtextension", "AxEdtExtension", "EdtExtensions", "extension", null, "EdtExtension", xsi: true),
                New("enumextension", "AxEnumExtension", "EnumExtensions", "extension", null, "EnumExtension"),
                New("viewextension", "AxViewExtension", "ViewExtensions", "extension", null, "ViewExtension"),
                // Query extensions are concrete on disk: folder and root element are both
                // AxQuerySimpleExtension, and the provider property is QuerySimpleExtensions.
                // There is no AxQueryExtension type at all — the bridge used to name one.
                New("queryextension", "AxQuerySimpleExtension", "QuerySimpleExtensions", "extension", null, "QueryExtension"),
                New("dataentityviewextension", "AxDataEntityViewExtension", "DataEntityViewExtensions", "extension", null, "DataEntityViewExtension"),
                // Folder ships in every model and no standard model has a file in it yet, so
                // there is nothing here to index — but `generate extension --kind map` does
                // build one, which is what the command name records.
                New("mapextension", "AxMapExtension", "MapExtensions", "extension", null, "MapExtension"),
                New("menuextension", "AxMenuExtension", "MenuExtensions", "extension", null, "MenuExtension", ns: NsV1),
                New("securitydutyextension", "AxSecurityDutyExtension", "SecurityDutyExtensions", "extension", null, "SecurityDutyExtension"),
                New("securityroleextension", "AxSecurityRoleExtension", "SecurityRoleExtensions", "extension", null, "SecurityRoleExtension"),

                // ── Families the provider exposes; generation now routes through it ──
                New("menuitemdisplay", "AxMenuItemDisplay", "MenuItemDisplays", "menu-item", null, "MenuItemDisplay", ns: NsV1),
                New("menuitemaction", "AxMenuItemAction", "MenuItemActions", "menu-item", null, "MenuItemAction", ns: NsV1),
                New("menuitemoutput", "AxMenuItemOutput", "MenuItemOutputs", "menu-item", null, "MenuItemOutput", ns: NsV1),
                New("report", "AxReport", "Reports", "report", null, "Report", ns: NsV2),
                New("service", "AxService", "Services", "custom-service", null, "Service"),
                New("servicegroup", "AxServiceGroup", "ServiceGroups", "custom-service", null, "ServiceGroup"),
                New("securityrole", "AxSecurityRole", "SecurityRoles", "role", null, "SecurityRole"),
                New("securityduty", "AxSecurityDuty", "SecurityDuties", "duty", null, "SecurityDuty"),
                New("securityprivilege", "AxSecurityPrivilege", "SecurityPrivileges", "privilege", null, "SecurityPrivilege"),
                New("securitypolicy", "AxSecurityPolicy", "SecurityPolicies", "security-policy", "security-policy", "SecurityPolicy"),
                // The AOT node Visual Studio labels "workflow type".
                New("workflowtemplate", "AxWorkflowTemplate", "WorkflowTemplates", "workflow", null, "WorkflowTemplate", ns: NsV2),
                New("workflowapproval", "AxWorkflowApproval", "WorkflowApprovals", "workflow", null, "WorkflowApproval", ns: NsV2),
                New("workflowtask", "AxWorkflowTask", "WorkflowTasks", "workflow", null, "WorkflowTask", ns: NsV2),

                // ── The small kinds: each has a generate subcommand of its own ──────
                New("workflowcategory", "AxWorkflowCategory", "WorkflowCategories", "workflow-category", null, "WorkflowCategory", indexed: false, ns: NsV2),
                New("configurationkey", "AxConfigurationKey", "ConfigurationKeys", "configuration-key", null, "ConfigurationKey"),
                New("labelfile", "AxLabelFile", "LabelFiles", "label-file", null, "LabelFile"),
                New("tile", "AxTile", "Tiles", "tile", null, "Tile", ns: NsV1),
                New("menu", "AxMenu", "Menus", "menu", null, "Menu", indexed: false, ns: NsV1),
                New("compositedataentityview", "AxCompositeDataEntityView", "CompositeDataEntityViews", "composite-entity", null, "CompositeDataEntityView", indexed: false),
                New("aggregatedataentity", "AxAggregateDataEntity", "AggregateDataEntities", "aggregate-entity", null, "AggregateDataEntity", indexed: false),
                New("formpart", "AxFormPart", "FormParts", "form-part", null, "FormPart", indexed: false),
                New("resource", "AxResource", "Resources", "resource", null, "Resource", indexed: false),

                // ── Names that exist in this codebase but not on any AOS ────────────
                // Kept so the parity test can assert nothing reads them: an extractor
                // pointed at a folder that never exists silently indexes zero objects
                // and looks healthy (finding G1).
                New("workspace", "AxWorkspace", null, null, null, "Workspace", existsInStandardAot: false),
                New("reportssrs", "AxReportSsrs", null, null, null, "Report", existsInStandardAot: false, indexed: false),
            };
        }

        /// <summary>Namespaces the MetaModel DataContracts declare. Only three are in use.</summary>
        public const string NsV1 = "Microsoft.Dynamics.AX.Metadata.V1";

        /// <inheritdoc cref="NsV1"/>
        public const string NsV2 = "Microsoft.Dynamics.AX.Metadata.V2";

        /// <inheritdoc cref="NsV1"/>
        public const string NsV6 = "Microsoft.Dynamics.AX.Metadata.V6";

        private static ObjectTypeInfo New(
            string kind,
            string rootElement,
            string providerCollection,
            string generateCommand,
            string mcpObjectType,
            string namingKind,
            bool xsi = false,
            bool abstractRoot = false,
            bool existsInStandardAot = true,
            bool indexed = true,
            string aotSubfolder = null,
            string metaModelType = null,
            string ns = null)
            => new ObjectTypeInfo(
                kind, rootElement,
                aotSubfolder ?? rootElement,
                metaModelType ?? rootElement,
                providerCollection, generateCommand, mcpObjectType, namingKind,
                ns ?? string.Empty,
                xsi, abstractRoot, existsInStandardAot, indexed);

        /// <summary>Every registered type.</summary>
        public static IReadOnlyList<ObjectTypeInfo> All
        {
            get { return _all; }
        }

        /// <summary>Normalise a caller-supplied kind: lower-case, drop separators ("menu-item" → "menuitem").</summary>
        public static string NormalizeKind(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = new char[value.Length];
            var n = 0;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (char.IsLetterOrDigit(c)) chars[n++] = char.ToLowerInvariant(c);
            }
            return new string(chars, 0, n);
        }

        /// <summary>Look up by kind id, root element, or AOT folder name. Null when unknown.</summary>
        public static ObjectTypeInfo Find(string kindOrRoot)
        {
            var key = NormalizeKind(kindOrRoot);
            if (key.Length == 0) return null;
            for (var i = 0; i < _all.Length; i++)
            {
                var t = _all[i];
                if (key == t.Kind
                    || key == NormalizeKind(t.RootElement)
                    || key == NormalizeKind(t.AotSubfolder))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// The type that extends <paramref name="baseKindOrRoot"/>, or null when the AOT
        /// has no extension form for it. Every registered extension kind is its base kind
        /// plus "extension", so the relation needs no second table to drift out of step:
        /// the returned entry carries the real root element ("query" yields
        /// <c>AxQuerySimpleExtension</c>, not the <c>AxQueryExtension</c> that no
        /// assembly declares) and the provider collection the bridge writes through.
        /// </summary>
        public static ObjectTypeInfo ExtensionOf(string baseKindOrRoot)
        {
            var baseType = Find(baseKindOrRoot);
            if (baseType == null) return null;
            // Extensions do not nest: an extension has no extension of its own.
            if (baseType.Kind.EndsWith("extension", StringComparison.Ordinal)) return null;

            var wanted = baseType.Kind + "extension";
            for (var i = 0; i < _all.Length; i++)
                if (_all[i].Kind == wanted)
                    return _all[i];
            return null;
        }

        /// <summary>AOT subfolder for a kind. Throws for an unknown kind — callers hard-code folders otherwise.</summary>
        public static string Subfolder(string kindOrRoot)
        {
            var t = Find(kindOrRoot);
            if (t == null)
                throw new ArgumentException("Unknown AOT object type '" + kindOrRoot + "'.", "kindOrRoot");
            return t.AotSubfolder;
        }

        /// <summary>Kinds the bridge can write through <c>IMetadataProvider</c>, kind → collection name.</summary>
        public static Dictionary<string, string> BridgeCollections()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _all.Length; i++)
                if (_all[i].ProviderCollection != null)
                    map[_all[i].Kind] = _all[i].ProviderCollection;
            return map;
        }

        /// <summary>Bridge-writable kinds, kind → fully qualified MetaModel type name.</summary>
        public static Dictionary<string, string> BridgeMetaModelTypes(string metaModelNamespace)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < _all.Length; i++)
                if (_all[i].ProviderCollection != null)
                    map[_all[i].Kind] = metaModelNamespace + _all[i].MetaModelType;
            return map;
        }

        /// <summary>Root elements whose files are unreadable without the XMLSchema-instance declaration.</summary>
        public static HashSet<string> XsiRequiredRoots()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < _all.Length; i++)
                if (_all[i].RequiresXsiNamespace)
                    set.Add(_all[i].RootElement);
            return set;
        }

        /// <summary>Root elements that are abstract MetaModel bases and need a concrete <c>i:type</c>.</summary>
        public static HashSet<string> AbstractRoots()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < _all.Length; i++)
                if (_all[i].AbstractRoot)
                    set.Add(_all[i].RootElement);
            return set;
        }

        /// <summary>
        /// AOT folders that identify a directory as a model root. Only folders that
        /// actually exist on an AOS qualify — a name that matches nothing can never
        /// prove anything.
        /// </summary>
        public static string[] ModelMarkerFolders()
        {
            var list = new List<string>();
            for (var i = 0; i < _all.Length; i++)
                if (_all[i].ExistsInStandardAot && _all[i].Indexed)
                    list.Add(_all[i].AotSubfolder);
            return list.ToArray();
        }
    }
}
