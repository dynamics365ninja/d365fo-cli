using System.Text.Json;
using System.Text.Json.Nodes;

namespace D365FO.Mcp;

/// <summary>
/// Catalog of MCP-exposed tools. Each entry holds:
/// <list type="bullet">
///   <item><description>the MCP tool name agents see (<c>tools/list</c>),</description></item>
///   <item><description>a human description,</description></item>
///   <item><description>a JSON Schema <c>inputSchema</c> so MCP clients render strong UIs,</description></item>
///   <item><description>a thin binder that turns a <c>tools/call</c> params object into
///   a <see cref="ToolHandlers"/> invocation.</description></item>
/// </list>
/// Kept as a hand-written table so the server can publish <c>inputSchema</c>
/// without reflection — important once this ships as a trimmed/AOT binary.
/// </summary>
public static class ToolCatalog
{
    public readonly record struct Descriptor(
        string Name,
        string Description,
        JsonObject InputSchema,
        Func<ToolHandlers, JsonElement, object> Invoke);

    /// <summary>
    /// Tools that modify the file system. Everything else is read-only. Drives
    /// both the MCP tool annotations (readOnlyHint/destructiveHint) and the
    /// duplicate-call dedup cache exclusions.
    /// </summary>
    public static readonly HashSet<string> WriteTools = new(StringComparer.Ordinal)
    {
        // Unified write tools. `generate_object` writes AOT XML to disk for the
        // table/class/coc/form objectTypes (the edt/enum/query/… objectTypes are
        // XML-only, but the whole tool is flagged write so clients confirm before
        // any write objectType runs). `labels` mixes read actions (search/info)
        // with write actions (create/rename/delete) — flagged here too.
        // `undo_last_modification` mutates the file system (or the live metadata
        // provider) just like the write it reverts — flagged write even though its
        // `dryRun` mode is read-only, since clients gate on the tool, not the call.
        "generate_object", "labels", "modify_method", "undo_last_modification",
    };

    /// <summary>
    /// MCP tool annotations (2025-03-26 spec): a human title plus behaviour
    /// hints, so clients can label runs ("Ran Search Classes") and skip write
    /// confirmations for read-only tools.
    /// </summary>
    public static JsonObject AnnotationsFor(in Descriptor d)
    {
        var isWrite = WriteTools.Contains(d.Name);
        return new JsonObject
        {
            ["title"] = TitleFor(d.Name),
            ["readOnlyHint"] = !isWrite,
            ["destructiveHint"] = isWrite,
            ["idempotentHint"] = !isWrite,
            ["openWorldHint"] = false,
        };
    }

