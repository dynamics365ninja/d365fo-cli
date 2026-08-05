using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Agent;

public sealed class SchemaCommand : Command<SchemaCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--full")]
        [System.ComponentModel.Description("Emit every CLI command. Default emits the compact agent-first surface.")]
        public bool Full { get; init; }
    }

    private sealed record CommandSpec(
        string Command,
        string Description,
        string[] Args,
        string[] Options,
        string[] McpTool,
        bool Preferred = false);

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var commands = Commands();
        var selected = settings.Full ? commands : commands.Where(c => c.Preferred).ToArray();

        var payload = new
        {
            name = "d365fo",
            version = typeof(SchemaCommand).Assembly.GetName().Version?.ToString() ?? "0.1.0-dev",
            defaultOutput = "json when stdout/stderr are redirected; table in an interactive TTY",
            envelope = new
            {
                ok = "bool",
                data = "T",
                error = new { code = "string", message = "string", hint = "string?" },
            },
            guidance = new[]
            {
                "Prefer CLI commands over MCP when a shell is available; command text is cheaper than MCP tool schemas over multi-turn work.",
                "Use compact commands first: search any, search batch, get object, find related, read class/table/form.",
                "Use dedicated commands when you need a narrower command or a bridge-backed live read.",
                "Generated AOT XML is written to files; stdout returns a JSON summary to avoid loading large XML into the prompt.",
            },
            workflows = Workflows(),
            commands = selected,
            fullManifestHint = settings.Full ? null : "Run `d365fo schema --full` only when you need the complete command catalog.",
        };

        Console.Out.WriteLine(D365Json.Serialize(ToolResult<object>.Success(payload), indented: true));
        return 0;
    }

    private static object[] Workflows() =>
    [
        new
        {
            name = "discover object",
            commands = new[]
            {
                "d365fo search any <name> --output json",
                "d365fo get object <kind> <name> --output json",
                "d365fo find related name-search <name> --output json",
            },
        },
        new
        {
            name = "author extension",
            commands = new[]
            {
                "d365fo suggest extension <target> --output json",
                "d365fo find related extensions <target> --output json",
                "d365fo generate extension <kind> <target> --suffix <suffix> --out <path> --output json",
            },
        },
        new
        {
            name = "safe table/form scaffolding",
            commands = new[]
            {
                "d365fo search batch <new-name> <primary-table> --output json",
                "d365fo get object table <primary-table> --output json",
                "d365fo find form-patterns --table <primary-table> --output json",
                "d365fo generate form <name> --pattern <pattern> --table <primary-table> --out <path> --output json",
            },
        },
        new
        {
            name = "security trace",
            commands = new[]
            {
                "d365fo security coverage <menu-item> --type Menuitem --output json",
                "d365fo security role <role> --output json",
                "d365fo security duty <duty> --output json",
                "d365fo security privilege <privilege> --output json",
            },
        },
    ];

    // The `mcpTool` field names the unified MCP tool (and discriminator) the
    // CLI command maps to, so an agent reading the manifest can translate
    // between the shell surface and the MCP surface. CLI-only commands carry [].
    private static CommandSpec[] Commands() =>
    [
        C("search any", "Scope-agnostic search across every indexed kind.", ["<QUERY>"], ["--limit", "--output"], ["search (type=any)"], true),
        C("search batch", "Run several scope-agnostic searches in one process.", ["<QUERY>..."], ["--limit", "--output"], ["search (queries[])"], true),
        C("get object", "Generic get by kind/name across object types.", ["<KIND>", "<NAME>"], ["--output", "--resolve-labels"], ["get_object_info"], true),
        C("get batch", "Fetch up to 10 objects (<kind>:<name> specs) in one call.", ["<SPEC>..."], ["--output"], ["batch_get_info"], true),
        C("prepare change", "Single-round change context + grounding token.", ["<OBJECT>"], ["--method", "--goal", "--output"], ["prepare (mode=change)"], true),
        C("prepare create", "Single-round new-object context + grounding token.", ["<NAME>"], ["--type", "--field", "--goal", "--output"], ["prepare (mode=create)"], true),
        C("validate xpp", "Offline X++/XML best-practice validator.", ["[FILE]"], ["--code-type", "--context", "--output"], [], true),
        C("validate references", "Verify every identifier in X++ code against the index.", ["[FILE]"], ["--output"], [], true),
        C("form-pattern validate", "Structural form-pattern validator (FP001-FP010) over AxForm XML.", ["[FILE]"], ["--output"], ["object_patterns (domain=form, action=validate)"], true),
        C("form-pattern spec", "Form pattern spec catalog (structure tree, versions, when-to-use).", ["[NAME]"], ["--output"], ["object_patterns (domain=form, action=spec)"], true),
        C("form-pattern analyze", "Analyse indexed forms by pattern / primary table / similarity.", [], ["--pattern", "--table", "--similar-to", "--output"], ["object_patterns (domain=form, action=analyze)"], true),
        C("find related", "Generic relation lookup by relation/name.", ["<RELATION>", "<NAME>"], ["--kind", "--method", "--limit", "--output"], ["search (type=any)"], true),
        C("read class", "Read X++ source embedded in an AxClass.", ["<NAME>"], ["--method", "--declaration", "--lines", "--around", "--output"], ["get_method (objectType=class)"], true),
        C("read table", "Read X++ source embedded in an AxTable.", ["<NAME>"], ["--method", "--declaration", "--lines", "--around", "--output"], ["get_method (objectType=table)"], true),
        C("read form", "Read X++ source embedded in an AxForm.", ["<NAME>"], ["--method", "--declaration", "--lines", "--around", "--output"], ["get_method (objectType=form)"], true),
        C("suggest edt", "Suggest EDTs for a field name.", ["<FIELDNAME>"], ["--limit", "--output"], ["suggest_edt"], true),
        C("suggest extension", "Recommend CoC/event-handler/AOT-extension strategy.", ["<TARGET>"], ["--kind", "--output"], ["extension_info (mode=strategy)"], true),
        C("validate name", "Check object name against naming conventions.", ["<KIND>", "<NAME>"], ["--prefix", "--output"], ["validate_object_naming"], true),
        C("stats", "Aggregate counters over the index.", [], ["--top", "--output"], ["stats"], true),
        C("lint", "In-process best-practice gate over the index.", [], ["--category", "--all-models", "--format", "--output"], ["lint"], true),
        C("schema", "Emit this JSON command manifest.", [], ["--full"], [], true),
        C("agent-prompt", "Emit the CLI-first LLM system prompt.", [], ["--out"], [], true),

        // Unified parity branches (mirror the consolidated MCP tools).
        C("security role", "Security role: duties + privileges.", ["<NAME>"], ["--output"], ["security_info (mode=artifact,type=role)"], true),
        C("security duty", "Security duty: privileges.", ["<NAME>"], ["--output"], ["security_info (mode=artifact,type=duty)"], true),
        C("security privilege", "Security privilege: entry points.", ["<NAME>"], ["--output"], ["security_info (mode=artifact,type=privilege)"], true),
        C("security coverage", "Role→Duty→Privilege routes that grant access to an object.", ["<OBJECT>"], ["--type", "--output"], ["security_info (mode=coverage)"], true),
        C("labels search", "Search label keys/values; FTS5 is preferred automatically.", ["<QUERY>"], ["--lang", "--limit", "--fts", "--raw-text", "--output"], ["labels (action=search/fts)"], true),
        C("labels resolve", "Resolve a @SYS12345-style label token across languages.", ["<TOKEN>"], ["--lang", "--raw-text", "--output"], ["labels (action=resolve)"], true),
        C("labels info", "Fetch one label entry by (file, language, key).", ["<FILE_OR_KEY>", "[KEY]"], ["--lang", "--raw-text", "--output"], ["labels (action=info)"], true),
        C("labels create", "Create or update a label entry.", ["<KEY>", "<VALUE>"], ["--file", "--overwrite", "--output"], ["labels (action=create)"], true),
        C("labels rename", "Rename a label key.", ["<OLD>", "<NEW>"], ["--file", "--overwrite", "--output"], ["labels (action=rename)"], true),
        C("labels delete", "Delete a label key.", ["<KEY>"], ["--file", "--output"], ["labels (action=delete)"], true),
        C("undo", "Revert the last N modification-journal entries (create removed, update/delete restored to their exact pre-image).", [], ["--steps", "--dry-run", "--db", "--output"], ["undo_last_modification"], true),
        C("journal list", "Inspect the modification-journal stack, most-recent-first.", [], ["--limit", "--db", "--output"], ["journal_list"], true),

        // Typed search/get commands (kind-specific) — all fold into the unified
        // `search` / `get_object_info` MCP tools via a `type`/`objectType` field.
        C("search class", "Find X++ classes by substring.", ["<QUERY>"], ["--model", "--limit", "--output"], ["search (type=class)"]),
        C("search table", "Find tables by substring.", ["<QUERY>"], ["--model", "--limit", "--output"], ["search (type=table)"]),
        C("search edt", "Find Extended Data Types by substring.", ["<QUERY>"], ["--limit", "--output"], ["search (type=edt)"]),
        C("search enum", "Find base enums by substring.", ["<QUERY>"], ["--limit", "--output"], ["search (type=enum)"]),
        C("search label", "Search label keys/values; FTS5 is preferred automatically.", ["<QUERY>"], ["--lang", "--limit", "--fts", "--raw-text", "--output"], ["labels (action=search)"]),
        C("search query", "Find AOT queries.", ["<QUERY>"], ["--limit", "--output"], ["search (type=query)"]),
        C("search view", "Find AOT views.", ["<QUERY>"], ["--limit", "--output"], ["search (type=view)"]),
        C("search entity", "Find data entities by AOT/OData name.", ["<QUERY>"], ["--limit", "--output"], ["search (type=entity)"]),
        C("search report", "Find SSRS/RDL reports.", ["<QUERY>"], ["--limit", "--output"], ["search (type=report)"]),
        C("search service", "Find SOAP services.", ["<QUERY>"], ["--limit", "--output"], ["search (type=service)"]),
        C("search workflow", "Find workflow types.", ["<QUERY>"], ["--limit", "--output"], ["search (type=workflow)"]),

        C("get table", "Get table fields, relations, indexes, methods, and delete actions.", ["<NAME>"], ["--include", "--output", "--resolve-labels"], ["get_object_info (objectType=table)"]),
        C("get edt", "Get an EDT definition.", ["<NAME>"], ["--output"], ["get_object_info (objectType=edt)"]),
        C("get class", "Get class metadata and method signatures.", ["<NAME>"], ["--output"], ["get_object_info (objectType=class)"]),
        C("get enum", "Get enum values.", ["<NAME>"], ["--output"], ["get_object_info (objectType=enum)"]),
        C("get menu-item", "Resolve menu item to launched object.", ["<NAME>"], ["--output"], ["get_object_info (objectType=menu-item)"]),
        C("get security", "Get role/duty/privilege coverage for an object.", ["<OBJECT>"], ["--type", "--output"], ["security_info (mode=coverage)"]),
        C("get label", "Resolve a label entry or token.", ["<FILE_OR_KEY>", "[KEY]"], ["--lang", "--raw-text", "--output"], ["labels (action=info)"]),
        C("get form", "Get form data sources and metadata.", ["<NAME>"], ["--output"], ["get_object_info (objectType=form)"]),
        C("get role", "Get security role duties/privileges.", ["<NAME>"], ["--output"], ["security_info (mode=artifact,type=role)"]),
        C("get duty", "Get security duty privileges.", ["<NAME>"], ["--output"], ["security_info (mode=artifact,type=duty)"]),
        C("get privilege", "Get security privilege entry points.", ["<NAME>"], ["--output"], ["security_info (mode=artifact,type=privilege)"]),
        C("get query", "Get query metadata and joins.", ["<NAME>"], ["--output"], ["get_object_info (objectType=query)"]),
        C("get view", "Get view fields and source query.", ["<NAME>"], ["--output"], ["get_object_info (objectType=view)"]),
        C("get entity", "Get data entity metadata and OData names.", ["<NAME>"], ["--output"], ["get_object_info (objectType=entity)"]),
        C("get report", "Get report datasets.", ["<NAME>"], ["--output"], ["get_object_info (objectType=report)"]),
        C("get service", "Get service operations.", ["<NAME>"], ["--output"], ["get_object_info (objectType=service)"]),
        C("get service-group", "Get service group members.", ["<NAME>"], ["--output"], ["get_object_info (objectType=service-group)"]),

        C("find coc", "Find Chain-of-Command extensions.", ["<TARGET>"], ["--output"], ["extension_info (mode=coc)"]),
        C("find relations", "Find table relations.", ["<TABLE>"], ["--output"], ["get_object_info (objectType=table,relations)"]),
        C("find usages", "Find indexed entities whose names contain a substring.", ["<SYMBOL>"], ["--limit", "--output"], ["search (type=any)"]),
        C("find fields", "Find tables that declare a field name or EDT (exact match) — precise field-level lookup, not relation/FK or source-code search.", ["<NAME>"], ["--model", "--limit", "--output"], ["find_tables_by_field"]),
        C("find extensions", "Find Table/Form/Edt/Enum extensions targeting an object.", ["<TARGET>"], ["--kind", "--output"], ["extension_info (mode=points)", "extension_info (mode=table-merge)"]),
        C("find handlers", "Find event subscribers.", ["<OBJECT>"], ["--kind", "--output"], ["extension_info (mode=events)"]),
        C("find event-handlers", "Alias of `find handlers`.", ["<OBJECT>"], ["--kind", "--output"], ["extension_info (mode=events)"]),
        C("find refs", "Scan indexed X++ source for reverse references.", ["<NAME>"], ["--kind", "--model", "--limit", "--xref", "--output"], ["find_references"]),
        C("find references", "Alias of `find refs`.", ["<NAME>"], ["--kind", "--model", "--limit", "--xref", "--output"], ["find_references"]),
        C("find form-patterns", "Find forms by Microsoft form pattern, table, or peer form.", [], ["--pattern", "--table", "--similar-to", "--output"], ["object_patterns (domain=form, action=analyze)"]),

        C("index build", "Create or ensure the metadata index schema.", [], ["--db", "--output"], ["index_status"]),
        C("index status", "Report index table counts and config.", [], ["--output"], ["index_status"]),
        C("index extract", "Walk PackagesLocalDirectory and ingest AOT metadata.", [], ["--packages", "--db", "--model", "--since", "--output"], []),
        C("index refresh", "Incremental extract using model fingerprints.", [], ["--packages", "--db", "--model", "--since", "--force", "--output"], []),
        C("index history", "Show recent extraction telemetry.", [], ["--db", "--model", "--limit", "--output"], ["index_history"]),
        C("models list", "List indexed models.", [], ["--output"], ["models (action=list)"]),
        C("models deps", "Show dependencies for a model.", ["<NAME>"], ["--output"], ["models (action=deps)"]),
        C("models coupling", "Show fan-in/fan-out/instability/cycles.", [], ["--top", "--only-cycles", "--output"], ["models (action=coupling)"]),

        C("generate table", "Scaffold AxTable XML.", ["<NAME>"], ["--out", "--overwrite", "--install-to", "--label", "--field", "--pattern", "--table-type", "--primary-key", "--output"], ["generate_object (objectType=table)"]),
        C("generate class", "Scaffold AxClass XML.", ["<NAME>"], ["--out", "--overwrite", "--install-to", "--extends", "--non-final", "--output"], ["generate_object (objectType=class)"]),
        C("generate coc", "Scaffold a Chain-of-Command class.", ["<TARGET>"], ["--out", "--overwrite", "--install-to", "--method", "--output"], ["generate_object (objectType=coc)"]),
        C("generate form", "Scaffold AxForm XML for the supported form patterns.", ["<FORM_NAME>"], ["--out", "--overwrite", "--install-to", "--pattern", "--table", "--caption", "--field", "--section", "--lines-table", "--output"], ["generate_object (objectType=form)"]),
        C("generate datasource-method", "Add/override a method on a form datasource (mutates the AxForm). Omit --method to list overridable methods.", ["<FORM>"], ["--datasource", "--method", "--return-type", "--body", "--list", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate control-method", "Add/override a method on a form control (mutates the AxForm). Omit --method to list overridable methods.", ["<FORM>"], ["--control", "--method", "--return-type", "--body", "--list", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate simple-list", "Deprecated alias for generate form --pattern SimpleList.", ["<FORM_NAME>"], ["--out", "--table", "--overwrite", "--output"], ["generate_object (objectType=form)"]),
        C("generate edt", "Scaffold AxEdt XML.", ["<NAME>"], ["--extends", "--label", "--size", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=edt)"]),
        C("generate enum", "Scaffold AxEnum XML.", ["<NAME>"], ["--label", "--value", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=enum)"]),
        C("generate query", "Scaffold AxQuery XML.", ["<NAME>"], ["--root-table", "--label", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=query)"]),
        C("generate sysoperation", "Scaffold SysOperation contract/service/controller.", ["<NAME>"], ["--execution-mode", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=sysoperation)"]),
        C("generate business-event", "Scaffold a business event class + contract.", ["<NAME>"], ["--contract", "--category", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=business-event)"]),
        C("generate runbase", "Scaffold a RunBase/RunBaseBatch class.", ["<NAME>"], ["--batch", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=runbase)"]),
        C("generate security-policy", "Scaffold an AxSecurityPolicy (XDS) XML.", ["<NAME>"], ["--constrained-table", "--policy-query", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=security-policy)"]),
        C("generate entity", "Scaffold AxDataEntityView XML.", ["<ENTITY>"], ["--out", "--overwrite", "--install-to", "--table", "--public-entity", "--public-collection", "--field", "--all-fields", "--output"], []),
        C("generate extension", "Scaffold Table/Form/Edt/Enum extension XML.", ["<KIND>", "<TARGET>"], ["--suffix", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate event-handler", "Scaffold an event subscriber class.", ["<CLASS_NAME>"], ["--source-kind", "--source-object", "--event", "--method", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate privilege", "Scaffold a security privilege.", ["<NAME>"], ["--entry-point", "--entry-kind", "--entry-object", "--access", "--label", "--into-role", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate duty", "Scaffold a security duty.", ["<NAME>"], ["--privilege", "--label", "--into-role", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate role", "Scaffold or merge a security role.", ["<NAME>"], ["--duty", "--privilege", "--label", "--description", "--add-to", "--out", "--overwrite", "--install-to", "--output"], []),
        C("generate report", "Scaffold AxReport and RDP skeleton.", ["<NAME>"], ["--dp", "--tmp", "--dataset", "--caption", "--field", "--parameter", "--extra-dataset", "--out-dp", "--out-contract", "--out", "--overwrite", "--install-to", "--output"], []),

        C("delete", "Delete an AOT object (bridge or on-disk), journaled for `undo`.", [], ["--kind", "--name", "--install-to", "--path", "--model", "--output"], []),

        C("analyze completeness", "Cross-check workspace AOT XML against the index.", [], ["--workspace", "--output"], []),
        C("analyze integration", "Cross-check data entities for OData/DMF readiness.", [], ["--model", "--output"], ["analyze (mode=integration)"]),
        C("analyze impact", "List downstream consumers of an AOT object.", ["<OBJECT>"], ["--output"], ["analyze (mode=impact)"]),
        C("report-integrations", "Aggregated integration surface report.", [], ["--model", "--output"], ["analyze (mode=report)"]),
        C("review diff", "Inspect AOT changes vs. a git revision.", [], ["--base", "--head", "--repo", "--output"], []),
        C("build", "Invoke MSBuild on a D365FO project.", [], ["--msbuild", "--project", "--config", "--output"], []),
        C("sync", "Run DB sync.", [], ["--tool", "--full", "--output"], []),
        C("test run", "Invoke SysTestRunner.", [], ["--runner", "--suite", "--output"], []),
        C("bp check", "Invoke xppbp best-practice checks.", [], ["--tool", "--model", "--packages", "--metadata", "--output"], []),
        C("daemon start", "Start warm JSON-RPC daemon.", [], ["--db", "--packages", "--foreground", "--no-watch", "--watch-debounce"], []),
        C("daemon stop", "Stop daemon.", [], [], []),
        C("daemon status", "Report daemon status.", [], [], []),
        C("doctor", "Diagnose environment.", [], ["--output"], []),
        C("init", "Quickstart index/profile setup.", [], ["--packages", "--extra-packages", "--db", "--run-extract", "--dry-run", "--persist-profile"], []),
        C("version", "Print version information.", [], ["--output"], []),
    ];

    private static CommandSpec C(
        string command,
        string description,
        string[] args,
        string[] options,
        string[] replacesMcp,
        bool preferred = false)
        => new(command, description, args, options, replacesMcp, preferred);
}
