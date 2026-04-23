using D365FO.Cli.Commands.Agent;
using D365FO.Cli.Commands.Find;
using D365FO.Cli.Commands.Get;
using D365FO.Cli.Commands.Index;
using D365FO.Cli.Commands.Ops;
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
        b.AddCommand<SearchLabelCommand>("label").WithDescription("Search label file entries.");
    });

    cfg.AddBranch("get", b =>
    {
        b.SetDescription("Fetch full metadata for a named object.");
        b.AddCommand<GetTableCommand>("table").WithDescription("Table shape: fields + relations.");
        b.AddCommand<GetEdtCommand>("edt").WithDescription("Extended Data Type definition.");
        b.AddCommand<GetClassCommand>("class").WithDescription("Class methods and signatures.");
        b.AddCommand<GetMenuItemCommand>("menu-item").WithDescription("Menu item -> object mapping.");
        b.AddCommand<GetSecurityCommand>("security").WithDescription("Role/Duty/Privilege coverage.");
    });

    cfg.AddBranch("find", b =>
    {
        b.SetDescription("Discover cross-references.");
        b.AddCommand<FindCocCommand>("coc").WithDescription("Find Chain-of-Command extensions.");
        b.AddCommand<FindRelationsCommand>("relations").WithDescription("Find table relations.");
    });

    cfg.AddBranch("index", b =>
    {
        b.SetDescription("Manage the local SQLite metadata index.");
        b.AddCommand<IndexBuildCommand>("build").WithDescription("Create/ensure index database.");
        b.AddCommand<IndexStatusCommand>("status").WithDescription("Report index health.");
    });

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

