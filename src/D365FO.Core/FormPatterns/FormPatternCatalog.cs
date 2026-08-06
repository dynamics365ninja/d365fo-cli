namespace D365FO.Core.FormPatterns;

/// <summary>
/// D365FO Form Pattern Catalog — registry and lookups. Port of the upstream
/// MCP <c>src/knowledge/formPatterns</c> catalog (curated from Microsoft Learn
/// pattern guideline docs and reference forms in PackagesLocalDirectory).
/// </summary>
public static class FormPatternCatalog
{
    /// <summary>
    /// Input controls allowed inside field-oriented sub-patterns (Fields and
    /// Field Groups, …). Intentionally generous — exotic but legitimate input
    /// types should not produce false errors.
    /// </summary>
    public static readonly string[] InputControlTypes =
    {
        "String", "Int", "Integer", "Int64", "Real", "Date", "UtcDateTime",
        "DateTime", "Time", "CheckBox", "ComboBox", "Radio", "RadioButton",
        "ReferenceGroup", "SegmentedEntry", "MultilineText", "ListBox",
        "Control", // extension/custom controls (e.g. dimension controls)
    };

    // ── Shared NodeSpec fragments ────────────────────────────────────────────

    private static NodeSpec ActionPane(Occurrence occurrence = Occurrence.Required) => new()
    {
        Id = "ActionPane",
        ControlTypes = new[] { "ActionPane" },
        Occurrence = occurrence,
        NameHint = "ActionPane",
        Extra = ExtraChildren.Any,
    };

    private static NodeSpec FilterGroup(Occurrence occurrence = Occurrence.Optional) => new()
    {
        Id = "FilterGroup",
        ControlTypes = new[] { "Group" },
        Occurrence = occurrence,
        NameHint = "CustomFilterGroup",
        Properties = new Dictionary<string, string> { ["Style"] = "CustomFilter" },
        RequiresSubPattern = true,
        AllowedSubPatterns = new[] { "CustomAndQuickFilters", "CustomFilters" },
        Extra = ExtraChildren.Any,
    };

    private static NodeSpec MainGrid(Occurrence occurrence = Occurrence.Required) => new()
    {
        Id = "Grid",
        ControlTypes = new[] { "Grid" },
        Occurrence = occurrence,
        NameHint = "Grid",
        Extra = ExtraChildren.Any,
    };

    private static NodeSpec CommitButtons() => new()
    {
        Id = "CommitButtonGroup",
        ControlTypes = new[] { "ButtonGroup" },
        Occurrence = Occurrence.Required,
        NameHint = "ButtonGroup",
        Properties = new Dictionary<string, string> { ["Style"] = "DialogCommitContainer" },
        Children = new NodeSpec[]
        {
            new()
            {
                Id = "CommitButton",
                ControlTypes = new[] { "CommandButton", "Button", "MenuItemButton" },
                Occurrence = Occurrence.OneOrMore,
            },
        },
        Extra = ExtraChildren.Any,
    };

    // ── Top-level patterns ───────────────────────────────────────────────────

