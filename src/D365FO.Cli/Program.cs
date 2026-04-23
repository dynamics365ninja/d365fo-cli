using D365FO.Cli.Commands.Agent;
using D365FO.Cli.Commands.Daemon;
using D365FO.Cli.Commands.Find;
using D365FO.Cli.Commands.Generate;
using D365FO.Cli.Commands.Get;
using D365FO.Cli.Commands.Index;
using D365FO.Cli.Commands.Models;
using D365FO.Cli.Commands.Ops;
using D365FO.Cli.Commands.Read;
using D365FO.Cli.Commands.Resolve;
using D365FO.Cli.Commands.Review;
using D365FO.Cli.Commands.Search;
using Spectre.Console.Cli;

var app = new CommandApp();
app.Configure(cfg =>
{
    cfg.SetApplicationName("d365fo");
    cfg.SetApplicationVersion("0.1.0-dev");
    cfg.CaseSensitivity(CaseSensitivity.None);
    cfg.PropagateExceptions();

    cfg.AddBranch("search", b =>
    {
        b.SetDescription("Search the D365FO metadata index.");
        b.AddCommand<SearchClassCommand>("class").WithDescription("Find X++ classes by substring.");
        b.AddCommand<SearchTableCommand>("table").WithDescription("Find tables by substring.");
        b.AddCommand<SearchEdtCommand>("edt").WithDescription("Find Extended Data Types.");
        b.AddCommand<SearchEnumCommand>("enum").WithDescription("Find base enums.");
        b.AddCommand<SearchLabelCommand>("label").WithDescription("Search label file entries.");
        b.AddCommand<SearchQueryCommand>("query").WithDescription("Find AOT queries.");
        b.AddCommand<SearchViewCommand>("view").WithDescription("Find AOT views.");
        b.AddCommand<SearchEntityCommand>("entity").WithDescription("Find data entities (by name or OData entity/collection).");
        b.AddCommand<SearchReportCommand>("report").WithDescription("Find SSRS / RDL reports.");
        b.AddCommand<SearchServiceCommand>("service").WithDescription("Find SOAP services.");
        b.AddCommand<SearchWorkflowCommand>("workflow").WithDescription("Find workflow types.");
    });

    cfg.AddBranch("get", b =>
    {
        b.SetDescription("Fetch full metadata for a named object.");
        b.AddCommand<GetTableCommand>("table").WithDescription("Table shape: fields + relations.");
        b.AddCommand<GetEdtCommand>("edt").WithDescription("Extended Data Type definition.");
        b.AddCommand<GetClassCommand>("class").WithDescription("Class methods and signatures.");
        b.AddCommand<GetEnumCommand>("enum").WithDescription("Enum values.");
        b.AddCommand<GetMenuItemCommand>("menu-item").WithDescription("Menu item -> object mapping.");
        b.AddCommand<GetSecurityCommand>("security").WithDescription("Role/Duty/Privilege coverage.");
        b.AddCommand<GetLabelCommand>("label").WithDescription("Resolve a single label entry.");
        b.AddCommand<GetFormCommand>("form").WithDescription("Form metadata: datasources.");
        b.AddCommand<GetRoleCommand>("role").WithDescription("Security role: duties + privileges.");
        b.AddCommand<GetDutyCommand>("duty").WithDescription("Security duty: privileges.");
        b.AddCommand<GetPrivilegeCommand>("privilege").WithDescription("Security privilege: entry points.");
        b.AddCommand<GetQueryCommand>("query").WithDescription("AOT query: datasources + joins.");
        b.AddCommand<GetViewCommand>("view").WithDescription("AOT view: fields mapped to datasource.field.");
        b.AddCommand<GetEntityCommand>("entity").WithDescription("Data entity: fields + OData names.");
        b.AddCommand<GetReportCommand>("report").WithDescription("Report: datasets + queries/RDP.");
        b.AddCommand<GetServiceCommand>("service").WithDescription("SOAP service: operations.");
        b.AddCommand<GetServiceGroupCommand>("service-group").WithDescription("Service group: members.");
    });

    cfg.AddBranch("find", b =>
    {
        b.SetDescription("Discover cross-references.");
        b.AddCommand<FindCocCommand>("coc").WithDescription("Find Chain-of-Command extensions.");
        b.AddCommand<FindRelationsCommand>("relations").WithDescription("Find table relations.");
        b.AddCommand<FindUsagesCommand>("usages").WithDescription("Find index entities whose name contains a substring.");
        b.AddCommand<FindExtensionsCommand>("extensions").WithDescription("Find Table/Form/Edt/Enum extensions targeting an object.");
        b.AddCommand<FindHandlersCommand>("handlers").WithDescription("Find event handlers subscribed to a form/table/delegate.");
    });

    cfg.AddBranch("resolve", b =>
    {
        b.SetDescription("Resolve tokens (labels etc.) to their concrete values.");
        b.AddCommand<ResolveLabelCommand>("label").WithDescription("Resolve @SYS12345-style label token to its text.");
    });

    cfg.AddBranch("read", b =>
    {
        b.SetDescription("Read X++ source embedded in AOT XML.");
        b.AddCommand<ReadClassCommand>("class").WithDescription("Read source of an AxClass (optionally a single method).");
        b.AddCommand<ReadTableCommand>("table").WithDescription("Read source of an AxTable's methods.");
        b.AddCommand<ReadFormCommand>("form").WithDescription("Read source of an AxForm's methods.");
    });

    cfg.AddBranch("index", b =>
    {
        b.SetDescription("Manage the local SQLite metadata index.");
        b.AddCommand<IndexBuildCommand>("build").WithDescription("Create/ensure index database.");
        b.AddCommand<IndexStatusCommand>("status").WithDescription("Report index health.");
        b.AddCommand<IndexExtractCommand>("extract").WithDescription("Walk PACKAGES_PATH and ingest AOT metadata.");
    });

    cfg.AddBranch("models", b =>
    {
        b.SetDescription("Inspect indexed models and their descriptor-declared dependencies.");
        b.AddCommand<ModelsListCommand>("list").WithDescription("List indexed models (name/publisher/layer/custom).");
        b.AddCommand<ModelsDepsCommand>("deps").WithDescription("Show dependency graph for a model (depends-on / depended-by).");
    });

    cfg.AddBranch("generate", b =>
    {
        b.SetDescription("Scaffold AOT XML skeletons.");
        b.AddCommand<GenerateTableCommand>("table").WithDescription("Create a new AxTable.");
        b.AddCommand<GenerateClassCommand>("class").WithDescription("Create a new AxClass.");
        b.AddCommand<GenerateCocCommand>("coc").WithDescription("Create a Chain-of-Command extension class.");
        b.AddCommand<GenerateSimpleListCommand>("simple-list").WithDescription("Create a SimpleList-pattern AxForm.");
    });

    cfg.AddBranch("test", b =>
    {
        b.SetDescription("Run D365FO developer tests (Windows VM).");
        b.AddCommand<TestRunCommand>("run").WithDescription("Invoke SysTestRunner.");
    });

    cfg.AddBranch("bp", b =>
    {
        b.SetDescription("Best-practice checks (Windows VM).");
        b.AddCommand<BpCheckCommand>("check").WithDescription("Invoke xppbp.");
    });

    cfg.AddBranch("review", b =>
    {
        b.SetDescription("Review utilities (Git-backed).");
        b.AddCommand<ReviewDiffCommand>("diff").WithDescription("Inspect AOT changes vs. a git revision.");
    });

    cfg.AddBranch("daemon", b =>
    {
        b.SetDescription("Long-running JSON-RPC IPC server (named pipe / unix socket).");
        b.AddCommand<DaemonStartCommand>("start").WithDescription("Start the daemon (foreground).");
        b.AddCommand<DaemonStopCommand>("stop").WithDescription("Stop the running daemon.");
        b.AddCommand<DaemonStatusCommand>("status").WithDescription("Report daemon status.");
    });

    cfg.AddCommand<BuildCommand>("build").WithDescription("Invoke MSBuild (Windows VM).");
    cfg.AddCommand<SyncCommand>("sync").WithDescription("Run DB sync (Windows VM).");
    cfg.AddCommand<DoctorCommand>("doctor").WithDescription("Diagnose environment.");
    cfg.AddCommand<VersionCommand>("version").WithDescription("Print version information.");
    cfg.AddCommand<AgentPromptCommand>("agent-prompt").WithDescription("Emit LLM system prompt for this CLI.");
    cfg.AddCommand<SchemaCommand>("schema").WithDescription("Emit JSON command manifest.");
});

try
{
    return await app.RunAsync(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine(D365FO.Core.D365Json.Serialize(
        D365FO.Core.ToolResult<object>.Fail("UNHANDLED", ex.Message, ex.GetType().FullName)));
    return 2;
}

