namespace D365FO.Core.ObjectTypes;

/// <summary>
/// One <c>d365fo generate &lt;name&gt;</c> subcommand and the AOT root elements it
/// writes — including the secondary artifacts a single invocation emits
/// alongside the headline one (<c>report</c> also writes a TempDB table, three
/// classes and an output menu item).
/// </summary>
public sealed record GenerateCapability(
    string Name,
    string Summary,
    IReadOnlyList<string> Roots,
    bool Deprecated = false);

/// <summary>
/// The <c>generate</c> branch as data: what an agent can ask this tool to build.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ObjectTypeRegistry"/> answers "what is this AOT type" and already
/// names the subcommand that emits each type. It cannot answer "what can the
/// tool build", because eleven subcommands emit X++ classes (<c>coc</c>,
/// <c>sysoperation</c>, <c>runbase</c>, <c>systest</c>, <c>business-event</c>,
/// <c>event-handler</c>, <c>migration-script</c>, …) and so map onto the single
/// registry kind <c>class</c>. The K∧E∧T coverage report needs the second
/// question answered per capability, which is what this table is for.
/// </para>
/// <para>
/// Two gates keep it from becoming the fifth drifting registry the audit
/// complained about (finding G2): <c>GenerateSurfaceTests</c> asserts that the
/// names here are exactly the subcommands <c>CliApp</c> registers (read off the
/// real CLI's own help output, in a child process), and that every registry kind
/// naming a <c>GenerateCommand</c> is listed under that capability's
/// <see cref="GenerateCapability.Roots"/>.
/// </para>
/// </remarks>
public static class GenerateSurface
{
    private static readonly GenerateCapability[] AllCapabilities =
    [
        new("table", "AxTable from a business-role pattern preset", [Root.Table]),
        new("class", "AxClass skeleton", [Root.Class]),
        new("coc", "Chain-of-Command wrapper class", [Root.Class]),
        new("form", "AxForm in one of nine patterns", [Root.Form]),
        new("datasource-method", "Method override on a form datasource", [Root.Form]),
        new("control-method", "Method override on a form control", [Root.Form]),
        new("form-clone", "Copy of an existing AxForm under a new name, datasources optionally re-bound", [Root.Form]),
        new("simple-list", "Alias for `form --pattern SimpleList`", [Root.Form], Deprecated: true),
        new("entity", "AxDataEntityView over a table", [Root.DataEntityView]),
        new("extension", "Table/Form/Edt/Enum/View/Query/Entity/Duty/Role extension",
        [
            Root.TableExtension, Root.FormExtension, Root.EdtExtension, Root.EnumExtension,
            Root.ViewExtension, Root.QuerySimpleExtension, Root.DataEntityViewExtension,
            Root.SecurityDutyExtension, Root.SecurityRoleExtension,
        ]),
        new("event-handler", "Event subscriber class", [Root.Class]),
        new("privilege", "Security privilege over an entry point", [Root.SecurityPrivilege]),
        new("duty", "Security duty grouping privileges", [Root.SecurityDuty]),
        new("role", "Security role", [Root.SecurityRole]),
        new("report", "SSRS auto-design report stack: report, TempDB table, DP/contract/controller, output menu item",
            [Root.Report, Root.Table, Root.Class, Root.MenuItemOutput]),
        new("sysoperation", "SysOperation contract + service + controller", [Root.Class]),
        new("number-sequence", "NumberSeq module extension class + EDT", [Root.Class, Root.Edt]),
        new("workflow", "Workflow template, its approval/task elements, and the document class",
            [Root.WorkflowTemplate, Root.WorkflowApproval, Root.WorkflowTask, Root.Class]),
        new("menu-item", "Display / Action / Output menu item",
            [Root.MenuItemDisplay, Root.MenuItemAction, Root.MenuItemOutput]),
        new("edt", "Extended data type", [Root.Edt]),
        new("enum", "Base enumeration", [Root.Enum]),
        new("query", "AxQuery with data sources and joins", [Root.Query]),
        new("view", "AxView over a query, with computed columns", [Root.View]),
        new("map", "AxMap field template mapped onto tables", [Root.Map]),
        new("business-event", "Business event class + contract", [Root.Class]),
        new("custom-service", "Service class, AxService and service group", [Root.Service, Root.ServiceGroup, Root.Class]),
        new("migration-script", "Data-migration runnable class", [Root.Class]),
        new("runbase", "RunBase/RunBaseBatch class with dialog and pack/unpack", [Root.Class]),
        new("security-policy", "AxSecurityPolicy (XDS)", [Root.SecurityPolicy]),
        new("systest", "ATL-ready SysTestCase class", [Root.Class]),
    ];