    public static readonly IReadOnlyList<FormPatternSpec> Patterns = new[]
    {
        new FormPatternSpec
        {
            Id = "SimpleList",
            XmlName = "SimpleList",
            DisplayName = "Simple List",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Maintains data for simple entities as a single editable grid with fewer than ~10 fields per record. "
                    + "The default pattern for setup/group tables.",
            WhenToUse = new[]
            {
                "Simple entity (setup table, group table) with < 10 fields per record",
                "Users maintain records directly in a grid",
                "No detail panel is needed — the grid shows everything",
            },
            WhenNotToUse = new[]
            {
                "More than ~10 fields → use Simple List & Details",
                "Read-only browsing entry point with FactBoxes → use List Page",
                "Complex master entity → use Details Master",
            },
            ReferenceForms = new[] { "CustGroup", "VendGroup", "CustClassificationGroup" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "SimpleList" },
            RequiresDataSource = "one",
            Root = new[] { ActionPane(), FilterGroup(), MainGrid() },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "Usually no form methods are needed — the grid binds straight to the datasource.",
                "Override the datasource initValue() to default new-record fields.",
                "Override the datasource validateWrite() for cross-field validation before save.",
            },
        },
        new FormPatternSpec
        {
            Id = "SimpleListDetails",
            XmlName = "SimpleListDetails",
            DisplayName = "Simple List & Details - List Grid",
            Versions = new[] { "1.3", "1.2", "1.1", "1.0" },
            Purpose = "Maintains data for entities of medium complexity: a left navigation list (2-3 fields) "
                    + "plus a right details panel. The default Simple List & Details variant.",
            WhenToUse = new[]
            {
                "Entity of medium complexity (~10-25 fields)",
                "Users pick a record from a compact list and edit details on the right",
                "2-3 identifying fields are enough for the navigation list",
            },
            WhenNotToUse = new[]
            {
                "More than 3 fields needed in the list → Simple List & Details - Tabular Grid",
                "Hierarchical data → Simple List & Details - Tree",
                "Fewer than ~10 fields → Simple List",
            },
            ReferenceForms = new[] { "PaymTerm", "CustPaymModeTable", "BankGroup" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "SimpleListDetails" },
            RequiresDataSource = "one",
            Root = new[]
            {
                ActionPane(),
                new NodeSpec
                {
                    Id = "NavigationList",
                    ControlTypes = new[] { "Group" },
                    Occurrence = Occurrence.Required,
                    NameHint = "GridContainer",
                    Properties = new Dictionary<string, string> { ["Style"] = "SidePanel" },
                    RequiresSubPattern = true,
                    AllowedSubPatterns = new[] { "SidePanel" },
                    Extra = ExtraChildren.Any,
                },
                new NodeSpec
                {
                    Id = "DetailsPanel",
                    ControlTypes = new[] { "Group", "Tab" },
                    Occurrence = Occurrence.Required,
                    NameHint = "DetailsGroup",
                    RequiresSubPattern = true,
                    AllowedSubPatterns = new[] { "FieldsFieldGroups", "TabularFields", "ToolbarAndFields" },
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "Override the datasource active() to refresh dependent detail content when selection changes.",
                "Override the datasource initValue() to default new-record fields.",
            },
            Notes = new[]
            {
                "Tabular Grid and Tree variants share the SimpleListDetails xmlName in metadata — "
                + "the variant is determined by the list panel content (tabular grid / tree control).",
            },
        },
        new FormPatternSpec
        {
            Id = "DetailsMaster",
            XmlName = "DetailsMaster",
            DisplayName = "Details Master",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Displays the details of a complex master entity on FastTabs, with a grid view and a details view "
                    + "(e.g. customers, vendors, products).",
            WhenToUse = new[]
            {
                "Complex primary/master entity with many fields organized into FastTabs",
                "Users switch between a grid (browse) view and a details view",
            },
            WhenNotToUse = new[]
            {
                "Header + lines transaction entity → Details Transaction",
                "More than ~15 FastTabs that can be grouped → Details Master w/ Standard Tabs",
                "Medium complexity entity → Simple List & Details",
            },
            ReferenceForms = new[] { "CustTable", "VendTable", "EcoResProductDetailsExtended" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "DetailsFormMaster" },
            RequiresDataSource = "one",
            Root = new[]
            {
                ActionPane(),
                FilterGroup(),
                new NodeSpec
                {
                    Id = "HeaderGroup",
                    ControlTypes = new[] { "Group" },
                    Occurrence = Occurrence.Optional,
                    NameHint = "HeaderGroup",
                    Extra = ExtraChildren.Any,
                },
                new NodeSpec
                {
                    Id = "FastTabs",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    NameHint = "Tab",
                    Properties = new Dictionary<string, string> { ["Style"] = "FastTabs" },
                    Children = new NodeSpec[]
                    {
                        new()
                        {
                            Id = "FastTabPage",
                            ControlTypes = new[] { "TabPage" },
                            Occurrence = Occurrence.OneOrMore,
                            RequiresSubPattern = true,
                            Extra = ExtraChildren.Any,
                        },
                    },
                    Extra = ExtraChildren.None,
                },
            },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "Override form init() to capture caller context (element.args()).",
                "Override the datasource active() to enable/disable controls per record state.",
                "Override the datasource validateWrite()/write() for save-time logic.",
            },
        },
        new FormPatternSpec
        {
            Id = "DetailsMasterTabs",
            XmlName = "DetailsMasterTabs",
            VariantOf = "DetailsMaster",
            DisplayName = "Details Master w/ Standard Tabs",
            Versions = new[] { "1.0" },
            Purpose = "Details Master variant for forms with a large number of FastTabs (>15) grouped into "
                    + "categories using standard tabs.",
            WhenToUse = new[] { "More than ~15 FastTabs that can be grouped into categories" },
            ReferenceForms = new[] { "HcmWorker" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "DetailsFormMaster" },
            RequiresDataSource = "one",
            Root = new[]
            {
                ActionPane(),
                FilterGroup(),
                new NodeSpec
                {
                    Id = "StandardTabs",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "DetailsTransaction",
            XmlName = "DetailsTransaction",
            DisplayName = "Details Transaction",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Displays the details of a complex transaction entity and its lines — an order header plus "
                    + "order lines (e.g. sales orders, purchase orders).",
            WhenToUse = new[]
            {
                "Header + lines transaction entity (order/journal with line items)",
                "Two related datasources: header table and lines table",
                "Users need both a header view and a line-editing grid",
            },
            WhenNotToUse = new[]
            {
                "Master entity without lines → Details Master",
                "Simple journal-style entry → consider Task patterns only for migrations",
            },
            ReferenceForms = new[] { "SalesTable", "PurchTable", "ProjInvoiceJournal" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "DetailsFormTransaction" },
            RequiresDataSource = "headerLines",
            Root = new[]
            {
                ActionPane(),
                FilterGroup(),
                new NodeSpec
                {
                    Id = "HeaderLinesTabs",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    NameHint = "Tab",
                    Properties = new Dictionary<string, string> { ["Style"] = "FastTabs" },
                    Children = new NodeSpec[]
                    {
                        new()
                        {
                            // Header pages follow field sub-patterns; the Lines page holds
                            // ActionPaneTab + Grid and typically has no sub-pattern.
                            Id = "HeaderOrLinesPage",
                            ControlTypes = new[] { "TabPage" },
                            Occurrence = Occurrence.OneOrMore,
                            RequiresSubPattern = false,
                            Extra = ExtraChildren.Any,
                        },
                    },
                    Extra = ExtraChildren.None,
                },
            },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "Link the lines datasource to the header datasource (JoinSource + Active link type).",
                "Override the lines datasource initValue() to default line fields from the header.",
                "Override the header datasource active() to refresh totals/line state.",
            },
        },
        new FormPatternSpec
        {
            Id = "Dialog",
            XmlName = "Dialog",
            DisplayName = "Dialog - Basic",
            Versions = new[] { "1.2", "1.1", "1.0" },
            Purpose = "Modal dialog that gathers or shows a small set of information, committed with OK/Cancel.",
            WhenToUse = new[]
            {
                "Gather a set of inputs before running an action",
                "Quick-create scenarios with a handful of fields",
            },
            WhenNotToUse = new[]
            {
                "Fewer than ~5 fields attached to a button → Drop Dialog",
                "Content grouped into FastTabs/tabs → Dialog FastTabs/Tabs variants",
                "Read-only info → Dialog - Read Only",
            },
            ReferenceForms = new[] { "ProjTableCreate", "CustOpenBalance" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "Dialog" },
            RequiresDataSource = "none",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "DialogBody",
                    ControlTypes = new[] { "Group" },
                    Occurrence = Occurrence.Required,
                    NameHint = "DialogBody",
                    Properties = new Dictionary<string, string> { ["Style"] = "DialogContent" },
                    RequiresSubPattern = true,
                    AllowedSubPatterns = new[] { "FieldsFieldGroups", "TabularFields", "FillText" },
                    Extra = ExtraChildren.Any,
                },
                CommitButtons(),
            },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "Override form init() to read caller args (element.args()) and default field values.",
                "Override the OK command button clicked() to validate and apply the action.",
                "For quick-create dialogs bind a datasource and override its initValue().",
            },
        },
        new FormPatternSpec
        {
            Id = "DropDialog",
            XmlName = "DropDialog",
            VariantOf = "Dialog",
            DisplayName = "Drop Dialog",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Lightweight dialog dropped from a button to gather a small set of inputs (<5 fields) "
                    + "that provide context for an action.",
            WhenToUse = new[] { "Action confirmation/parameters with fewer than ~5 fields, anchored to a button" },
            ReferenceForms = new[] { "CustCollectionsNewActivityAction", "SalesEstimates" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "DropDialog" },
            RequiresDataSource = "none",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "DialogBody",
                    ControlTypes = new[] { "Group" },
                    Occurrence = Occurrence.Required,
                    RequiresSubPattern = true,
                    AllowedSubPatterns = new[] { "FieldsFieldGroups", "TabularFields", "FillText" },
                    Extra = ExtraChildren.Any,
                },
                CommitButtons(),
            },
            ExtraRoot = ExtraChildren.None,
        },
        new FormPatternSpec
        {
            Id = "TableOfContents",
            XmlName = "TableOfContents",
            DisplayName = "Table of Contents",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Displays setup/parameters information or loosely related information sets as a vertical "
                    + "table-of-contents navigation with one content region per entry.",
            WhenToUse = new[]
            {
                "Module parameters forms (e.g. CustParameters)",
                "Loosely related groups of setup fields navigated from a vertical list",
            },
            WhenNotToUse = new[] { "A single simple entity → Simple List", "A complex entity → Details Master" },
            ReferenceForms = new[] { "CustParameters", "VendParameters", "BankParameters" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "TableOfContents" },
            RequiresDataSource = "one",
            Root = new[]
            {
                ActionPane(Occurrence.Optional),
                new NodeSpec
                {
                    Id = "TOCTabs",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    // No Style: a Tab control's Style is the TabStyle enum
                    // (Auto|Tabs|FastTabs|VerticalTabs|IndexTabs|Panorama|DropList), and
                    // "TOCList" is not a member — a form carrying it cannot be deserialized at
                    // all. The table-of-contents look comes from the Design's own
                    // Style=TableOfContents above; shipped TOC forms (CustParameters) leave the
                    // Tab control's Style unset.
                    Children = new NodeSpec[]
                    {
                        new()
                        {
                            Id = "TOCSection",
                            ControlTypes = new[] { "TabPage" },
                            Occurrence = Occurrence.OneOrMore,
                            RequiresSubPattern = true,
                            AllowedSubPatterns = new[]
                            {
                                "FieldsFieldGroups", "TabularFields", "FillText",
                                "ToolbarAndList", "ToolbarAndFields", "NestedSimpleListDetails",
                            },
                            Extra = ExtraChildren.Any,
                        },
                    },
                    Extra = ExtraChildren.None,
                },
            },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "Parameters forms typically use a single-record datasource with InsertIfEmpty=Yes.",
                "Override form init() + datasource executeQuery() when sections load related tables.",
            },
        },
        new FormPatternSpec
        {
            Id = "Lookup",
            XmlName = "Lookup",
            DisplayName = "Lookup - Basic",
            Versions = new[] { "1.2", "1.1", "1.0" },
            Purpose = "Form used as a lookup: a grid (or tree) optimized for picking a value, with optional "
                    + "filters or buttons.",
            WhenToUse = new[]
            {
                "Custom lookup replacing the auto-generated one (form name conventionally ends in \"Lookup\")",
                "Pick-a-value scenarios launched from a control",
            },
            WhenNotToUse = new[]
            {
                "A record preview is needed → Lookup w/ Preview",
                "Multiple lookup views (grid + tree) → Lookup w/ Tabs",
            },
            ReferenceForms = new[] { "SysLanguageLookup", "HcmWorkerLookup", "CaseCategoryLookup" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "Lookup" },
            RequiresDataSource = "one",
            Root = new[] { FilterGroup(), MainGrid() },
            // Lookups may add button groups / preview panes around the grid
            ExtraRoot = ExtraChildren.Any,
            LifecycleGuidance = new[]
            {
                "Override form init() to read the calling control via element.args().",
                "Override the datasource executeQuery() to apply context filters from the caller.",
                "Use SysTableLookup/selectMode patterns to return the picked value.",
            },
        },
        new FormPatternSpec
        {
            Id = "ListPage",
            XmlName = "ListPage",
            DisplayName = "List Page",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Read-optimized grid entry point for browsing records and acting on them, typically with "
                    + "FactBoxes in the Parts node and a corresponding Details form.",
            WhenToUse = new[]
            {
                "Primary navigation entry point for a master entity",
                "Browsing/acting on records rather than editing them in place",
                "FactBoxes show related info; opening a record navigates to a Details form",
            },
            WhenNotToUse = new[]
            {
                "In-grid editing of a simple entity → Simple List",
                "New forms generally favor Details Master with its grid view",
            },
            ReferenceForms = new[] { "SalesTableListPage", "CustTableListPage" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "ListPage" },
            RequiresDataSource = "one",
            Root = new[] { ActionPane(), FilterGroup(), MainGrid() },
            ExtraRoot = ExtraChildren.None,
            LifecycleGuidance = new[]
            {
                "List pages traditionally pair with a *ListPageInteraction class for logic.",
                "Keep the grid read-only; actions live in the ActionPane.",
            },
        },
        new FormPatternSpec
        {
            Id = "Workspace",
            XmlName = "Workspace",
            DisplayName = "Workspace (Panorama)",
            Versions = new[] { "1.0" },
            Purpose = "Activity overview page: a horizontally scrolling panorama with a tile/KPI summary section "
                    + "followed by list/chart/link sections. Primary means of navigation for an activity.",
            WhenToUse = new[]
            {
                "Overview dashboard for an operational activity (work queues, KPIs, quick links)",
                "Primary navigation hub combining tiles, lists, and links",
            },
            WhenNotToUse = new[]
            {
                "New workspaces should prefer the Operational Workspace pattern",
                "Entity maintenance → Details Master / Simple List",
            },
            ReferenceForms = new[] { "FmClerkWorkspace" },
            DesignProperties = new Dictionary<string, string> { ["Style"] = "Workspace" },
            RequiresDataSource = "none",
            Root = new[]
            {
                ActionPane(Occurrence.Optional),
                new NodeSpec
                {
                    Id = "PanoramaBody",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    NameHint = "PanoramaBody",
                    Properties = new Dictionary<string, string> { ["Style"] = "Panorama" },
                    Children = new NodeSpec[]
                    {
                        new()
                        {
                            Id = "PanoramaSection",
                            ControlTypes = new[] { "TabPage" },
                            Occurrence = Occurrence.OneOrMore,
                            Extra = ExtraChildren.Any,
                        },
                    },
                    Extra = ExtraChildren.None,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            LifecycleGuidance = new[]
            {
                "Tile counts come from menu items with query-based counts or unbound fields refreshed in executeQuery().",
                "Each panorama list section typically binds its own datasource or Form Part.",
            },
        },
        new FormPatternSpec
        {
            Id = "WorkspaceOperational",
            XmlName = "WorkspaceOperational",
            VariantOf = "Workspace",
            DisplayName = "Operational Workspace",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "The preferred, performance-enhanced workspace variant: sections render via Form Part "
                    + "controls so content loads on demand.",
            WhenToUse = new[] { "All new workspaces" },
            ReferenceForms = new[] { "FmClerkWorkspace", "SalesOrderProcessingWorkspace" },
            RequiresDataSource = "none",
            Root = new[]
            {
                ActionPane(Occurrence.Optional),
                new NodeSpec
                {
                    Id = "PanoramaBody",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName/versions to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "FormPartSectionList",
            XmlName = "FormPartSectionList",
            XmlAliases = new[] { "SectionList", "WorkspaceSectionList" },
            DisplayName = "Form Part Section List",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "A list shown in a workspace section — modeled as a separate form and rendered in the "
                    + "workspace via a Form Part control.",
            WhenToUse = new[] { "List section of an Operational Workspace (work queue, recent items)" },
            ReferenceForms = new[] { "FMRentalsToStartPart" },
            RequiresDataSource = "one",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "SectionBody",
                    ControlTypes = new[] { "Group", "Grid" },
                    Occurrence = Occurrence.OneOrMore,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining. Variant \"Section List - Double\" adds a secondary hidden list." },
        },
        new FormPatternSpec
        {
            Id = "HubPartChart",
            XmlName = "HubPartChart",
            XmlAliases = new[] { "SectionChart", "WorkspaceSectionChart" },
            DisplayName = "Hub Part Chart",
            Versions = new[] { "1.0" },
            Purpose = "A chart shown in a workspace section via a Form Part control.",
            WhenToUse = new[] { "Chart section of an Operational Workspace" },
            ReferenceForms = new[] { "VendInvoiceJourCountChart" },
            RequiresDataSource = "none",
            Root = new[]
            {
                new NodeSpec { Id = "Chart", ControlTypes = new[] { "*" }, Occurrence = Occurrence.Required },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "FormPartFactboxGrid",
            XmlName = "FormPartFactboxGrid",
            XmlAliases = new[] { "FactBoxGrid", "FormPartFactBoxGrid" },
            DisplayName = "Form Part FactBox Grid",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "FactBox showing a child collection of related records as a small grid.",
            WhenToUse = new[]
            {
                "Related child records (e.g. contacts of a customer) shown beside a parent form",
                "Modeled as a separate form, referenced from the parent's Parts node",
            },
            ReferenceForms = new[] { "ContactsInfoPart" },
            RequiresDataSource = "one",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "FactBoxGrid",
                    ControlTypes = new[] { "Grid" },
                    Occurrence = Occurrence.Required,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "FormPartFactboxCard",
            XmlName = "FormPartFactboxCard",
            XmlAliases = new[] { "FactBoxCard", "FormPartFactBoxCard" },
            DisplayName = "Form Part FactBox Card",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "FactBox showing a set of related fields for a single record (card style).",
            WhenToUse = new[] { "A handful of related fields (e.g. customer statistics) beside a parent form" },
            ReferenceForms = new[] { "CustStatisticsStatistics" },
            RequiresDataSource = "one",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "CardBody",
                    ControlTypes = new[] { "Group", "*" },
                    Occurrence = Occurrence.Optional,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "SimpleDetails",
            XmlName = "SimpleDetails",
            XmlAliases = new[] { "SimpleDetailsToolbarFields", "SimpleDetailsWToolbar" },
            DisplayName = "Simple Details w/ Toolbar and Fields",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Shows fields for a single base record with an optional toolbar — the default Simple Details variant.",
            WhenToUse = new[]
            {
                "Form focused on ONE record (no grid/list navigation)",
                "A flat set of fields with a toolbar for actions",
            },
            WhenNotToUse = new[]
            {
                "Fields organized into FastTabs → Simple Details w/ FastTabs",
                "Multiple records → Simple List / Simple List & Details",
            },
            ReferenceForms = new[] { "AgreementLine" },
            RequiresDataSource = "one",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "Toolbar",
                    ControlTypes = new[] { "ActionPane", "ActionPaneTab" },
                    Occurrence = Occurrence.Optional,
                    Extra = ExtraChildren.Any,
                },
                new NodeSpec
                {
                    Id = "FieldsBody",
                    ControlTypes = new[] { "Group", "Tab" },
                    Occurrence = Occurrence.OneOrMore,
                    RequiresSubPattern = false,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            LifecycleGuidance = new[]
            {
                "Override the datasource active()/validateWrite() for record-state logic.",
            },
            Notes = new[]
            {
                "Variants (FastTabs / Standard Tabs / Panorama) share the Simple Details class; "
                + "the variant is the body container style. Exact xmlNames to be confirmed by mining.",
            },
        },
        new FormPatternSpec
        {
            Id = "TaskSingle",
            XmlName = "TaskSingle",
            XmlAliases = new[] { "Task", "SimpleTask" },
            DisplayName = "Task Single (legacy)",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Legacy AX 2012-style entity form (Overview + General tabs). MIGRATION ONLY — do not use for new forms.",
            WhenToUse = new[] { "Migrating an AX 2012 form with Overview/General tabs and a single datasource" },
            WhenNotToUse = new[] { "Any NEW form — use Simple List, Simple List & Details, or Details Master instead" },
            ReferenceForms = new[] { "LedgerJournalTable" },
            RequiresDataSource = "one",
            Root = new[]
            {
                new NodeSpec { Id = "ActionPane", ControlTypes = new[] { "ActionPane" }, Occurrence = Occurrence.Optional, Extra = ExtraChildren.Any },
                new NodeSpec { Id = "TaskTabs", ControlTypes = new[] { "Tab" }, Occurrence = Occurrence.Required, Extra = ExtraChildren.Any },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "Legacy pattern. xmlName to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "TaskDouble",
            XmlName = "TaskDouble",
            VariantOf = "TaskSingle",
            DisplayName = "Task Double (legacy)",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Legacy AX 2012-style transaction form (two stacked Overview/General sets, header + lines). MIGRATION ONLY.",
            WhenToUse = new[] { "Migrating an AX 2012 header+lines form that does not fit Details Transaction" },
            WhenNotToUse = new[] { "Any NEW form — use Details Transaction instead" },
            ReferenceForms = new[] { "HRMAbsenceTableHistory", "LedgerJournalTransDaily" },
            RequiresDataSource = "headerLines",
            Root = new[]
            {
                new NodeSpec { Id = "ActionPane", ControlTypes = new[] { "ActionPane" }, Occurrence = Occurrence.Optional, Extra = ExtraChildren.Any },
                new NodeSpec { Id = "UpperTabs", ControlTypes = new[] { "Tab" }, Occurrence = Occurrence.Required, Extra = ExtraChildren.Any },
                new NodeSpec { Id = "LowerTabs", ControlTypes = new[] { "Tab", "Group" }, Occurrence = Occurrence.Optional, Extra = ExtraChildren.Any },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "Legacy pattern. xmlName to be confirmed by mining." },
        },
        new FormPatternSpec
        {
            Id = "Wizard",
            XmlName = "Wizard",
            DisplayName = "Wizard",
            Versions = new[] { "1.1", "1.0" },
            Purpose = "Displays a sequence of tab pages gathering information in a predetermined order, "
                    + "navigated with Back/Next/Finish buttons (backed by a SysWizard class).",
            WhenToUse = new[]
            {
                "Multi-step guided input where order matters",
                "Setup/onboarding flows broken into discrete steps",
            },
            WhenNotToUse = new[] { "A single set of inputs → Dialog" },
            ReferenceForms = new[] { "WrkCtrBulkResReqEditWizard" },
            RequiresDataSource = "none",
            Root = new[]
            {
                new NodeSpec
                {
                    Id = "WizardTabs",
                    ControlTypes = new[] { "Tab" },
                    Occurrence = Occurrence.Required,
                    Children = new NodeSpec[]
                    {
                        new() { Id = "WizardStep", ControlTypes = new[] { "TabPage" }, Occurrence = Occurrence.OneOrMore, Extra = ExtraChildren.Any },
                    },
                    Extra = ExtraChildren.None,
                },
                new NodeSpec
                {
                    Id = "NavigationButtons",
                    ControlTypes = new[] { "ButtonGroup", "Group" },
                    Occurrence = Occurrence.Optional,
                    Extra = ExtraChildren.Any,
                },
            },
            ExtraRoot = ExtraChildren.Any,
            LifecycleGuidance = new[]
            {
                "Pair the form with a SysWizard subclass driving step navigation and validation.",
                "Override form init() to wire the wizard class; validate each step in the wizard class, not the form.",
            },
        },
    };

    // ── Sub-patterns ─────────────────────────────────────────────────────────

    private static SubPatternSpec WorkspaceSection(
        string id, string xmlName, string[] aliases, string displayName, string purpose,
        string[]? referenceForms = null) => new()
    {
        Id = id,
        XmlName = xmlName,
        XmlAliases = aliases,
        DisplayName = displayName,
        Versions = new[] { "1.0" },
        AppliesToControlTypes = new[] { "Group", "TabPage" },
        ParentPatterns = new[] { "Workspace", "WorkspaceOperational" },
        Purpose = purpose,
        ReferenceForms = referenceForms,
        Root = Array.Empty<NodeSpec>(),
        ExtraRoot = ExtraChildren.Any,
        Notes = new[] { "xmlName to be confirmed by mining." },
    };

    public static readonly IReadOnlyList<SubPatternSpec> SubPatterns = new[]
    {
        new SubPatternSpec
        {
            Id = "CustomAndQuickFilters",
            XmlName = "CustomAndQuickFilters",
            DisplayName = "Custom and Quick Filters",
            Versions = new[] { "1.1", "1.0" },
            AppliesToControlTypes = new[] { "Group" },
            Purpose = "Filter group above a grid containing a QuickFilter plus optional modeled custom filter fields. "
                    + "Used when a QuickFilter is required (the default for list-style patterns).",
            ReferenceForms = new[] { "CustTable (CustomFilterGroup)", "CustGroup (CustomFilterGroup)" },
            Root = new NodeSpec[]
            {
                new()
                {
                    Id = "QuickFilter",
                    ControlTypes = new[] { "QuickFilterControl" },
                    Occurrence = Occurrence.Required,
                    NameHint = "QuickFilterControl",
                },
            },
            // Custom filter input controls may follow the QuickFilter
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "CustomFilters",
            XmlName = "CustomFilters",
            DisplayName = "Custom Filters",
            Versions = new[] { "1.1", "1.0" },
            AppliesToControlTypes = new[] { "Group" },
            Purpose = "Filter group with modeled custom filter fields only — no QuickFilter required.",
            ReferenceForms = new[] { "LedgerJournalTable (TopFields)" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "FieldsFieldGroups",
            XmlName = "FieldsFieldGroups",
            DisplayName = "Fields and Field Groups",
            Versions = new[] { "1.1", "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Responsive column layout for containers that contain only fields and one level of field groups. "
                    + "The pattern sets WidthMode/HeightMode to SizeToContent — manual widths and SizeToAvailable are not allowed.",
            ReferenceForms = new[] { "InventLocation (LocationNames)", "CustTable (FastTabs pages)" },
            Root = new NodeSpec[]
            {
                new()
                {
                    Id = "FieldGroup",
                    ControlTypes = new[] { "Group" },
                    Occurrence = Occurrence.ZeroOrMore,
                    // Only one level of group depth is allowed — nested groups are rejected
                    Extra = ExtraChildren.Of(InputControlTypes),
                },
            },
            ExtraRoot = ExtraChildren.Of(InputControlTypes),
            Notes = new[]
            {
                "Static text and images are NOT allowed (use HelpText or form-level help instead).",
                "More than one level of group nesting is not allowed.",
            },
        },
        new SubPatternSpec
        {
            Id = "TabularFields",
            XmlName = "TabularFields",
            DisplayName = "Tabular Fields",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Structured grid-like layout of fields, intended primarily for totals.",
            ReferenceForms = new[] { "LedgerJournalTransVendPaym (Balances)" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "FillText",
            XmlName = "FillText",
            DisplayName = "Fill Text",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "A single input control that requires full container width (e.g. notes).",
            ReferenceForms = new[] { "FmRental (Notes)" },
            Root = new NodeSpec[]
            {
                new()
                {
                    Id = "FullWidthField",
                    ControlTypes = new[] { "String", "MultilineText" },
                    Occurrence = Occurrence.Required,
                },
            },
            ExtraRoot = ExtraChildren.None,
        },
        new SubPatternSpec
        {
            Id = "HorizontalFieldsButtonGroup",
            XmlName = "HorizontalFieldsButtonGroup",
            XmlAliases = new[] { "HorizontalFieldsAndButtonGroup", "FieldsAndButtonGroup" },
            DisplayName = "Horizontal Fields and Button Group",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group" },
            Purpose = "A field (or few fields) with an inline action button on the same row.",
            ReferenceForms = new[] { "SalesTable (GroupHeaderAddressHeaderOverview)" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new SubPatternSpec
        {
            Id = "ImagePreview",
            XmlName = "ImagePreview",
            DisplayName = "Image Preview",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Container with an image control and optional related fields.",
            ReferenceForms = new[] { "RetailVisualProfile (Login)" },
            Root = new NodeSpec[]
            {
                new() { Id = "Image", ControlTypes = new[] { "Image" }, Occurrence = Occurrence.Required },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new SubPatternSpec
        {
            Id = "SidePanel",
            XmlName = "SidePanel",
            DisplayName = "Side Panel (navigation list)",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group" },
            ParentPatterns = new[] { "SimpleListDetails" },
            Purpose = "Left-hand navigation list of a Simple List & Details form: an optional QuickFilter above a "
                    + "list-style grid with 2-3 fields per row.",
            ReferenceForms = new[] { "PaymTerm (GridContainer)" },
            Root = new NodeSpec[]
            {
                new()
                {
                    Id = "QuickFilter",
                    ControlTypes = new[] { "QuickFilterControl" },
                    Occurrence = Occurrence.Optional,
                    NameHint = "QuickFilterControl",
                },
                new()
                {
                    Id = "NavigationList",
                    ControlTypes = new[] { "Grid" },
                    Occurrence = Occurrence.Required,
                    Properties = new Dictionary<string, string> { ["Style"] = "List" },
                },
            },
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "WorkspaceSummaryNumbersUnboundFields",
            XmlName = "Workspace_SummaryNumbers_UnboundFields",
            DisplayName = "Workspace Summary Numbers (Unbound Fields)",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group" },
            ParentPatterns = new[] { "Workspace" },
            Purpose = "Tile/KPI summary section of an operational workspace (count tiles as unbound fields).",
            ReferenceForms = new[] { "FmClerkWorkspace" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "ToolbarAndList",
            XmlName = "ToolbarAndList",
            DisplayName = "Toolbar and List",
            Versions = new[] { "1.1", "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Container with actions (ActionPaneTab toolbar) above a grid.",
            ReferenceForms = new[] { "VendTable (TabCommunication)" },
            Root = new NodeSpec[]
            {
                new() { Id = "Toolbar", ControlTypes = new[] { "ActionPaneTab" }, Occurrence = Occurrence.Optional },
                new() { Id = "List", ControlTypes = new[] { "Grid" }, Occurrence = Occurrence.Required, Extra = ExtraChildren.Any },
            },
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "ToolbarAndListDouble",
            XmlName = "ToolbarAndListDouble",
            XmlAliases = new[] { "ToolbarAndList2", "ToolbarAndListsDouble" },
            DisplayName = "Toolbar and List - Double",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Container with actions above TWO grids.",
            ReferenceForms = new[] { "SalesQuickQuote (TabPageExistingItems)" },
            Root = new NodeSpec[]
            {
                new() { Id = "Toolbar", ControlTypes = new[] { "ActionPaneTab" }, Occurrence = Occurrence.Optional },
                new() { Id = "FirstList", ControlTypes = new[] { "Grid" }, Occurrence = Occurrence.Required, Extra = ExtraChildren.Any },
                new() { Id = "SecondList", ControlTypes = new[] { "Grid" }, Occurrence = Occurrence.Required, Extra = ExtraChildren.Any },
            },
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new SubPatternSpec
        {
            Id = "ToolbarAndFields",
            XmlName = "ToolbarAndFields",
            DisplayName = "Toolbar and Fields",
            Versions = new[] { "1.1", "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Container with actions (toolbar) above a set of fields.",
            ReferenceForms = new[] { "HcmPosition (WorkerAssignmentTabPage)" },
            Root = new NodeSpec[]
            {
                new() { Id = "Toolbar", ControlTypes = new[] { "ActionPaneTab" }, Occurrence = Occurrence.Optional },
            },
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "NestedSimpleListDetails",
            XmlName = "NestedSimpleListDetails",
            DisplayName = "Nested Simple List and Details",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Embeds a simpler Simple List & Details layout (list panel + details panel) inside a tab "
                    + "or group of a larger form.",
            ReferenceForms = new[] { "HcmJob (TaskTabPage)" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
        },
        new SubPatternSpec
        {
            Id = "ListPanel",
            XmlName = "ListPanel",
            DisplayName = "List Panel",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Two lists users move items between (e.g. available ↔ selected), typically via SysListPanel.",
            ReferenceForms = new[] { "CLIControls_ListPanel (FormTabPageControl1)" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "Usually built at runtime via SysListPanel — model the container, the class fills it." },
        },
        new SubPatternSpec
        {
            Id = "DimensionEntryControl",
            XmlName = "DimensionEntryControl",
            DisplayName = "Dimension Entry Control",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Tab page or group containing only a financial Dimension Entry Control.",
            ReferenceForms = new[] { "CustTable (TabFinancialDimensions)" },
            Root = new NodeSpec[]
            {
                new() { Id = "DimensionControl", ControlTypes = new[] { "Control", "*" }, Occurrence = Occurrence.Required },
            },
            ExtraRoot = ExtraChildren.None,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        new SubPatternSpec
        {
            Id = "DimensionExpressionBuilder",
            XmlName = "DimensionExpressionBuilder",
            DisplayName = "Dimension Expression Builder",
            Versions = new[] { "1.0" },
            AppliesToControlTypes = new[] { "Group", "TabPage" },
            Purpose = "Container with a Dimension Expression Builder control.",
            ReferenceForms = new[] { "LedgerAllocationRuleDestination" },
            Root = Array.Empty<NodeSpec>(),
            ExtraRoot = ExtraChildren.Any,
            Notes = new[] { "xmlName to be confirmed by mining." },
        },
        WorkspaceSection(
            "WorkspaceSectionTiles", "Workspace_Tiles",
            new[] { "SectionTiles", "WorkspaceTiles" },
            "Section Tiles",
            "Set of count tiles / charts in a workspace summary section (tiles bound to menu items, charts via Form Part controls).",
            new[] { "SalesOrderProcessingWorkspace" }),
        WorkspaceSection(
            "WorkspaceSectionRelatedLinks", "Workspace_Links",
            new[] { "SectionRelatedLinks", "WorkspaceLinks", "Workspace_RelatedLinks" },
            "Section Related Links",
            "Set of hyperlinks (menu item buttons) in a workspace links section.",
            new[] { "SalesOrderProcessingWorkspace" }),
        WorkspaceSection(
            "WorkspaceSectionTabbedList", "Workspace_TabbedList",
            new[] { "SectionTabbedList" },
            "Section Tabbed List",
            "Multiple list variants in one workspace section — only one visible at a time."),
        WorkspaceSection(
            "WorkspaceSectionStackedChart", "Workspace_StackedChart",
            new[] { "SectionStackedChart" },
            "Section Stacked Chart",
            "Up to two charts stacked in an Operational Workspace section."),
        WorkspaceSection(
            "WorkspaceSectionPowerBI", "Workspace_PowerBI",
            new[] { "SectionPowerBI" },
            "Section Power BI",
            "Power BI content section in an Operational Workspace."),
        WorkspaceSection(
            "WorkspacePageFilterGroup", "Workspace_FilterGroup",
            new[] { "WorkspacePageFilterGroup", "Workspace_PageFilter" },
            "Workspace Page Filter Group",
            "A single page-level filter applied across workspace sections."),
        WorkspaceSection(
            "FiltersAndToolbarStacked", "FiltersAndToolbar_Stacked",
            new[] { "FiltersAndToolbarStacked" },
            "Filters and Toolbar - Stacked",
            "Form Part Section List: actions BELOW filters."),
        WorkspaceSection(
            "FiltersAndToolbarInline", "FiltersAndToolbar_Inline",
            new[] { "FiltersAndToolbarInline" },
            "Filters and Toolbar - Inline",
            "Form Part Section List: filters and actions on the SAME line."),
    };

    // ── Lookups ──────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, FormPatternSpec> PatternByKey = BuildPatternIndex();
    private static readonly Dictionary<string, SubPatternSpec> SubPatternByKey = BuildSubPatternIndex();

    private static Dictionary<string, FormPatternSpec> BuildPatternIndex()
    {
        var map = new Dictionary<string, FormPatternSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Patterns)
        {
            map[p.Id] = p;
            map[p.XmlName] = p;
            foreach (var alias in p.XmlAliases ?? Array.Empty<string>()) map[alias] = p;
        }
        return map;
    }

    private static Dictionary<string, SubPatternSpec> BuildSubPatternIndex()
    {
        var map = new Dictionary<string, SubPatternSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var sp in SubPatterns)
        {
            map[sp.Id] = sp;
            map[sp.XmlName] = sp;
            foreach (var alias in sp.XmlAliases ?? Array.Empty<string>()) map[alias] = sp;
        }
        return map;
    }

    /// <summary>
    /// Free-text aliases → pattern id. Absorbs the historical
    /// <c>FormPatternNormalizer</c> mappings so user/AI phrasing like
    /// "list", "master", "transaction" still resolves.
    /// </summary>
    private static readonly (Func<string, bool> Test, string Id)[] PatternAliases =
    {
        (s => s.Contains("simplelist") && s.Contains("detail"), "SimpleListDetails"),
        (s => s.Contains("simplelist"), "SimpleList"),
        (s => s.Contains("listpage"), "ListPage"),
        (s => s.Contains("detail") && s.Contains("master"), "DetailsMaster"),
        (s => s.Contains("detail") && s.Contains("transaction"), "DetailsTransaction"),
        (s => s.Contains("dropdialog"), "DropDialog"),
        (s => s.Contains("dialog"), "Dialog"),
        (s => s.Contains("tableofcontents") || s.Contains("toc") || s.Contains("parameter"), "TableOfContents"),
        (s => s.Contains("lookup"), "Lookup"),
        // Bare "FactBox" resolves to the grid variant — the common case; the
        // card variant stays an explicit ask ("FactBoxCard" hits the exact
        // alias map before this fallback runs).
        (s => s.Contains("factbox"), "FormPartFactboxGrid"),
        (s => s.Contains("operational"), "WorkspaceOperational"),
        (s => s.Contains("workspace") || s.Contains("panorama"), "Workspace"),
        (s => s.Contains("master"), "DetailsMaster"),
        (s => s.Contains("transaction"), "DetailsTransaction"),
        (s => s.Contains("list"), "SimpleList"),
    };

    /// <summary>
    /// Resolve a top-level form pattern by id, xmlName, or free-text alias.
    /// Exact (case-insensitive) matches win; alias matching is a fallback.
    /// </summary>
    public static FormPatternSpec? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (PatternByKey.TryGetValue(name.Trim(), out var exact)) return exact;

        var normalized = new string(name.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        if (PatternByKey.TryGetValue(normalized, out var byNormalized)) return byNormalized;

        foreach (var (test, id) in PatternAliases)
            if (test(normalized)) return PatternByKey.GetValueOrDefault(id);
        return null;
    }

    /// <summary>
    /// Strict resolution by id/xmlName only (case-insensitive) — used by the
    /// validator, where alias fuzziness would mask typos in &lt;Pattern&gt; values.
    /// </summary>
    public static FormPatternSpec? ResolveExact(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : PatternByKey.GetValueOrDefault(name.Trim());

    /// <summary>Resolve a sub-pattern by id or xmlName (case-insensitive, exact only).</summary>
    public static SubPatternSpec? ResolveSubPattern(string? name)
        => string.IsNullOrWhiteSpace(name) ? null : SubPatternByKey.GetValueOrDefault(name.Trim());

    /// <summary>
    /// Sub-patterns applicable to a container control type, optionally
    /// restricted to those valid under a given top-level pattern.
    /// </summary>
    public static IReadOnlyList<SubPatternSpec> SubPatternsFor(string controlType, string? parentPatternId = null)
        => SubPatterns.Where(sp =>
                sp.AppliesToControlTypes.Contains(controlType)
                && (sp.ParentPatterns is null || parentPatternId is null || sp.ParentPatterns.Contains(parentPatternId)))
            .ToArray();

    /// <summary>All known top-level pattern xmlNames (for command descriptions / errors).</summary>
    public static IReadOnlyList<string> KnownPatternNames()
        => Patterns.Select(p => p.XmlName).ToArray();
}
