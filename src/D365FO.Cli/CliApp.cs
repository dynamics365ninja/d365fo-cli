using D365FO.Cli.Commands;
using D365FO.Cli.Commands.Agent;
using D365FO.Cli.Commands.Analyze;
using D365FO.Cli.Commands.Daemon;
using D365FO.Cli.Commands.Eval;
using D365FO.Cli.Commands.Find;
using D365FO.Cli.Commands.Generate;
using D365FO.Cli.Commands.Get;
using D365FO.Cli.Commands.Index;
using D365FO.Cli.Commands.Knowledge;
using D365FO.Cli.Commands.Models;
using D365FO.Cli.Commands.Modify;
using D365FO.Cli.Commands.Ops;
using D365FO.Cli.Commands.Read;
using D365FO.Cli.Commands.Resolve;
using D365FO.Cli.Commands.Review;
using D365FO.Cli.Commands.Search;
using D365FO.Cli.Commands.Stats;
using D365FO.Cli.Commands.Suggest;
using D365FO.Cli.Commands.Validate;
using D365FO.Cli.Commands.Lint;
using Spectre.Console.Cli;

namespace D365FO.Cli;

/// <summary>
/// Builds the <see cref="CommandApp"/> that backs the <c>d365fo</c> CLI.
/// Extracted from <c>Program.cs</c> so the exact same command surface can be
/// re-entered in-process — e.g. by <c>d365fo eval run</c>, which replays a
/// case's <c>canonical_args</c> through this app rather than through a
/// separate dispatch table.
/// </summary>
public static class CliApp
{
    /// <param name="console">
    /// Optional scoped console to render through instead of the global
    /// <see cref="Spectre.Console.AnsiConsole.Console"/> singleton — lets tests
    /// capture output without mutating process-wide static state (which would
    /// race against other tests capturing console output in parallel).
    /// </param>
    public static CommandApp Build(Spectre.Console.IAnsiConsole? console = null)
    {
        var app = new CommandApp();
        app.Configure(cfg =>
        {
            cfg.SetApplicationName("d365fo");
            cfg.SetApplicationVersion("0.1.0-dev");
            cfg.CaseSensitivity(CaseSensitivity.None);
            cfg.PropagateExceptions();

            // Spectre's parser defaults to StrictParsing=false, which silently
            // collects any unrecognised option into IRemainingArguments — nothing
            // in this CLI reads those, so `generate table X --storage TempDB`
            // (the option is really --table-type) wrote a regular table and still
            // reported ok:true. A misspelled flag that the tool confidently
            // reports success for is the worst failure mode in this repo's triage
            // rubric (eval/README.md), so unknown options must fail loudly.
            cfg.Settings.StrictParsing = true;
            if (console is not null) cfg.ConfigureConsole(console);

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
                b.AddCommand<SearchAnyCommand>("any").WithDescription("Scope-agnostic search across every indexed kind.");
                b.AddCommand<SearchBatchCommand>("batch").WithDescription("Run several scope-agnostic searches in one CLI call.");
                b.AddCommand<SearchBusinessEventCommand>("business-event").WithDescription("Find business events (classes extending BusinessEventsBase).");
                b.AddCommand<SearchSecurityPolicyCommand>("security-policy").WithDescription("Find XDS security policies.");
                b.AddCommand<SearchConfigurationKeyCommand>("configuration-key").WithDescription("Find configuration keys.");
                b.AddCommand<SearchTileCommand>("tile").WithDescription("Find navigation tiles.");
                b.AddCommand<SearchWorkspaceCommand>("workspace").WithDescription("Find workspace descriptors.");
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
                b.AddCommand<GetObjectCommand>("object").WithDescription("Generic get by kind/name for agent workflows.");
                b.AddCommand<GetBatchCommand>("batch").WithDescription("Fetch up to 10 objects (<kind>:<name> specs) in one call.");
                b.AddCommand<GetFormPatternCommand>("form-pattern").WithDescription("Form pattern spec catalog: structure tree, versions, when-to-use, reference forms. Omit NAME to list all.");
                b.AddCommand<GetBusinessEventCommand>("business-event").WithDescription("Business event: class, category, contract.");
                b.AddCommand<GetSecurityPolicyCommand>("security-policy").WithDescription("XDS security policy: constrained table, query, operation type.");
            });

            cfg.AddBranch("find", b =>
            {
                b.SetDescription("Discover cross-references.");
                b.AddCommand<FindCocCommand>("coc").WithDescription("Find Chain-of-Command extensions.");
                b.AddCommand<FindRelationsCommand>("relations").WithDescription("Find table relations.");
                b.AddCommand<FindUsagesCommand>("usages").WithDescription("Find index entities whose name contains a substring.");
                b.AddCommand<FindFieldsCommand>("fields").WithDescription("Find tables that declare a field name or EDT (exact match) — precise field-level lookup, not a relation/FK or source-code search.");
                b.AddCommand<FindExtensionsCommand>("extensions").WithDescription("Find Table/Form/Edt/Enum extensions targeting an object.");
                b.AddCommand<FindHandlersCommand>("handlers").WithDescription("Find event handlers subscribed to a form/table/delegate.");
                b.AddCommand<FindRefsCommand>("refs").WithDescription("Regex scan of indexed X++ source for reverse references to a symbol.");
                b.AddCommand<FindFormPatternsCommand>("form-patterns").WithDescription("Analyse indexed forms by Microsoft pattern / primary table / similarity to a reference form.");
                b.AddCommand<FindRelatedCommand>("related").WithDescription("Generic relation lookup by relation/name for agent workflows.");
                b.AddCommand<FindBatchJobsCommand>("batch-jobs").WithDescription("Find all RunBaseBatch / SysOperationServiceController subclasses.");
                // Parity aliases matching the unified MCP surface (extension_info mode=events / find_references).
                b.AddCommand<FindHandlersCommand>("event-handlers").WithDescription("Alias of `find handlers` — event handlers subscribed to a form/table/delegate.");
                b.AddCommand<FindRefsCommand>("references").WithDescription("Alias of `find refs` — reverse references to a symbol in indexed X++ source.");
            });

            // Unified `security` branch — mirrors the MCP `security_info` tool
            // (mode=artifact → role/duty/privilege; mode=coverage → coverage).
            cfg.AddBranch("security", b =>
            {
                b.SetDescription("Security hierarchy: named artifacts and reverse coverage. Mirrors the MCP `security_info` tool.");
                b.AddCommand<GetRoleCommand>("role").WithDescription("Security role: duties + privileges.");
                b.AddCommand<GetDutyCommand>("duty").WithDescription("Security duty: privileges.");
                b.AddCommand<GetPrivilegeCommand>("privilege").WithDescription("Security privilege: entry points.");
                b.AddCommand<GetSecurityCommand>("coverage").WithDescription("Role→Duty→Privilege routes that grant access to an object.");
            });

            // Unified `form-pattern` branch — mirrors the MCP `object_patterns` tool
            // (domain=form, action=analyze|spec|validate).
            cfg.AddBranch("form-pattern", b =>
            {
                b.SetDescription("Form-pattern advisor, spec catalog, and structural validator. Mirrors the MCP `object_patterns` tool (domain=form).");
                b.AddCommand<FindFormPatternsCommand>("analyze").WithDescription("Analyse indexed forms by Microsoft pattern / primary table / similarity to a reference form.");
                b.AddCommand<GetFormPatternCommand>("spec").WithDescription("Form pattern spec catalog: structure tree, versions, when-to-use, reference forms. Omit NAME to list all.");
                b.AddCommand<ValidateFormPatternCommand>("validate").WithDescription("Structural form-pattern validator (FP001-FP010) over AxForm XML.");
                b.AddCommand<RepairFormPatternCommand>("repair").WithDescription("Auto-repair the structural violations that have exactly one correct fix (missing controls, order, version, pattern defaults). Dry-run unless --apply/--out.");
            });

            // Unified `labels` branch — mirrors the MCP `labels` tool
            // (action=search|fts|info|resolve|create|rename|delete).
            cfg.AddBranch("labels", b =>
            {
                b.SetDescription("All label operations: search, resolve, and edit *.label.txt files. Mirrors the MCP `labels` tool.");
                b.AddCommand<SearchLabelCommand>("search").WithDescription("Search label file entries (FTS5-ranked; pass --fts for explicit FTS syntax).");
                b.AddCommand<ResolveLabelCommand>("resolve").WithDescription("Resolve a @SYS12345-style label token to its text across languages.");
                b.AddCommand<GetLabelCommand>("info").WithDescription("Fetch one label entry by (file, language, key).");
                b.AddCommand<D365FO.Cli.Commands.Label.LabelCreateCommand>("create").WithDescription("Create or update a label entry.");
                b.AddCommand<D365FO.Cli.Commands.Label.LabelRenameCommand>("rename").WithDescription("Rename a label key.");
                b.AddCommand<D365FO.Cli.Commands.Label.LabelDeleteCommand>("delete").WithDescription("Delete a label entry.");
            });

            cfg.AddBranch("resolve", b =>
            {
                b.SetDescription("Resolve tokens (labels etc.) to their concrete values.");
                b.AddCommand<ResolveLabelCommand>("label").WithDescription("Resolve @SYS12345-style label token to its text.");
            });

            cfg.AddBranch("suggest", b =>
            {
                b.SetDescription("Heuristic suggestions over the index (no scaffolding).");
                b.AddCommand<SuggestEdtCommand>("edt").WithDescription("Suggest EDTs matching a field name.");
                b.AddCommand<SuggestExtensionCommand>("extension").WithDescription("Recommend CoC / event-handler / AOT-extension strategy for a Class, Table, or Form.");
            });

            cfg.AddBranch("prepare", b =>
            {
                b.SetDescription("Single-round context aggregators: gather everything needed for a change/new object in ONE call and get a grounding token.");
                b.AddCommand<D365FO.Cli.Commands.Prepare.PrepareChangeCommand>("change").WithDescription("Aggregate signature, CoC wrappers, eligibility, strategy, and naming for an extension change. Replaces 4–6 discovery calls.");
                b.AddCommand<D365FO.Cli.Commands.Prepare.PrepareCreateCommand>("create").WithDescription("Aggregate collision check, naming, similar objects, EDT suggestions, labels, and property defaults for a NEW object.");
                b.AddCommand<D365FO.Cli.Commands.Prepare.PrepareTestCommand>("test").WithDescription("Aggregate everything needed to WRITE a SysTest: methods worth covering, existing coverage, TestEssentials check, the scaffold call, and the red-first cycle.");
            });

            cfg.AddBranch("validate", b =>
            {
                b.SetDescription("Static checks without touching the filesystem.");
                b.AddCommand<ValidateNameCommand>("name").WithDescription("Check object name against naming conventions.");
                b.AddCommand<ValidateXppCommand>("xpp").WithDescription("Offline X++/XML best-practice validator over a file or stdin (no VM needed).");
                b.AddCommand<ValidateReferencesCommand>("references").WithDescription("Semantic anti-hallucination gate: verify every identifier in X++ code against the index.");
                b.AddCommand<ValidateMetadataCommand>("metadata").WithDescription("Round-trip AOT XML through Microsoft's IMetadataProvider serializer and report anything it drops. Nothing is written. Requires D365FO_BRIDGE_ENABLED=1.");
                b.AddCommand<ValidateFormPatternCommand>("form-pattern").WithDescription("Structural form-pattern validator (FP001-FP010) over AxForm XML — same gate `generate form` enforces.");
                b.AddCommand<RepairFormPatternCommand>("form-pattern-repair").WithDescription("Alias of `form-pattern repair` — auto-repair deterministic structural violations.");
            });

            cfg.AddBranch("label", b =>
            {
                b.SetDescription("Edit *.label.txt resource files in-place.");
                b.AddCommand<D365FO.Cli.Commands.Label.LabelCreateCommand>("create").WithDescription("Create or update a label entry.");
                b.AddCommand<D365FO.Cli.Commands.Label.LabelRenameCommand>("rename").WithDescription("Rename a label key.");
                b.AddCommand<D365FO.Cli.Commands.Label.LabelDeleteCommand>("delete").WithDescription("Delete a label entry.");
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
                b.AddCommand<IndexRefreshCommand>("refresh").WithDescription("Incremental extract — skip models whose XMLs haven't changed since last extract.");
                b.AddCommand<IndexHistoryCommand>("history").WithDescription("Show recent ExtractionRuns (per-model timings persisted across runs).");
                b.AddCommand<IndexCrossCheckCommand>("cross-check").WithDescription("Report where this tool's catalogs are narrower than the installation.");
                b.AddCommand<IndexOptimizeCommand>("optimize").WithDescription("VACUUM + ANALYZE the index (reclaim space, refresh query-planner stats).");
                b.AddCommand<IndexExportCommand>("export").WithDescription("Export index as a GZip-compressed snapshot for sharing or CI caching.");
                b.AddCommand<IndexImportCommand>("import").WithDescription("Import a GZip-compressed index snapshot.");
            });

            cfg.AddBranch("models", b =>
            {
                b.SetDescription("Inspect indexed models and their descriptor-declared dependencies.");
                b.AddCommand<ModelsListCommand>("list").WithDescription("List indexed models (name/publisher/layer/custom).");
                b.AddCommand<ModelsDepsCommand>("deps").WithDescription("Show dependency graph for a model (depends-on / depended-by).");
                b.AddCommand<ModelsCouplingCommand>("coupling").WithDescription("Coupling metrics (fan-in, fan-out, instability, cycles) over ModelDependencies.");
            });

            cfg.AddBranch("generate", b =>
            {
                b.SetDescription("Scaffold AOT XML skeletons.");
                b.AddCommand<GenerateTableCommand>("table").WithDescription("Create a new AxTable.");
                b.AddCommand<GenerateTableRelationCommand>("table-relation").WithDescription("Derive explicit AxTableRelation fragments from a table's EDT references (BPErrorEDTNotMigrated); --apply-to merges them into the table XML.");
                b.AddCommand<GenerateFindMethodsCommand>("find-methods").WithDescription("Generate the standard static find()/exists()/findRecId() for a table from its unique index; --apply-to merges them into the table XML.");
                b.AddCommand<GenerateClassCommand>("class").WithDescription("Create a new AxClass.");
                b.AddCommand<GenerateCocCommand>("coc").WithDescription("Create a Chain-of-Command extension class.");
                b.AddCommand<GenerateFormCommand>("form").WithDescription("Create an AxForm with a chosen pattern (SimpleList, DetailsMaster, DetailsTransaction, Dialog, Lookup, ListPage, Workspace, …).");
                b.AddCommand<GenerateDataSourceMethodCommand>("datasource-method").WithDescription("Add/override a method on a form datasource (form-level SourceCode). Omit --method to list overridable methods.");
                b.AddCommand<GenerateControlMethodCommand>("control-method").WithDescription("Add/override a method on a form control (form-level SourceCode). Omit --method to list overridable methods.");
                b.AddCommand<GenerateFormCloneCommand>("form-clone").WithDescription("Clone an existing AxForm under a new name, optionally re-binding its datasources.");
                b.AddCommand<GenerateSimpleListCommand>("simple-list").WithDescription("(Deprecated) Alias for `generate form --pattern SimpleList`.");
                b.AddCommand<GenerateEntityCommand>("entity").WithDescription("Create an AxDataEntityView over a table.");
                b.AddCommand<GenerateExtensionCommand>("extension").WithDescription("Create a Table/Form/Edt/Enum extension.");
                b.AddCommand<GenerateEventHandlerCommand>("event-handler").WithDescription("Create an event subscriber class.");
                b.AddCommand<GeneratePrivilegeCommand>("privilege").WithDescription("Create a security privilege over an entry point.");
                b.AddCommand<GenerateDutyCommand>("duty").WithDescription("Create a security duty grouping privileges.");
                b.AddCommand<GenerateRoleCommand>("role").WithDescription("Create an AxSecurityRole or merge duties/privileges into an existing role.");
                b.AddCommand<GenerateReportCommand>("report").WithDescription("Create an AxReport + SrsReportDataProviderBase skeleton (DP class).");
                b.AddCommand<GenerateReportExtensionCommand>("report-extension").WithDescription("Extend a SHIPPED report: dataset (PostHandlerFor/DataEventHandler), custom-design (controller + PrintMgmt delegate), or menu-redirect (post-handler on construct()).");
                b.AddCommand<GenerateSysOperationCommand>("sysoperation").WithDescription("Create a SysOperation DataContract + Service + Controller triplet.");
                b.AddCommand<GenerateNumberSequenceCommand>("number-sequence").WithDescription("Create a NumberSeq module extension, EDT, and form handler.");
                b.AddCommand<GenerateWorkflowCommand>("workflow").WithDescription("Create an AxWorkflowTemplate (workflow type), its approval/task elements, a WorkflowDocument class, and a canSubmitToWorkflow stub.");
                b.AddCommand<GenerateMenuItemCommand>("menu-item").WithDescription("Create an AxMenuItemDisplay, AxMenuItemAction, or AxMenuItemOutput.");
                b.AddCommand<GenerateEdtCommand>("edt").WithDescription("Create an AxEdt Extended Data Type.");
                b.AddCommand<GenerateEnumCommand>("enum").WithDescription("Create an AxEnum base enumeration.");
                b.AddCommand<GenerateQueryCommand>("query").WithDescription("Create an AxQuery with data sources and joins.");
                b.AddCommand<GenerateViewCommand>("view").WithDescription("Create an AxView projecting an AxQuery (bound and computed fields).");
                b.AddCommand<GenerateMapCommand>("map").WithDescription("Create an AxMap: a shared field template mapped onto tables.");
                b.AddCommand<GenerateBusinessEventCommand>("business-event").WithDescription("Scaffold a business event class + contract.");
                b.AddCommand<GenerateCustomServiceCommand>("custom-service").WithDescription("Scaffold an AxService class, XML, and service group.");
                b.AddCommand<GenerateMigrationScriptCommand>("migration-script").WithDescription("Scaffold a SysRunnable data-migration class.");
                b.AddCommand<GenerateRunBaseCommand>("runbase").WithDescription("Scaffold a RunBase/RunBaseBatch class with dialog and pack/unpack.");
                b.AddCommand<GenerateSecurityPolicyCommand>("security-policy").WithDescription("Scaffold an AxSecurityPolicy (XDS) XML.");
                b.AddCommand<GenerateSysTestCommand>("systest").WithDescription("Scaffold an ATL-ready SysTestCase class (Arrange/Act/Assert skeleton).");
            });

            cfg.AddBranch("modify", b =>
            {
                b.SetDescription("Structured, live edits to existing AOT objects via D365FO.Bridge (Windows VM). No on-disk fallback. Writes outside D365FO_CUSTOM_MODELS are redirected to an extension. Every edit is journaled for `d365fo undo`.");
                b.AddCommand<ModifyMethodCommand>("method").WithDescription("Replace the body of an existing method on a class/table/edt/form.");
                b.AddCommand<ModifyPropertyCommand>("property").WithDescription("Set a property (Label, ConfigurationKey, TableGroup, …) on a live object.");
                b.AddCommand<ModifyAddFieldCommand>("add-field").WithDescription("Add a field to a live table — concrete AxTableField subtype resolved from the EDT.");
                b.AddCommand<ModifyAddEnumValueCommand>("add-enum-value").WithDescription("Add a value to a live base enum (positional, never a hard-coded ordinal).");
                b.AddCommand<ModifyAddControlCommand>("add-control").WithDescription("Add a control to a live form's design, optionally bound to a datasource field.");
            });

            cfg.AddBranch("analyze", b =>
            {
                b.SetDescription("Cross-check workspace AOT XML against the index.");
                b.AddCommand<AnalyzeCompletenessCommand>("completeness").WithDescription("Report broken EDT, label, security-role references in a project folder.");
                b.AddCommand<AnalyzeIntegrationCommand>("integration").WithDescription("Cross-check data entities for OData / DMF integration readiness.");
                b.AddCommand<AnalyzeImpactCommand>("impact").WithDescription("Change-impact analysis: list all downstream consumers of an AOT object.");
            });

            cfg.AddCommand<ReportIntegrationsCommand>("report-integrations").WithDescription("Aggregated report of OData entities, services, business events, workflow types, and batch jobs.");

            cfg.AddBranch("test", b =>
            {
                b.SetDescription("Run D365FO developer tests (Windows VM).");
                b.AddCommand<TestRunCommand>("run").WithDescription("Invoke the platform SysTest console runner (SysTestConsole.exe).");
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
                b.AddCommand<DaemonWarmupCommand>("warmup").WithDescription("Pre-warm the SQLite page cache for faster first queries.");
            });

            cfg.AddCommand<D365FO.Cli.Commands.Journal.UndoCommand>("undo").WithDescription("Revert the last N modification-journal entries (create removed, update/delete restored to their exact pre-image).");
            cfg.AddCommand<D365FO.Cli.Commands.Journal.DeleteObjectCommand>("delete").WithDescription("Delete an AOT object (bridge or on-disk), journaled for `d365fo undo`.");

            cfg.AddBranch("journal", b =>
            {
                b.SetDescription("Inspect the modification journal (issue #113).");
                b.AddCommand<D365FO.Cli.Commands.Journal.JournalListCommand>("list").WithDescription("List journal entries, most-recent-first.");
            });

            cfg.AddBranch("eval", b =>
            {
                b.SetDescription("Self-improving agent eval loop: run/score catalog cases against golden metadata (docs/AGENT_EVAL_LOOP.md).");
                b.AddCommand<EvalListCommand>("list").WithDescription("List eval catalog cases.");
                b.AddCommand<EvalRunCommand>("run").WithDescription("Deterministic replay of a case's canonical_args: generate -> validate -> golden diff -> score.");
                b.AddCommand<EvalScoreCommand>("score").WithDescription("Score an already-produced artifact against a case's golden (for agent-driven runs).");
                b.AddCommand<EvalCaptureCommand>("capture").WithDescription("Capture/update a case's golden from a reviewed artifact.");
                b.AddCommand<EvalReportCommand>("report").WithDescription("Aggregate scoreboard over the corpus of run records.");
                b.AddCommand<EvalClustersCommand>("clusters").WithDescription("Rank failure clusters over the corpus of run records.");
                b.AddCommand<EvalCoverageCommand>("coverage").WithDescription("K/E/T coverage taxonomy per AOT family and generate capability; --check gates eval/COVERAGE.md.");
                b.AddCommand<EvalKnowledgeCommand>("knowledge").WithDescription("Turn KNOWLEDGE_GAP / MODEL_ERROR runs into skills/_source topic-edit proposals with corpus provenance.");
                b.AddCommand<EvalVerifyBuildCommand>("verify-build").WithDescription("L3 oracle (Windows + D365FO install): recompile every golden with xppc and persist the verdicts.");
            });

            // The CLI's `get_knowledge` equivalent: the verified skills/_source corpus,
            // embedded in the binary and served per-topic / per-section so an agent
            // without skill-file support can still ground itself.
            cfg.AddBranch("knowledge", b =>
            {
                b.SetDescription("Verified X++/D365FO knowledge topics (the skills/_source corpus), served per topic or per section.");
                b.AddCommand<KnowledgeListCommand>("list").WithDescription("List knowledge topics with descriptions and token cost.");
                b.AddCommand<KnowledgeGetCommand>("get").WithDescription("Fetch a topic, one of its '##' sections, or just its outline.");
                b.AddCommand<KnowledgeSearchCommand>("search").WithDescription("Rank topic sections against a free-text question.");
                b.AddCommand<KnowledgeAuditCommand>("audit").WithDescription("Prove the corpus itself: every named API resolves against the index (live or snapshot) and every example passes the BP validator.");
            });

            cfg.AddCommand<ExplainErrorCommand>("explain-error").WithDescription("Score xppc/build errors (argument, --file, or stdin) against the fix-hint rules and point at the knowledge topic behind each.");

            cfg.AddCommand<BuildCommand>("build").WithDescription("Invoke MSBuild (Windows VM).");
            cfg.AddCommand<SyncCommand>("sync").WithDescription("Run DB sync (Windows VM).");
            cfg.AddCommand<D365FO.Cli.Commands.Connect.ConnectCommand>("connect").WithDescription("Point an editor's MCP config at a deployed `d365fo-mcp --http` server (probes /health, merges rather than clobbers).");
            cfg.AddCommand<DoctorCommand>("doctor").WithDescription("Diagnose environment.");
            cfg.AddCommand<InitCommand>("init").WithDescription("Interactive quickstart: detects PackagesLocalDirectory and prepares the index.");
            cfg.AddCommand<StatsCommand>("stats").WithDescription("Aggregate counters over the index (top tables / classes / CoC targets).");
            cfg.AddCommand<LintCommand>("lint").WithDescription("In-process Best-Practice gate over the index.");
            cfg.AddCommand<VersionCommand>("version").WithDescription("Print version information.");
            cfg.AddCommand<AgentPromptCommand>("agent-prompt").WithDescription("Emit LLM system prompt for this CLI.");
            cfg.AddCommand<SchemaCommand>("schema").WithDescription("Emit JSON command manifest.");
            cfg.AddCommand<CompletionCommand>("completion").WithDescription("Emit shell tab-completion script (bash, zsh, powershell).");
        });
        return app;
    }
}