    private static string TitleFor(string name) =>
        string.Join(' ', name.Split('_').Select(w =>
            w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    public static IReadOnlyList<Descriptor> All { get; } = new[]
    {
        // ──────────────────────────────────────────────────────────────────
        // Unified tool surface. Each tool dispatches to the underlying
        // ToolHandlers methods via a discriminator (type / objectType / mode /
        // action / include). This mirrors the upstream d365fo-mcp-server
        // consolidation: fewer tools for the agent to choose from, identical
        // coverage. No handler logic changed.
        // ──────────────────────────────────────────────────────────────────

        // ---- Search & discovery ----

        new Descriptor("search",
            "Unified metadata search across the index. `type` selects the kind: " +
            "class | table | edt | enum | query | view | entity | report | service | workflow | " +
            "business-event | security-policy | configuration-key | tile | workspace | batch-jobs | any. " +
            "Pass `queries` (array) to run several searches in one call (batch). Omit `type` (or use `any`) " +
            "for a scope-agnostic search across every kind. `model` filters class/table searches; " +
            "`category` filters business-event searches.",
            Schema(("type", "string", false), ("query", "string", false), ("queries", "array", false),
                   ("model", "string", false), ("category", "string", false), ("limit", "integer", false)),
            (h, p) =>
            {
                var queries = StrArray(p, "queries");
                if (queries is { Length: > 0 }) return h.BatchSearch(queries, Int(p, "limit", 50));
                var query = Str(p, "query");
                var limit = Int(p, "limit", 50);
                var model = StrOrNull(p, "model");
                return StrOr(p, "type", "any").ToLowerInvariant() switch
                {
                    "class"             => h.SearchClasses(query, model, limit),
                    "table"             => h.SearchTables(query, model, limit),
                    "edt"               => h.SearchEdts(query, limit),
                    "enum"              => h.SearchEnums(query, limit),
                    "query"             => h.SearchQueries(query, limit),
                    "view"              => h.SearchViews(query, limit),
                    "entity" or "data-entity" => h.SearchDataEntities(query, limit),
                    "report"            => h.SearchReports(query, limit),
                    "service"           => h.SearchServices(query, limit),
                    "workflow"          => h.SearchWorkflowTypes(query, limit),
                    "business-event"    => h.SearchBusinessEvents(query, StrOrNull(p, "category"), limit),
                    "security-policy"   => h.SearchSecurityPolicies(query, limit),
                    "configuration-key" => h.SearchConfigurationKeys(query, limit),
                    "tile"              => h.SearchTiles(query, limit),
                    "workspace"         => h.SearchWorkspaces(query, limit),
                    "batch-jobs"        => h.FindBatchJobs(model),
                    _                   => h.SearchAny(query, limit),
                };
            }),

        new Descriptor("batch_get_info",
            "Fetch up to 10 objects in one call. Each spec is \"<kind>:<name>\" (kinds: class, table, edt, enum, form, menu-item, query, view, entity, report, service, service-group, role, duty, privilege). One failed lookup never fails the batch.",
            Schema(("objects", "array", true)),
            (h, p) => h.BatchGetInfo(StrArray(p, "objects"))),

        // ---- Object info ----

        new Descriptor("get_object_info",
            "Read one object's full metadata by `objectType`: table | class | edt | enum | form | query | view | " +
            "entity | report | service | service-group | menu-item | business-event. For tables, set exactly one of " +
            "`relations` | `methods` | `indexes` | `deleteActions` to return just that slice instead of the full table.",
            Schema(("objectType", "string", true), ("name", "string", true),
                   ("relations", "boolean", false), ("methods", "boolean", false),
                   ("indexes", "boolean", false), ("deleteActions", "boolean", false)),
            (h, p) =>
            {
                var name = Str(p, "name");
                return StrOr(p, "objectType", "").ToLowerInvariant() switch
                {
                    "table" => Bool(p, "relations")     ? h.GetTableRelations(name)
                             : Bool(p, "methods")       ? h.GetTableMethods(name)
                             : Bool(p, "indexes")       ? h.GetTableIndexes(name)
                             : Bool(p, "deleteActions") ? h.GetTableDeleteActions(name)
                             : h.GetTable(name),
                    "class"                   => h.GetClass(name),
                    "edt"                     => h.GetEdt(name),
                    "enum"                    => h.GetEnum(name),
                    "form"                    => h.GetForm(name),
                    "query"                   => h.GetQuery(name),
                    "view"                    => h.GetView(name),
                    "entity" or "data-entity" => h.GetDataEntity(name),
                    "report"                  => h.GetReport(name),
                    "service"                 => h.GetService(name),
                    "service-group"           => h.GetServiceGroup(name),
                    "menu-item"               => h.GetMenuItem(name),
                    "business-event"          => h.GetBusinessEvent(name),
                    _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                            $"Unknown objectType '{Str(p, "objectType")}'.",
                            "Use one of: table, class, edt, enum, form, query, view, entity, report, service, service-group, menu-item, business-event."),
                };
            }),

        new Descriptor("get_method",
            "Read X++ source from the index. `objectType` (class | table | form, default class), `name`, optional `method`. " +
            "`include` = signature | source | both (default both — signature is the cheap header for CoC planning). " +
            "Omit `method` to list every method's signature.",
            Schema(("objectType", "string", false), ("name", "string", true),
                   ("method", "string", false), ("include", "string", false)),
            (h, p) => h.ReadMethod(StrOr(p, "objectType", "class"), Str(p, "name"),
                StrOrNull(p, "method"), StrOr(p, "include", "both"))),

        // ---- Labels ----

        new Descriptor("labels",
            "Unified label operations via `action`: " +
            "search (substring across label files) · fts (ranked FTS5; supports phrases, NEAR, Value: filters) · " +
            "info (all translations of a token like @SYS12345, or one entry by file+language+key) · " +
            "resolve (alias of info) · create (write a key=value; needs file or installTo; " +
            "pass `labels:[{key,value}, …]` for a bulk create with shared top-level fields) · " +
            "rename (rename a key in place) · delete (remove a key). Values are sanitised unless raw=true.",
            Schema(("action", "string", true), ("query", "string", false), ("token", "string", false),
                   ("languages", "array", false), ("limit", "integer", false), ("raw", "boolean", false),
                   ("file", "string", false), ("language", "string", false), ("key", "string", false),
                   ("value", "string", false), ("overwrite", "boolean", false), ("labels", "array", false),
                   ("installTo", "string", false), ("lang", "string", false), ("labelFile", "string", false),
                   ("oldKey", "string", false), ("newKey", "string", false),
                   // create-input aliases tolerated for clients that guess the schema
                   ("labelId", "string", false), ("text", "string", false), ("label", "string", false),
                   ("model", "string", false), ("labelFileId", "string", false)),
            (h, p) => StrOr(p, "action", "search").ToLowerInvariant() switch
            {
                "fts"    => h.SearchLabelsFts(Str(p, "query"), StrArray(p, "languages"), Int(p, "limit", 100), Bool(p, "raw")),
                "info" or "resolve" => StrOrNull(p, "file") is not null
                            ? h.GetLabel(Str(p, "file"), Str(p, "language"), Str(p, "key"), Bool(p, "raw"))
                            : h.ResolveLabel(Str(p, "token"), StrArray(p, "languages"), Bool(p, "raw")),
                // Tolerate the param names MCP clients commonly guess: a scalar
                // text/label for value, model for installTo, language for lang,
                // labelFileId for labelFile. Canonical names still win when both
                // are present.
                // A `labels:[…]` array fans out as a bulk create (shared top-level
                // file/installTo/lang/labelFile/overwrite); otherwise a single create.
                "create" => LabelEntries(p) is { Count: > 0 } entries
                            ? h.CreateLabels(entries, StrOrNull(p, "file"), Bool(p, "overwrite"),
                                StrAliasOrNull(p, "installTo", "model"), StrAliasOrNull(p, "lang", "language"),
                                StrAliasOrNull(p, "labelFile", "labelFileId"))
                            : h.CreateLabel(StrOrNull(p, "file"),
                                StrAlias(p, "key", "labelId"), StrAlias(p, "value", "text", "label"),
                                Bool(p, "overwrite"),
                                StrAliasOrNull(p, "installTo", "model"), StrAliasOrNull(p, "lang", "language"),
                                StrAliasOrNull(p, "labelFile", "labelFileId")),
                "rename" => h.RenameLabel(Str(p, "file"), Str(p, "oldKey"), Str(p, "newKey"), Bool(p, "overwrite")),
                "delete" => h.DeleteLabel(Str(p, "file"), Str(p, "key")),
                _        => h.SearchLabels(Str(p, "query"), StrArray(p, "languages"), Int(p, "limit", 100), Bool(p, "raw")),
            }),

        // ---- Security ----

        new Descriptor("security_info",
            "Security lookup. `mode=artifact` returns a named role/duty/privilege (set `type` = role | duty | privilege, and `name`). " +
            "`mode=coverage` returns which roles → duties → privileges grant access to `object` (set `objectKind`, default Menuitem).",
            Schema(("mode", "string", true), ("type", "string", false), ("name", "string", false),
                   ("object", "string", false), ("objectKind", "string", false)),
            (h, p) => StrOr(p, "mode", "artifact").ToLowerInvariant() switch
            {
                "coverage" => h.GetSecurity(Str(p, "object"), StrOr(p, "objectKind", "Menuitem")),
                _ => StrOr(p, "type", "role").ToLowerInvariant() switch
                {
                    "duty"      => h.GetSecurityDuty(Str(p, "name")),
                    "privilege" => h.GetSecurityPrivilege(Str(p, "name")),
                    _           => h.GetSecurityRole(Str(p, "name")),
                },
            }),

        // ---- Extensions & handlers ----

        new Descriptor("extension_info",
            "D365FO extensibility analyzer. Pick a `mode`: " +
            "coc (Chain-of-Command extensions for `target` class, optionally scoped to `method`) · " +
            "events (DataEventHandler / SubscribesTo handlers bound to `target`; set `objectType` to its kind) · " +
            "table-merge (all TableExtensions targeting `target` table + effective merged schema) · " +
            "points (Table/Form/Enum/EDT _Extension objects targeting `target`; filter with `kind`) · " +
            "strategy (enumerate existing extensions/handlers/CoC on `target` and recommend the least-invasive change). " +
            "Use before writing any extension to check for conflicts and pick the right mechanism. " +
            "`target` may be the base object (CustTable) or the full extension name (CustTable.Extension) — both resolve to the base.",
            Schema(("mode", "string", true), ("target", "string", true),
                   ("method", "string", false), ("objectType", "string", false), ("kind", "string", false)),
            (h, p) => StrOr(p, "mode", "").ToLowerInvariant() switch
            {
                "coc"         => h.FindCoc(Str(p, "target"), StrOrNull(p, "method")),
                "events"      => h.FindEventSubscribers(Str(p, "target"), StrOrNull(p, "objectType")),
                "table-merge" => h.GetTableExtensionInfo(Str(p, "target")),
                "points"      => h.FindExtensions(Str(p, "target"), StrOrNull(p, "kind")),
                "strategy"    => h.AnalyzeExtensionPoints(Str(p, "target")),
                _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                        $"Unknown mode '{Str(p, "mode")}' for extension_info.",
                        "Use one of: coc, events, table-merge, points, strategy."),
            }),

        new Descriptor("find_references",
            "Reverse references: regex scan of indexed X++ source for where a symbol is used. " +
            "`kind` (class/table/form) and `model` narrow the scan; `limit` caps hits (default 200). " +
            "Returns the method + up to 3 sample lines per hit.",
            Schema(("name", "string", true), ("kind", "string", false), ("model", "string", false), ("limit", "integer", false)),
            (h, p) => h.FindReferences(Str(p, "name"), StrOrNull(p, "kind"), StrOrNull(p, "model"), Int(p, "limit", 200))),

        new Descriptor("find_tables_by_field",
            "Which tables declare a field named `name` (or whose field uses an EDT named `name`) — exact match. " +
            "Use this for 'which tables contain field X' questions. Do NOT use find_references or relation lookups for " +
            "this — those answer source-code usage / FK-relation targets, not a table's own field list, and will " +
            "return an inflated, semantically-wrong result set.",
            Schema(("name", "string", true), ("model", "string", false), ("limit", "integer", false)),
            (h, p) => h.FindTablesByField(Str(p, "name"), StrOrNull(p, "model"), Int(p, "limit", 200))),

        new Descriptor("validate_object_naming",
            "Static naming-rule check (PascalCase, length, character set, extension suffix, optional publisher prefix). No index access required.",
            Schema(("kind", "string", true), ("name", "string", true), ("prefix", "string", false)),
            (h, p) => h.ValidateObjectNaming(Str(p, "kind"), Str(p, "name"), StrOrNull(p, "prefix"))),

        new Descriptor("get_workspace_info",
            "Return the effective configuration in use (paths, custom-model patterns, label languages). Each D365FO_* key resolves via CLI flag → environment variable → settings.json → default.",
            Schema(),
            (h, _) => h.GetWorkspaceInfo()),

        // ---- Object patterns ----

        new Descriptor("object_patterns",
            "Pattern catalog + structural validator, selected by `domain` (default form). " +
            "`domain=form`: spec (catalog spec for a pattern/sub-pattern — versions, when-to-use, reference forms, " +
            "lifecycle; omit `name` to list all) · validate (structural validator FP001-FP010 over complete AxForm " +
            "`xml` — the same gate `generate_object(objectType=form)` enforces before writing). " +
            "`domain=table` is not backed by the C# index here — use `analyze(mode=integration)` or the CLI " +
            "`d365fo find form-patterns` for table/form mining.",
            Schema(("domain", "string", false), ("action", "string", true), ("name", "string", false), ("xml", "string", false)),
            (h, p) => StrOr(p, "domain", "form").ToLowerInvariant() switch
            {
                "form" => StrOr(p, "action", "spec").ToLowerInvariant() switch
                {
                    "validate" => h.ValidateFormPattern(Str(p, "xml")),
                    _          => h.GetFormPatternSpec(StrOrNull(p, "name")),
                },
                _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                        $"Unsupported domain '{Str(p, "domain")}' for object_patterns.",
                        "Only domain=form is index-backed here. Use analyze(mode=integration) or CLI 'd365fo find form-patterns' for table patterns."),
            }),

