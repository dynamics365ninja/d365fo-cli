﻿using D365FO.Core;
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

    /// <summary>
    /// One entry of the published manifest. Internal rather than private because
    /// <c>CliMcpParityTests</c> reads the table directly: the manifest is the only
    /// declared map between the two surfaces, and a map nothing checks is how the
    /// CLI came to register commands the manifest never mentioned and to claim MCP
    /// routes that no tool dispatched.
    /// </summary>
    internal sealed record CommandSpec(
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
    internal static CommandSpec[] Commands() =>
    [
        C("search any", "Scope-agnostic search across every indexed kind.", ["<QUERY>"], ["--limit", "--output"], ["search (type=any)"], true),
        C("search batch", "Run several scope-agnostic searches in one process.", ["<QUERY>..."], ["--limit", "--output"], ["search (queries[])"], true),
        C("get object", "Generic get by kind/name across object types.", ["<KIND>", "<NAME>"], ["--output", "--resolve-labels"], ["get_object_info"], true),
        C("get batch", "Fetch up to 10 objects (<kind>:<name> specs) in one call.", ["<SPEC>..."], ["--output"], ["batch_get_info"], true),
        C("prepare change", "Single-round change context + grounding token.", ["<OBJECT>"], ["--method", "--goal", "--output"], ["prepare (mode=change)"], true),
        C("prepare create", "Single-round new-object context + grounding token.", ["<NAME>"], ["--type", "--field", "--goal", "--output"], ["prepare (mode=create)"], true),
        C("prepare test", "Single-round SysTest context: methods worth covering, existing coverage, TestEssentials check, scaffold call, red-first cycle + grounding token.", ["<CLASS>"], ["--method", "--goal", "--model", "--output"], ["prepare (mode=test)"], true),
        C("validate xpp", "Offline X++/XML best-practice validator.", ["[FILE]"], ["--code-type", "--context", "--output"], ["validate (mode=xpp)"], true),
        C("validate references", "Verify every identifier in X++ code against the index.", ["[FILE]"], ["--output"], ["validate (mode=references)"], true),
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
        C("find extensions", "Find Table/Form/Edt/Enum extensions targeting an object; --merged adds the effective merged table schema.", ["<TARGET>"], ["--kind", "--merged", "--output"], ["extension_info (mode=points)", "extension_info (mode=table-merge)"]),
        C("find handlers", "Find event subscribers.", ["<OBJECT>"], ["--kind", "--output"], ["extension_info (mode=events)"]),
        C("find event-handlers", "Alias of `find handlers`.", ["<OBJECT>"], ["--kind", "--output"], ["extension_info (mode=events)"]),
        C("find refs", "Scan indexed X++ source for reverse references.", ["<NAME>"], ["--kind", "--model", "--limit", "--xref", "--output"], ["find_references"]),
        C("find references", "Alias of `find refs`.", ["<NAME>"], ["--kind", "--model", "--limit", "--xref", "--output"], ["find_references"]),
        C("find form-patterns", "Find forms by Microsoft form pattern, table, or peer form.", [], ["--pattern", "--table", "--similar-to", "--output"], ["object_patterns (domain=form, action=analyze)"]),

        C("index build", "Create or ensure the metadata index schema.", [], ["--db", "--output"], []),
        C("index status", "Report index table counts and config.", [], ["--output"], ["index_status"]),
        C("index extract", "Walk PackagesLocalDirectory and ingest AOT metadata.", [], ["--packages", "--db", "--model", "--since", "--output"], []),
        C("index refresh", "Incremental extract using model fingerprints.", [], ["--packages", "--db", "--model", "--since", "--force", "--output"], []),
        C("index sync", "Re-index ONE model, named directly or by a file inside it — for an edit made outside this tool.", ["[TARGET]"], ["--model", "--packages", "--db", "--index-source", "--output"], ["index_sync"]),
        C("index history", "Show recent extraction telemetry.", [], ["--db", "--model", "--limit", "--output"], ["index_history"]),
        C("models list", "List indexed models.", [], ["--output"], ["models (action=list)"]),
        C("models deps", "Show dependencies for a model.", ["<NAME>"], ["--output"], ["models (action=deps)"]),
        C("models coupling", "Show fan-in/fan-out/instability/cycles.", [], ["--top", "--only-cycles", "--output"], ["models (action=coupling)"]),

        C("generate table", "Scaffold AxTable XML.", ["<NAME>"], ["--out", "--overwrite", "--install-to", "--label", "--field", "--pattern", "--table-type", "--primary-key", "--output"], ["generate_object (objectType=table)"]),
        C("generate class", "Scaffold AxClass XML.", ["<NAME>"], ["--out", "--overwrite", "--install-to", "--extends", "--non-final", "--output"], ["generate_object (objectType=class)"]),
        C("generate coc", "Scaffold a Chain-of-Command class.", ["<TARGET>"], ["--out", "--overwrite", "--install-to", "--method", "--output"], ["generate_object (objectType=coc)"]),
        C("generate form", "Scaffold AxForm XML for the supported form patterns.", ["<FORM_NAME>"], ["--out", "--overwrite", "--install-to", "--pattern", "--table", "--caption", "--field", "--section", "--lines-table", "--output"], ["generate_object (objectType=form)"]),
        C("generate datasource-method", "Add/override a method on a form datasource (mutates the AxForm). Omit --method to list overridable methods.", ["<FORM>"], ["--datasource", "--method", "--return-type", "--body", "--list", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=datasource-method)"]),
        C("generate control-method", "Add/override a method on a form control (mutates the AxForm). Omit --method to list overridable methods.", ["<FORM>"], ["--control", "--method", "--return-type", "--body", "--list", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=control-method)"]),
        C("generate simple-list", "Deprecated alias for generate form --pattern SimpleList.", ["<FORM_NAME>"], ["--out", "--table", "--overwrite", "--output"], ["generate_object (objectType=form)"]),
        C("generate edt", "Scaffold AxEdt XML.", ["<NAME>"], ["--extends", "--label", "--size", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=edt)"]),
        C("generate enum", "Scaffold AxEnum XML.", ["<NAME>"], ["--label", "--value", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=enum)"]),
        C("generate query", "Scaffold AxQuery XML.", ["<NAME>"], ["--root-table", "--label", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=query)"]),
        C("generate sysoperation", "Scaffold SysOperation contract/service/controller.", ["<NAME>"], ["--execution-mode", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=sysoperation)"]),
        C("generate business-event", "Scaffold a business event class + contract.", ["<NAME>"], ["--contract", "--category", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=business-event)"]),
        C("generate runbase", "Scaffold a RunBase/RunBaseBatch class.", ["<NAME>"], ["--batch", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=runbase)"]),
        C("generate security-policy", "Scaffold an AxSecurityPolicy (XDS) XML.", ["<NAME>"], ["--constrained-table", "--policy-query", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=security-policy)"]),
        C("generate entity", "Scaffold AxDataEntityView XML.", ["<ENTITY>"], ["--out", "--overwrite", "--install-to", "--table", "--public-entity", "--public-collection", "--field", "--all-fields", "--output"], ["generate_object (objectType=entity)"]),
        C("generate extension", "Scaffold Table/Form/Edt/Enum extension XML.", ["<KIND>", "<TARGET>"], ["--suffix", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=extension)"]),
        C("generate event-handler", "Scaffold an event subscriber class.", ["<CLASS_NAME>"], ["--source-kind", "--source-object", "--event", "--method", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=event-handler)"]),
        C("generate privilege", "Scaffold a security privilege.", ["<NAME>"], ["--entry-point", "--entry-kind", "--entry-object", "--access", "--label", "--into-role", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=privilege)"]),
        C("generate duty", "Scaffold a security duty.", ["<NAME>"], ["--privilege", "--label", "--into-role", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=duty)"]),
        C("generate role", "Scaffold or merge a security role.", ["<NAME>"], ["--duty", "--privilege", "--label", "--description", "--add-to", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=role)"]),
        C("generate report", "Scaffold AxReport and RDP skeleton.", ["<NAME>"], ["--dp", "--tmp", "--dataset", "--caption", "--field", "--parameter", "--extra-dataset", "--out-dp", "--out-contract", "--out", "--overwrite", "--install-to", "--output"], ["generate_object (objectType=report)"]),

        C("delete", "Delete an AOT object (bridge or on-disk), journaled for `undo`.", [], ["--kind", "--name", "--install-to", "--path", "--model", "--output"], ["delete_object"]),

        C("analyze completeness", "Cross-check a workspace folder's AOT XML against the index: broken EDT, label and security-role references.", ["<PATH>"], ["--skip-labels", "--skip-edts", "--skip-security", "--output"], ["analyze (mode=completeness)"]),
        C("analyze integration", "Cross-check data entities for OData/DMF readiness.", [], ["--model", "--output"], ["analyze (mode=integration)"]),
        C("analyze impact", "List downstream consumers of an AOT object.", ["<OBJECT>"], ["--output"], ["analyze (mode=impact)"]),
        C("report-integrations", "Aggregated integration surface report.", [], ["--model", "--output"], ["analyze (mode=report)"]),
        C("review diff", "Inspect AOT changes vs. a git revision.", [], ["--base", "--head", "--repo", "--output"], ["get_workspace_info (changes=true)"]),
        C("build", "Invoke MSBuild on a D365FO project.", [], ["--msbuild", "--project", "--config", "--output"], ["sdlc (action=build)"]),
        C("sync", "Run DB sync.", [], ["--tool", "--full", "--output"], ["sdlc (action=sync)"]),
        C("test run", "Invoke the platform SysTest console runner (SysTestConsole.exe).", [], ["--runner", "--test", "--suite", "--granularity", "--results", "--parallel", "--output"], ["sdlc (action=test)"]),
        C("bp check", "Invoke xppbp best-practice checks.", [], ["--tool", "--model", "--packages", "--metadata", "--output"], ["sdlc (action=bp-check)"]),
        C("daemon start", "Start warm JSON-RPC daemon.", [], ["--db", "--packages", "--foreground", "--no-watch", "--watch-debounce"], []),
        C("daemon stop", "Stop daemon.", [], [], []),
        C("daemon status", "Report daemon status.", [], [], []),
        C("doctor", "Diagnose environment.", [], ["--output"], []),
        C("init", "Quickstart index/profile setup.", [], ["--packages", "--extra-packages", "--db", "--run-extract", "--dry-run", "--persist-profile"], []),
        C("version", "Print version information.", [], ["--output"], []),

        // ── The rest of the registered surface ─────────────────────────────
        // Everything below was reachable from the shell and invisible here, which is the same
        // as not shipping it to an agent that reads this manifest: the whole `modify` write
        // surface, the knowledge corpus, the BP-moniker catalog, three pattern catalogs and
        // eleven `generate` sub-commands. `CliMcpParityTests` now fails the build when a
        // registered command appears in neither this table nor its declared exclusions.
        C("search business-event", "Find business events (classes extending BusinessEventsBase).", ["<QUERY>"], ["--category", "--limit", "--output"], ["search (type=business-event)"]),
        C("search configuration-key", "Find configuration keys.", ["<QUERY>"], ["--limit", "--output"], ["search (type=configuration-key)"]),
        C("search security-policy", "Find XDS security policies.", ["<QUERY>"], ["--limit", "--output"], ["search (type=security-policy)"]),
        C("search tile", "Find navigation tiles.", ["<QUERY>"], ["--limit", "--output"], ["search (type=tile)"]),
        C("search workspace", "Find workspace descriptors.", ["<QUERY>"], ["--limit", "--output"], ["search (type=workspace)"]),

        C("get business-event", "Business event: class, category, contract.", ["<NAME>"], ["--output"], ["get_object_info (objectType=business-event)"]),
        C("get form-pattern", "Form pattern spec catalog: structure tree, versions, when-to-use, reference forms. Omit NAME to list all.", ["[NAME]"], ["--output"], ["object_patterns (domain=form, action=spec)"]),
        C("get security-policy", "XDS security policy: constrained table, query, operation type.", ["<NAME>"], ["--output"], ["get_object_info (objectType=security-policy)"]),

        C("find batch-jobs", "Find all RunBaseBatch / SysOperationServiceController subclasses.", [], ["--model", "--output"], ["search (type=batch-jobs)"]),

        C("resolve label", "Resolve @SYS12345-style label token to its text.", ["<TOKEN>"], ["--lang", "--output"], ["labels (action=resolve)"]),

        C("label create", "Create or update a label entry.", ["[KEY]", "[VALUE]"], ["--entry", "--file", "--install-to", "--lang", "--label-file", "--overwrite", "--allow-extension-label-file", "--output"], ["labels (action=create)"]),
        C("label delete", "Delete a label entry.", ["<KEY>"], ["--file", "--output"], ["labels (action=delete)"]),
        C("label rename", "Rename a label key.", ["<OLD>", "<NEW>"], ["--file", "--overwrite", "--allow-extension-label-file", "--output"], ["labels (action=rename)"]),

        C("knowledge get", "Fetch a topic, one of its '##' sections, or just its outline.", ["<TOPIC>"], ["--section", "--outline", "--output"], ["get_knowledge (action=get)"]),
        C("knowledge list", "List knowledge topics with descriptions and token cost.", [], ["--output"], ["get_knowledge (action=list)"]),
        C("knowledge search", "Rank topic sections against a free-text question.", ["<QUERY>"], ["--topic", "--limit", "--output"], ["get_knowledge (action=search)"]),

        C("bp-moniker search", "Find the rule covering a scenario, by words in its name, message or description.", ["<QUERY>"], ["--limit", "--canonical-only", "--output"], ["get_knowledge (kind=bp-moniker, action=search)"]),
        C("bp-moniker suppress", "Render a _BPSuppressions.xml <Diagnostic> block; refuses a moniker the catalog does not know.", ["<MONIKER>"], ["--path", "--justification", "--message", "--severity", "--output"], ["get_knowledge (kind=bp-moniker, action=suppress)"]),
        C("bp-moniker validate", "Is this an exact, real moniker? Case-sensitive, as xppbp is.", ["<MONIKER>"], ["--output"], ["get_knowledge (kind=bp-moniker, action=validate)"]),

        C("table-pattern list", "Every pattern with its TableGroup, when to use it, and its default fields.", [], ["--output"], ["object_patterns (domain=table, action=list)"]),
        C("table-pattern spec", "One pattern in full, with the scaffold call that produces it.", ["<PATTERN>"], ["--output"], ["object_patterns (domain=table, action=spec)"]),

        C("report-pattern list", "The seven shapes and when each applies.", [], ["--output"], ["object_patterns (domain=report, action=list)"]),
        C("report-pattern spec", "One shape in full, with shipped reference objects to read.", ["<PATTERN>"], ["--output"], ["object_patterns (domain=report, action=spec)"]),

        C("mobile-pattern list", "The framework decision, then the seven recipes.", [], ["--framework", "--output"], ["object_patterns (domain=mobile-app, action=list)"]),
        C("mobile-pattern spec", "One recipe in full, with shipped reference classes to read.", ["<RECIPE>"], ["--output"], ["object_patterns (domain=mobile-app, action=spec)"]),

        C("form-pattern repair", "Auto-repair the structural violations that have exactly one correct fix (missing controls, order, version, pattern defaults). Dry-run unless --apply/--out.", ["[FILE]"], ["--pattern", "--apply", "--out", "--output"], ["object_patterns (domain=form, action=repair)"]),

        C("validate form-pattern", "Structural form-pattern validator (FP001-FP010) over AxForm XML — same gate `generate form` enforces.", ["[FILE]"], ["--output"], ["validate (mode=form-pattern)"]),
        C("validate metadata", "Round-trip AOT XML through Microsoft's IMetadataProvider serializer and report anything it drops. Nothing is written. Requires D365FO_BRIDGE_ENABLED=1.", ["[PATH]"], ["--kind", "--recursive", "--output"], ["validate (mode=metadata)"]),

        C("generate custom-service", "Scaffold an AxService class, XML, and service group.", ["<NAME>"], ["--class-name", "--external-name", "--group-name", "--operation", "--contract-param", "--out-class", "--out-service", "--out-group", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=custom-service)"]),
        C("generate find-methods", "Generate the standard static find()/exists()/findRecId() for a table from its unique index; --apply-to merges them into the table XML.", ["<TABLE>"], ["--key", "--no-exists", "--no-find-recid", "--apply-to", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=find-methods)"]),
        C("generate form-clone", "Clone an existing AxForm under a new name, optionally re-binding its datasources.", ["<NAME>"], ["--from", "--rebind", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=form-clone)"]),
        C("generate map", "Create an AxMap: a shared field template mapped onto tables.", ["<NAME>"], ["--field", "--map-to", "--label", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=map)"]),
        C("generate menu-item", "Create an AxMenuItemDisplay, AxMenuItemAction, or AxMenuItemOutput.", ["<NAME>"], ["--kind", "--object", "--object-type", "--label", "--enum-type", "--enum-value", "--parameters", "--config-key", "--query", "--needed-permission", "--linked-permission-object", "--linked-permission-type", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=menu-item)"]),
        C("generate migration-script", "Scaffold a SysRunnable data-migration class.", ["<NAME>"], ["--source-table", "--target-table", "--batch-size", "--mode", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=migration-script)"]),
        C("generate number-sequence", "Create a NumberSeq module extension, EDT, and form handler.", ["<MODULE_NAME>"], ["--edt", "--edt-label", "--scope", "--table", "--out-edt", "--out-handler", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=number-sequence)"]),
        C("generate report-extension", "Extend a SHIPPED report: dataset (PostHandlerFor/DataEventHandler), custom-design (controller + PrintMgmt delegate), or menu-redirect (post-handler on construct()).", ["<PATTERN>"], ["--dp", "--tmp-table", "--dataset-accessor", "--report", "--design", "--document-type", "--base-controller", "--controller", "--suffix", "--out-second", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=report-extension)"]),
        C("generate systest", "Scaffold an ATL-ready SysTestCase class (Arrange/Act/Assert skeleton).", ["<NAME>"], ["--data-area-id", "--atl", "--class", "--table", "--method", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=systest)"]),
        C("generate table-relation", "Derive explicit AxTableRelation fragments from a table's EDT references (BPErrorEDTNotMigrated); --apply-to merges them into the table XML.", ["<TABLE>"], ["--field", "--apply-to", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=table-relation)"]),
        C("generate view", "Create an AxView projecting an AxQuery (bound and computed fields).", ["<NAME>"], ["--query", "--field", "--computed", "--label", "--configuration-key", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=view)"]),
        C("generate workflow", "Create an AxWorkflowTemplate (workflow type), its approval/task elements, a WorkflowDocument class, and a canSubmitToWorkflow stub.", ["<NAME>"], ["--table", "--approval-name", "--task-name", "--category", "--document-menu-item", "--submit-menu-item", "--document-class", "--query", "--no-query", "--out-query", "--out-document", "--out-approval", "--out-task", "--out-submit", "--no-submit-stub", "--out", "--overwrite", "--install-to", "--verify", "--output"], ["generate_object (objectType=workflow)"]),

        C("modify add-control", "Add a control to a live form's design, optionally bound to a datasource field.", ["<FORM>", "<CONTROL>"], ["--type", "--parent", "--datasource", "--datafield", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-control)"]),
        C("modify add-delete-action", "Add a delete action to a live table (Cascade | Restricted | CascadeRestricted | None).", ["<TABLE>"], ["--related-table", "--action", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-delete-action)"]),
        C("modify add-entry-point", "Grant an entry point on a security privilege.", ["<PRIVILEGE>", "<OBJECT>"], ["--type", "--access", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-entry-point)"]),
        C("modify add-enum-value", "Add a value to a live base enum (positional, never a hard-coded ordinal).", ["<ENUM>", "<VALUE>"], ["--label", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-enum-value)"]),
        C("modify add-field", "Add a field to a live table — concrete AxTableField subtype resolved from the EDT.", ["<TABLE>", "<FIELD>"], ["--edt", "--label", "--mandatory", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-field)"]),
        C("modify add-field-group", "Add a field group to a live table.", ["<TABLE>", "<GROUP>"], ["--field", "--label", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-field-group)"]),
        C("modify add-index", "Add an index to a live table; unique unless --allow-duplicates.", ["<TABLE>", "<INDEX>"], ["--field", "--allow-duplicates", "--alternate-key", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-index)"]),
        C("modify add-query-range", "Add a range to an AOT query's datasource.", ["<QUERY>", "<FIELD>"], ["--data-source", "--value", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-query-range)"]),
        C("modify add-relation", "Add a foreign-key relation to a live table.", ["<TABLE>", "<FIELD>"], ["--related-table", "--related-field", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=add-relation)"]),
        C("modify batch", "Apply several changes to one object in a single read-edit-write; a refused step discards the batch.", ["<KIND>", "<OBJECT>"], ["--operations", "--operations-file", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (operations[])"]),
        C("modify method", "Replace the body of an existing method on a class/table/edt/form.", ["<KIND>", "<OBJECT>", "<METHOD>"], ["--body", "--model", "--output"], ["modify_method"]),
        C("modify property", "Set a property (Label, ConfigurationKey, TableGroup, …) on a live object.", ["<KIND>", "<OBJECT>", "<PROPERTY>"], ["--value", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=property)"]),
        C("modify remove-control", "Remove a control, and everything nested under it, from a live form.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-control)"]),
        C("modify remove-delete-action", "Remove a delete action, named by its related table.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-delete-action)"]),
        C("modify remove-entry-point", "Revoke an entry point from a security privilege.", ["<PRIVILEGE>", "<OBJECT>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-entry-point)"]),
        C("modify remove-enum-value", "Remove a value from a live base enum.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-enum-value)"]),
        C("modify remove-field", "Remove a field from a live table.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-field)"]),
        C("modify remove-field-group", "Remove a field group from a live table.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-field-group)"]),
        C("modify remove-index", "Remove an index from a live table.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-index)"]),
        C("modify remove-query-range", "Remove a range from an AOT query's datasource.", ["<QUERY>", "<FIELD>"], ["--data-source", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-query-range)"]),
        C("modify remove-relation", "Remove a relation from a live table.", ["<OBJECT>", "<MEMBER>"], ["--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=remove-relation)"]),
        C("modify rename-field", "Rename a table field, rewriting the indexes, field groups and relations that name it.", ["<TABLE>", "<FIELD>"], ["--new-name", "--model", "--extension", "--extension-model", "--require-extension", "--output"], ["modify_object (action=rename-field)"]),

        C("explain-error", "Score xppc/build errors (argument, --file, or stdin) against the fix-hint rules and point at the knowledge topic behind each.", ["[MESSAGE]"], ["--file", "--all", "--output"], ["explain_build_error"]),
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