    /// <summary>Every generate subcommand, in the order <c>CliApp</c> registers them.</summary>
    public static IReadOnlyList<GenerateCapability> All => AllCapabilities;

    public static GenerateCapability? Find(string name) =>
        AllCapabilities.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Root elements as constants, so a typo here is a build error rather than an empty coverage row.</summary>
    private static class Root
    {
        public const string Table = ObjectTypeRegistry.Folders.Table;
        public const string Class = ObjectTypeRegistry.Folders.Class;
        public const string Edt = ObjectTypeRegistry.Folders.Edt;
        public const string Enum = ObjectTypeRegistry.Folders.Enum;
        public const string Form = ObjectTypeRegistry.Folders.Form;
        public const string View = ObjectTypeRegistry.Folders.View;
        public const string Map = ObjectTypeRegistry.Folders.Map;
        public const string Query = ObjectTypeRegistry.Folders.Query;
        public const string DataEntityView = ObjectTypeRegistry.Folders.DataEntityView;

        public const string TableExtension = ObjectTypeRegistry.Folders.TableExtension;
        public const string FormExtension = ObjectTypeRegistry.Folders.FormExtension;
        public const string EdtExtension = ObjectTypeRegistry.Folders.EdtExtension;
        public const string EnumExtension = ObjectTypeRegistry.Folders.EnumExtension;
        public const string SecurityDutyExtension = ObjectTypeRegistry.Folders.SecurityDutyExtension;
        public const string SecurityRoleExtension = ObjectTypeRegistry.Folders.SecurityRoleExtension;

        // No Folders constant: nothing writes these through GenerateInstaller yet,
        // they are reachable only through `generate extension`.
        public const string ViewExtension = "AxViewExtension";
        public const string QuerySimpleExtension = "AxQuerySimpleExtension";
        public const string DataEntityViewExtension = "AxDataEntityViewExtension";

        public const string MenuItemDisplay = ObjectTypeRegistry.Folders.MenuItemDisplay;
        public const string MenuItemAction = ObjectTypeRegistry.Folders.MenuItemAction;
        public const string MenuItemOutput = ObjectTypeRegistry.Folders.MenuItemOutput;

        public const string Report = ObjectTypeRegistry.Folders.Report;
        public const string Service = ObjectTypeRegistry.Folders.Service;
        public const string ServiceGroup = ObjectTypeRegistry.Folders.ServiceGroup;

        public const string SecurityRole = ObjectTypeRegistry.Folders.SecurityRole;
        public const string SecurityDuty = ObjectTypeRegistry.Folders.SecurityDuty;
        public const string SecurityPrivilege = ObjectTypeRegistry.Folders.SecurityPrivilege;
        public const string SecurityPolicy = ObjectTypeRegistry.Folders.SecurityPolicy;

        public const string WorkflowTemplate = ObjectTypeRegistry.Folders.WorkflowTemplate;
        public const string WorkflowApproval = ObjectTypeRegistry.Folders.WorkflowApproval;
        public const string WorkflowTask = ObjectTypeRegistry.Folders.WorkflowTask;
    }
}