        // ---- Generation ----

        new Descriptor("generate_object",
            "Scaffold an AOT object from `objectType`. Two families share this one tool:\n" +
            "• WRITE to disk (requires `installTo` model name — resolves the path from the configured packages " +
            "paths — or `out` explicit path): table (pattern-aware, `fields` \"<name>:<edt>[:mandatory]\", " +
            "`pattern` main|transaction|parameter|group|reference|miscellaneous) · class (`extends`, `nonFinal`) · " +
            "coc (`target` + `methods`, writes <target>_Extension) · form (`table`, `pattern` SimpleList|" +
            "SimpleListDetails|DetailsMaster|DetailsTransaction|Dialog|TableOfContents|Lookup|ListPage|Workspace, " +
            "`caption`, `fields`, `linesTable`).\n" +
            "• XML-only, no file written (cloud/Linux friendly): edt (`extends`, `label`, `size`) · enum (`label`, " +
            "`values`) · query (`rootTable`, `label`) · sysoperation (`executionMode`; Contract+Service+Controller) · " +
            "business-event (`contractName`, `category`) · runbase (`batch`) · security-policy (`constrainedTable`, " +
            "`policyQuery`).",
            Schema(("objectType", "string", true), ("name", "string", false), ("label", "string", false),
                   ("fields", "array", false), ("pattern", "string", false),
                   ("extends", "string", false), ("nonFinal", "boolean", false),
                   ("target", "string", false), ("methods", "array", false),
                   ("table", "string", false), ("caption", "string", false), ("linesTable", "string", false),
                   ("size", "integer", false), ("values", "array", false),
                   ("rootTable", "string", false), ("executionMode", "string", false),
                   ("contractName", "string", false), ("category", "string", false), ("batch", "boolean", false),
                   ("constrainedTable", "string", false), ("policyQuery", "string", false),
                   ("installTo", "string", false), ("out", "string", false), ("overwrite", "boolean", false)),
            (h, p) => StrOr(p, "objectType", "").ToLowerInvariant() switch
            {
                // Write-to-disk objectTypes.
                "class" => h.GenerateClass(Str(p, "name"), StrOrNull(p, "extends"), Bool(p, "nonFinal"),
                            StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite")),
                "coc"   => h.GenerateCoc(Str(p, "target"), StrArray(p, "methods") ?? Array.Empty<string>(),
                            StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite")),
                "form"  => h.GenerateForm(Str(p, "name"), StrOrNull(p, "table"), StrOrNull(p, "pattern"),
                            StrOrNull(p, "caption"), StrArray(p, "fields"), StrOrNull(p, "linesTable"),
                            StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite")),
                "table" => h.GenerateTable(Str(p, "name"), StrOrNull(p, "label"), StrArray(p, "fields"),
                            StrOrNull(p, "pattern"), StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite")),
                // XML-only objectTypes.
                "edt"             => h.GenerateEdt(Str(p, "name"), StrOrNull(p, "extends"), StrOrNull(p, "label"), Int(p, "size", 0)),
                "enum"            => h.GenerateEnum(Str(p, "name"), StrOrNull(p, "label"), StrArray(p, "values")),
                "query"           => h.GenerateQuery(Str(p, "name"), Str(p, "rootTable"), StrOrNull(p, "label")),
                "sysoperation"    => h.GenerateSysOperation(Str(p, "name"), StrOr(p, "executionMode", "Synchronous")),
                "business-event"  => h.GenerateBusinessEvent(Str(p, "name"), StrOrNull(p, "contractName"), StrOr(p, "category", "Custom")),
                "runbase"         => h.GenerateRunBase(Str(p, "name"), Bool(p, "batch")),
                "security-policy" => h.GenerateSecurityPolicy(Str(p, "name"), Str(p, "constrainedTable"), StrOrNull(p, "policyQuery")),
                _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                        $"Unknown objectType '{Str(p, "objectType")}' for generate_object.",
                        "Write: table, class, coc, form. XML-only: edt, enum, query, sysoperation, business-event, runbase, security-policy."),
            }),

        new Descriptor("modify_method",
            "Replace the body of an EXISTING method on a live class/table/edt/form via D365FO.Bridge " +
            "(Windows VM, requires D365FO_BRIDGE_ENABLED=1). Reads the object through IMetadataProvider, " +
            "structurally replaces one <Method>'s source (never raw XML/CDATA string surgery), runs the same " +
            "reference/BP validation gate as generate_object and blocks the write on any error-severity finding " +
            "(unconditionally — not gated by D365FO_GROUNDING_ENFORCE), then writes back through the provider. " +
            "No on-disk fallback: fails BRIDGE_REQUIRED when the bridge is unavailable. Use generate_object to " +
            "create new objects/methods; use this only to change an existing method's body.",
            Schema(("kind", "string", true), ("name", "string", true), ("method", "string", true),
                   ("body", "string", true), ("model", "string", false), ("groundingToken", "string", false)),
            (h, p) => h.ModifyMethod(Str(p, "kind"), Str(p, "name"), Str(p, "method"), Str(p, "body"),
                        StrOrNull(p, "model"), StrOrNull(p, "groundingToken"))),

        new Descriptor("suggest_edt",
            "Suggest indexed EDTs for a field name using similarity heuristics. Returns confidence-ranked candidates.",
            Schema(("fieldName", "string", true), ("limit", "integer", false)),
            (h, p) => h.SuggestEdt(Str(p, "fieldName"), Int(p, "limit", 5))),

        // ---- Analysis ----

        new Descriptor("analyze",
            "Cross-index analysis via `mode`: integration (OData/DMF readiness of data entities — duplicate PublicEntityName, " +
            "missing staging table, zero-field entities) · impact (downstream consumers of `object`: CoC wrappers, event " +
            "handlers, extensions, form datasources, data entities, queries) · report (aggregated integration surface: OData " +
            "entities, custom services, business events, workflow types, batch jobs). `model` scopes integration/report.",
            Schema(("mode", "string", true), ("object", "string", false), ("model", "string", false)),
            (h, p) => StrOr(p, "mode", "integration").ToLowerInvariant() switch
            {
                "impact" => h.AnalyzeImpact(Str(p, "object")),
                "report" => h.ReportIntegrations(StrOrNull(p, "model")),
                _        => h.AnalyzeIntegration(StrOrNull(p, "model")),
            }),

        new Descriptor("prepare",
            "Single-round context aggregator that issues a 30-min grounding token. " +
            "`mode=change` (extend/modify an existing `object`): signature + CoC eligibility, existing wrappers, " +
            "strategy, naming check (`proposedName`/`prefix`), similar objects — set `method` for a specific method. " +
            "`mode=create` (new object `name` of `type`): collision check, naming, similar objects, EDT suggestions " +
            "for `fields[]`, reusable labels, mined property defaults.",
            Schema(("mode", "string", true), ("object", "string", false), ("name", "string", false),
                   ("type", "string", false), ("goal", "string", false), ("method", "string", false),
                   ("proposedName", "string", false), ("prefix", "string", false), ("fields", "array", false)),
            (h, p) => StrOr(p, "mode", "change").ToLowerInvariant() switch
            {
                "create" => h.PrepareCreate(StrOr(p, "name", Str(p, "object")), StrOr(p, "type", "class"),
                                StrOrNull(p, "goal"), StrArray(p, "fields"), StrOrNull(p, "prefix")),
                _        => h.PrepareChange(StrOr(p, "object", Str(p, "name")), StrOrNull(p, "goal"),
                                StrOrNull(p, "method"), StrOrNull(p, "type"), StrOrNull(p, "proposedName"), StrOrNull(p, "prefix")),
            }),

        new Descriptor("lint",
            "In-process Best-Practice gate. Categories: table-no-index, ext-named-not-attributed, string-without-edt.",
            Schema(("categories", "array", false), ("onlyCustomModels", "boolean", false)),
            (h, p) => h.Lint(StrArray(p, "categories"), Bool(p, "onlyCustomModels", true))),

        new Descriptor("stats",
            "Aggregate counters over the index: totals + top-N tables (by fields), top-N classes (by methods), top-N CoC targets, and per-model counts.",
            Schema(("topN", "integer", false)),
            (h, p) => h.Stats(Int(p, "topN", 10))),

        // ---- Models & index ----

        new Descriptor("models",
            "Model inspection via `action`: list (every indexed model — name/publisher/layer/custom) · " +
            "deps (dependsOn / dependedBy for `name`) · coupling (fan-in / fan-out / instability + dependency cycles; `topN`, `onlyCycles`).",
            Schema(("action", "string", true), ("name", "string", false), ("topN", "integer", false), ("onlyCycles", "boolean", false)),
            (h, p) => StrOr(p, "action", "list").ToLowerInvariant() switch
            {
                "deps"     => h.GetModelDependencies(Str(p, "name")),
                "coupling" => h.ModelsCoupling(Int(p, "topN", 20), Bool(p, "onlyCycles")),
                _          => h.ListModels(),
            }),

        new Descriptor("index_status",
            "Current row counts of every entity table.",
            Schema(),
            (h, _) => h.IndexStatus()),

        new Descriptor("index_history",
            "Recent ExtractionRuns telemetry (per-model timings). Returns newest first.",
            Schema(("limit", "integer", false), ("model", "string", false)),
            (h, p) => h.IndexHistory(Int(p, "limit", 50), Str(p, "model"))),

        // ---- Modification journal / undo (issue #113) ----

        new Descriptor("undo_last_modification",
            "Revert the last `steps` writes (default 1) made by `generate_object` / `labels` (create/rename/delete) / " +
            "`delete` — CLI parity for upstream `undo_last_modification`. Replays each entry in reverse through the " +
            "SAME write path that produced it (on-disk file, or the live metadata provider when the bridge was used), " +
            "restoring the exact pre-image: a create is removed, an update/delete is restored byte-for-byte. Stops at " +
            "the first failure so older entries are never skipped. Pass `dryRun=true` to preview without changing " +
            "anything — always do this first when unsure what will be reverted.",
            Schema(("steps", "integer", false), ("dryRun", "boolean", false)),
            (h, p) => h.UndoLastModification(Int(p, "steps", 1), Bool(p, "dryRun"))),

        new Descriptor("journal_list",
            "Inspect the modification-journal stack (most-recent-first) without reverting anything — " +
            "see what `undo_last_modification` would act on.",
            Schema(("limit", "integer", false)),
            (h, p) => h.JournalList(Int(p, "limit", 50))),
    };

    // ---- JSON helpers ----

    private static JsonObject Schema(params (string name, string type, bool required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (n, t, r) in props)
        {
            var node = new JsonObject { ["type"] = t };
            if (t == "array") node["items"] = new JsonObject { ["type"] = "string" };
            properties[n] = node;
            if (r) required.Add(n);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static string Str(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
            ? v.GetString() ?? "" : "";

    private static string StrOr(JsonElement p, string name, string dflt)
    {
        var s = Str(p, name);
        return string.IsNullOrEmpty(s) ? dflt : s;
    }

    private static string? StrOrNull(JsonElement p, string name)
    {
        var s = Str(p, name);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Read the first non-empty value among <paramref name="names"/>. Lets MCP
    /// clients that guess alias param names (e.g. <c>text</c> for <c>value</c>)
    /// still reach the handler; the canonical name (listed first) wins.
    /// </summary>
    private static string StrAlias(JsonElement p, params string[] names)
    {
        foreach (var n in names)
        {
            var s = Str(p, n);
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return "";
    }

    private static string? StrAliasOrNull(JsonElement p, params string[] names)
    {
        var s = StrAlias(p, names);
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static int Int(JsonElement p, string name, int dflt) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : dflt;

    private static bool Bool(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.True;

    private static bool Bool(JsonElement p, string name, bool dflt)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return dflt;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => dflt,
        };
    }

    private static string[]? StrArray(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                 .Select(x => x.GetString()!).ToArray();
    }

    /// <summary>
    /// Read a bulk-create <c>labels:[{key,value}, …]</c> array. Each entry tolerates the
    /// same key/value aliases as a single create (key|labelId, value|text|label). Entries
    /// missing a key are skipped here; an empty result falls back to single-create.
    /// </summary>
    private static List<(string key, string value)> LabelEntries(JsonElement p)
    {
        var list = new List<(string, string)>();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("labels", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var e in arr.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            var key = StrAlias(e, "key", "labelId");
            if (string.IsNullOrEmpty(key)) continue;
            list.Add((key, StrAlias(e, "value", "text", "label")));
        }
        return list;
    }
}
