using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Knowledge;

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
        "generate_object", "labels", "modify_method", "modify_object", "undo_last_modification",
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
            "entity | report | service | service-group | menu-item | business-event | security-policy. For tables, set exactly one of " +
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
                    "security-policy"         => h.GetSecurityPolicy(name),
                    _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                            $"Unknown objectType '{Str(p, "objectType")}'.",
                            "Use one of: table, class, edt, enum, form, query, view, entity, report, service, service-group, menu-item, business-event, security-policy."),
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
            "table-merge (all TableExtensions targeting `target` table + the effective merged schema: base fields, " +
            "indexes, relations and field groups with every extension folded in, each member labelled with the " +
            "object that contributes it; reports any extension whose file could not be read rather than dropping it) · " +
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

        new Descriptor("validate",
            "Check an artifact before it is written. `mode`:\n" +
            "• xpp — the offline X++/XML best-practice validator (SEL/COC/BP/FN/TTS/CS/ATTR/EXT/KW/RPT + " +
            "XML001-XML013), 40+ compiler-grounded rules. `codeType` " +
            "xpp | xml-table | xml-report | xml-any, auto-detected when omitted.\n" +
            "• references — every identifier in the code must exist in the index; the anti-hallucination gate. " +
            "Requires an index.\n" +
            "• form-pattern — structural AxForm pattern rules FP001-FP010, the same gate generate_object(form) enforces.\n" +
            "• metadata-shape — judges AOT XML against the serialization contract the AOT reader is generated from. " +
            "Inside the document: XML007 (a member the type does not declare, silently dropped on read) and XML008 " +
            "(a value outside its enum, which stops the read outright). The root itself: XML009 (names no AOT type), " +
            "XML010 (abstract root with no concrete i:type), XML011 (missing xmlns:i), XML012 (wrong contract " +
            "namespace). Every AOT family, not just tables. Needs no bridge and no VM.\n" +
            "\u2022 metadata \u2014 the provider's own verdict: round-trips `code` through Microsoft's " +
            "IMetadataProvider serializer and reports every member it drops on the way in. Nothing is written. " +
            "Requires D365FO_BRIDGE_ENABLED=1 on a machine with the metadata assemblies; without them it " +
            "returns skipped rather than a verdict it cannot support. `kind` hints the type when the root " +
            "element alone cannot resolve it.",
            Schema(("mode", "string", true), ("code", "string", true),
                   ("context", "string", false), ("codeType", "string", false), ("kind", "string", false)),
            (h, p) => StrOr(p, "mode", "").ToLowerInvariant() is "metadata" or "metadata-provider"
                      ? h.ValidateMetadata(StrOrNull(p, "kind"), Str(p, "code"))
                      : h.Validate(Str(p, "mode"), Str(p, "code"), StrOrNull(p, "context"), StrOrNull(p, "codeType"))),

        new Descriptor("validate_object_naming",
            "Static naming-rule check (PascalCase, length, character set, extension suffix, optional publisher prefix). No index access required.",
            Schema(("kind", "string", true), ("name", "string", true), ("prefix", "string", false)),
            (h, p) => h.ValidateObjectNaming(Str(p, "kind"), Str(p, "name"), StrOrNull(p, "prefix"))),

        new Descriptor("get_workspace_info",
            "Return the effective configuration in use (paths, custom-model patterns, label languages). Each "
            + "D365FO_* key resolves via CLI flag → environment variable → settings.json → default. "
            + "`changes=true` answers a different question with the same tool: what has changed in the working "
            + "tree (`git diff` over `repo`, default the current directory, from `baseRev` to `headRev`) plus a "
            + "cheap rule pass over the changed AOT XML — fields with no EDT or label, hard-coded strings, "
            + "dynamic query construction. Shallow on purpose: it tells you whether a build or a BP check is "
            + "worth its minutes, and says plainly when the directory is not a git work tree.",
            Schema(("changes", "boolean", false), ("repo", "string", false),
                   ("baseRev", "string", false), ("headRev", "string", false)),
            (h, p) => Bool(p, "changes")
                      ? D365FO.Core.Analysis.WorkspaceReview.Diff(
                            StrOrNull(p, "repo"), StrOr(p, "baseRev", "HEAD"), StrOrNull(p, "headRev"))
                      : h.GetWorkspaceInfo()),

        // ---- Knowledge ----

        new Descriptor("get_knowledge",
            "Verified X++/D365FO knowledge topics (the same corpus the d365fo skill files ship, checked against a " +
            "live D365FO dev VM). `action=list` returns the catalog with each topic's section count and token cost; " +
            "`action=get` returns one topic by `topic` id (`section` narrows to one '##' section, `outline=true` " +
            "returns only headings); `action=search` ranks sections across the corpus against a free-text `query`. " +
            "Prefer search → get(section) over fetching whole topics — a full topic can run to 2.5k tokens.\n" +
            "`kind=bp-moniker` switches to the Best-Practice rule catalog, whose names come from the AxRuleSet " +
            "files and BP rule assemblies of a real installation and are NEVER inferred: `action=validate` " +
            "(is `moniker` an exact, real rule? matched case-sensitively, as xppbp is — a case-only miss is " +
            "answered with the right casing) · `action=search` (which rule covers `query`; every word must appear " +
            "in the name, message or description, so a scenario can be described in words the rule name does not " +
            "contain; `canonicalOnly` drops the rule-assembly strings no rule set declares) · `action=suppress` " +
            "(render the `_BPSuppressions.xml` <Diagnostic> block for `moniker` at dynamics:// `path`, with " +
            "`justification`, `message` and `severity`; refused for a moniker the catalog does not know, because " +
            "a suppression naming a rule that does not exist suppresses nothing while looking deliberate).",
            Schema(("kind", "string", false), ("action", "string", false), ("topic", "string", false),
                   ("section", "string", false), ("query", "string", false), ("limit", "integer", false),
                   ("outline", "boolean", false), ("moniker", "string", false), ("path", "string", false),
                   ("justification", "string", false), ("message", "string", false),
                   ("severity", "string", false), ("canonicalOnly", "boolean", false)),
            (h, p) => StrOr(p, "kind", "knowledge").ToLowerInvariant() switch
            {
                "bp-moniker" or "bp" => StrOr(p, "action", "validate").ToLowerInvariant() switch
                {
                    "search"   => BpMonikerAnswers.Search(Str(p, "query"), Int(p, "limit", 20), Bool(p, "canonicalOnly")),
                    "suppress" => BpMonikerAnswers.Suppress(Str(p, "moniker"), StrOrNull(p, "path"),
                                    StrOrNull(p, "message"), StrOrNull(p, "justification"),
                                    StrOr(p, "severity", "Warning")),
                    _          => BpMonikerAnswers.Validate(Str(p, "moniker")),
                },
                _ => h.GetKnowledge(StrOr(p, "action", "list"), StrOrNull(p, "topic"), StrOrNull(p, "section"),
                                    StrOrNull(p, "query"), Int(p, "limit", 10), Bool(p, "outline")),
            }),

        new Descriptor("explain_build_error",
            "Score xppc / MSBuild output against the fix-hint rules and return ranked, machine-identified causes " +
            "(rule id + fix + the `get_knowledge` topic behind it). Offline — needs no VM or index, so it works on " +
            "a log pasted by the user. Pass the whole log; structured `dynamics://` lines are parsed per-diagnostic, " +
            "a bare message is scored as-is. Messages matching no rule are returned verbatim rather than guessed at.",
            Schema(("log", "string", true), ("all", "boolean", false)),
            (h, p) => h.ExplainBuildError(Str(p, "log"), Bool(p, "all"))),

        // ---- Object patterns ----

        new Descriptor("object_patterns",
            "Pattern catalog + structural validator, selected by `domain` (default form). " +
            "`domain=form`: spec (catalog spec for a pattern/sub-pattern — versions, when-to-use, reference forms, " +
            "lifecycle; omit `name` to list all) · validate (structural validator FP001-FP010 over complete AxForm " +
            "`xml` — the same gate `generate_object(objectType=form)` enforces before writing) · " +
            "repair (apply the deterministic fixes for those violations — missing required controls, control order, " +
            "PatternVersion, pattern-default properties, unambiguous sub-patterns — and return the repaired `xml` " +
            "plus what it refused to change; pass `name` to adopt an unpatterned form into a pattern) · " +
            "analyze (mine the INDEXED forms rather than the catalog: no filter returns the pattern histogram, " +
            "`pattern`/`table` filter it, `similarTo` returns the peers of one form — the reference forms worth " +
            "cloning). " +
            "`domain=table`: list (every pattern with its TableGroup, when to use it, its default fields, and the " +
            "storage choices) · spec (one pattern by `name`, including the scaffold call that produces it). " +
            "`domain=report`: list · spec — the seven SSRS shapes as recipes; there is no pattern XML to validate " +
            "a report against, so each is an object roster, base classes, one scaffold call and the checks to run. " +
            "`domain=mobile-app`: list (leads with the framework decision — ProcessGuide vs the legacy " +
            "WHSWorkExecuteDisplay hierarchy, which is a rewrite to get wrong; `framework` filters) · spec — " +
            "warehouse scanner screens.",
            Schema(("domain", "string", false), ("action", "string", true), ("name", "string", false),
                   ("xml", "string", false), ("pattern", "string", false), ("table", "string", false),
                   ("similarTo", "string", false), ("model", "string", false), ("framework", "string", false),
                   ("limit", "integer", false)),
            (h, p) => StrOr(p, "domain", "form").ToLowerInvariant() switch
            {
                "form" => StrOr(p, "action", "spec").ToLowerInvariant() switch
                {
                    "validate" => h.ValidateFormPattern(Str(p, "xml")),
                    "repair"   => h.RepairFormPattern(Str(p, "xml"), StrOrNull(p, "name")),
                    "analyze"  => h.AnalyzeFormPatterns(StrOrNull(p, "pattern"), StrOrNull(p, "table"),
                                    StrOrNull(p, "similarTo"), StrOrNull(p, "model"), Int(p, "limit", 50)),
                    _          => h.GetFormPatternSpec(StrOrNull(p, "name")),
                },
                "table" => StrOr(p, "action", "list").ToLowerInvariant() switch
                {
                    "spec" => PatternCatalogAnswers.TableSpec(StrOr(p, "name", Str(p, "pattern"))),
                    _      => PatternCatalogAnswers.TableList(),
                },
                "report" => StrOr(p, "action", "list").ToLowerInvariant() switch
                {
                    "spec" => PatternCatalogAnswers.ReportSpec(StrOr(p, "name", Str(p, "pattern"))),
                    _      => PatternCatalogAnswers.ReportList(),
                },
                "mobile-app" or "mobile" => StrOr(p, "action", "list").ToLowerInvariant() switch
                {
                    "spec" => PatternCatalogAnswers.MobileSpec(StrOr(p, "name", Str(p, "pattern"))),
                    _      => PatternCatalogAnswers.MobileList(StrOrNull(p, "framework")),
                },
                _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                        $"Unsupported domain '{Str(p, "domain")}' for object_patterns.",
                        "Use one of: form, table, report, mobile-app."),
            }),

        // ---- Generation ----

        new Descriptor("generate_object",
            "Scaffold an AOT object from `objectType`. Every objectType that WRITES runs the same grounding "
            + "gate the CLI runs: the generated X++ is proved against the index and checked by the offline BP "
            + "validator, findings come back in `grounding`, and under D365FO_GROUNDING_ENFORCE=true a write "
            + "needs a valid `groundingToken` (from `prepare`, bound to this object) and is refused when an "
            + "identifier cannot be proved. Two families share this one tool:\n" +
            "• WRITE to disk (requires `installTo` model name — resolves the path from the configured packages " +
            "paths — or `out` explicit path): table (pattern-aware, `fields` \"<name>:<edt>[:mandatory]\", " +
            "`pattern` main|transaction|parameter|group|reference|miscellaneous) · class (`extends`, `nonFinal`) · " +
            "coc (`target` + `methods`, writes <target>_Extension) · form (`table`, `pattern` SimpleList|" +
            "SimpleListDetails|DetailsMaster|DetailsTransaction|Dialog|TableOfContents|Lookup|ListPage|Workspace, " +
            "`caption`, `fields`, `linesTable`).\n" +
            "• XML-only, no file written (cloud/Linux friendly): edt (`extends`, `label`, `size`) · enum (`label`, " +
            "`values`) · query (`rootTable`, `label`) · sysoperation (`executionMode`; Contract+Service+Controller) · " +
            "business-event (`contractName`, `category`) · runbase (`batch`) · security-policy (`constrainedTable`, " +
            "`policyQuery`) · menu-item (`objectName`, `menuKind` Display|Action|Output, `objectTypeTarget` " +
            "Form|Class|Report|Query, `neededPermission`) · privilege (`entryPoint` + `entryKind`, or `dataEntity`; " +
            "`access` Read|Update|Create|Correct|Delete) · duty (`privileges`) · role (`duties`, `privileges`) · " +
            "entity (`table`, `fields` \"<entityField>[:<tableField>]\", `entityCategory`) · extension " +
            "(`extensionKind` table|form|edt|enum|view|query|dataEntityView, `target`, `suffix`) · " +
            "event-handler (`sourceKind` Form|FormDataSource|FormControl|Table|Class, `sourceObject`, `event`, " +
            "`method`) · view (`query` — a view projects an AxQuery — plus `fields` \"<name>:<dataSource>" +
            "[:<dataField>]\" and `computed` \"<name>:<viewMethod>:<type>\", `configurationKey`) · map " +
            "(`fields` \"<name>:<edt>[:<label>]\", `mapTo` \"<table>[:<mapField>=<tableField>,…]\") · systest " +
            "(`targetClass` or `targetTable`, `methods`, `dataAreaId`, `atl` — the scaffold fails on purpose so " +
            "the first run is red) · migration-script (`sourceTable`, `targetTable`, `mode` insert|update|upsert, " +
            "`batchSize`) · custom-service (`className`, `externalName`, `groupName`, `operationSpecs` " +
            "\"<name>[:<returnType>]\", `contractParam`; returns class + AxService + AxServiceGroup) · " +
            "number-sequence (`name` is the module, `edt`, `scope` company|shared, `table` to also get the form " +
            "handler) · workflow (`table`, `approvalName`, `taskName`, `category`, `documentMenuItem`, " +
            "`submitMenuItem`, `documentClass`, `query`) · report (the whole SSRS stack — AxReport + DP + " +
            "contract + TmpTable + controller; `dpClass`, `tmpTable`, `datasetName`, `fields`, `parameters` " +
            "\"<name>[:<dataType>]\", `preProcess`, `controllerType` standard|print-mgmt, `uiBuilder`. The RDL " +
            "design is authored in Visual Studio — nothing here produces it) · report-extension (`pattern` " +
            "dataset|custom-design|menu-redirect, with `dpClass`+`tmpTable`, or `report`+`design`, or " +
            "`controller`+`report`+`design`) · find-methods (`table` — static find/exists/findRecId keyed on its " +
            "unique index; returns method bodies to merge, not a document) · table-relation (`table`, `fields` — " +
            "AxTableRelation fragments derived from the EDTs the fields use) · form-clone (`from` an indexed form " +
            "name or an XML path, `rebind` \"<OldTable>=<NewTable>\") · datasource-method / control-method " +
            "(`form` plus `dataSource`/`control`; omit `method` to list what is overridable, pass it to get the " +
            "whole form back with the method injected).",
            Schema(("objectType", "string", true), ("name", "string", false), ("label", "string", false),
                   ("fields", "array", false), ("pattern", "string", false),
                   ("extends", "string", false), ("nonFinal", "boolean", false),
                   ("target", "string", false), ("methods", "array", false),
                   ("table", "string", false), ("caption", "string", false), ("linesTable", "string", false),
                   ("size", "integer", false), ("values", "array", false),
                   ("rootTable", "string", false), ("executionMode", "string", false),
                   ("contractName", "string", false), ("category", "string", false), ("batch", "boolean", false),
                   ("constrainedTable", "string", false), ("policyQuery", "string", false),
                   ("objectName", "string", false), ("menuKind", "string", false), ("objectTypeTarget", "string", false),
                   ("neededPermission", "string", false), ("entryPoint", "string", false), ("entryKind", "string", false),
                   ("access", "string", false), ("dataEntity", "string", false), ("privileges", "array", false),
                   ("duties", "array", false), ("entityCategory", "string", false),
                   ("extensionKind", "string", false), ("suffix", "string", false),
                   ("sourceKind", "string", false), ("sourceObject", "string", false), ("event", "string", false),
                   ("method", "string", false), ("computed", "array", false), ("configurationKey", "string", false),
                   ("mapTo", "array", false), ("dataAreaId", "string", false), ("atl", "boolean", false),
                   ("targetClass", "string", false), ("targetTable", "string", false), ("methods", "array", false),
                   ("sourceTable", "string", false), ("mode", "string", false), ("batchSize", "integer", false),
                   ("className", "string", false), ("externalName", "string", false), ("groupName", "string", false),
                   ("operationSpecs", "array", false), ("contractParam", "string", false),
                   ("edt", "string", false), ("edtLabel", "string", false), ("scope", "string", false),
                   ("approvalName", "string", false), ("taskName", "string", false),
                   ("documentMenuItem", "string", false), ("submitMenuItem", "string", false),
                   ("documentClass", "string", false), ("query", "string", false),
                   ("dpClass", "string", false), ("tmpTable", "string", false), ("datasetName", "string", false),
                   ("parameters", "array", false), ("preProcess", "boolean", false),
                   ("controllerType", "string", false), ("uiBuilder", "boolean", false),
                   ("datasetAccessor", "string", false), ("report", "string", false), ("design", "string", false),
                   ("documentType", "string", false), ("baseController", "string", false),
                   ("controller", "string", false), ("keys", "array", false),
                   ("noExists", "boolean", false), ("noFindRecId", "boolean", false),
                   ("from", "string", false), ("rebind", "array", false), ("form", "string", false),
                   ("dataSource", "string", false), ("control", "string", false), ("returnType", "string", false),
                   ("body", "string", false),
                   ("installTo", "string", false), ("out", "string", false), ("overwrite", "boolean", false),
                   ("groundingToken", "string", false)),
            (h, p) => StrOr(p, "objectType", "").ToLowerInvariant() switch
            {
                // Write-to-disk objectTypes.
                "class" => h.GenerateClass(Str(p, "name"), StrOrNull(p, "extends"), Bool(p, "nonFinal"),
                            StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite"),
                            StrOrNull(p, "groundingToken")),
                "coc"   => h.GenerateCoc(Str(p, "target"), StrArray(p, "methods") ?? Array.Empty<string>(),
                            StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite"),
                            StrOrNull(p, "groundingToken")),
                "form"  => h.GenerateForm(Str(p, "name"), StrOrNull(p, "table"), StrOrNull(p, "pattern"),
                            StrOrNull(p, "caption"), StrArray(p, "fields"), StrOrNull(p, "linesTable"),
                            StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite"),
                            StrOrNull(p, "groundingToken")),
                "table" => h.GenerateTable(Str(p, "name"), StrOrNull(p, "label"), StrArray(p, "fields"),
                            StrOrNull(p, "pattern"), StrOrNull(p, "installTo"), StrOrNull(p, "out"), Bool(p, "overwrite"),
                            StrOrNull(p, "groundingToken")),
                // XML-only objectTypes.
                "edt"             => h.GenerateEdt(Str(p, "name"), StrOrNull(p, "extends"), StrOrNull(p, "label"), Int(p, "size", 0)),
                "enum"            => h.GenerateEnum(Str(p, "name"), StrOrNull(p, "label"), StrArray(p, "values")),
                "query"           => h.GenerateQuery(Str(p, "name"), Str(p, "rootTable"), StrOrNull(p, "label")),
                "sysoperation"    => h.GenerateSysOperation(Str(p, "name"), StrOr(p, "executionMode", "Synchronous")),
                "business-event"  => h.GenerateBusinessEvent(Str(p, "name"), StrOrNull(p, "contractName"), StrOr(p, "category", "Custom")),
                "runbase"         => h.GenerateRunBase(Str(p, "name"), Bool(p, "batch")),
                "security-policy" => h.GenerateSecurityPolicy(Str(p, "name"), Str(p, "constrainedTable"), StrOrNull(p, "policyQuery")),
                "menu-item"       => h.GenerateMenuItem(Str(p, "name"), Str(p, "objectName"), StrOr(p, "menuKind", "Display"),
                                        StrOr(p, "objectTypeTarget", "Form"), StrOrNull(p, "label"), StrOrNull(p, "neededPermission")),
                "privilege"       => h.GeneratePrivilege(Str(p, "name"), StrOrNull(p, "entryPoint"), StrOrNull(p, "entryKind"),
                                        StrOrNull(p, "access"), StrOrNull(p, "label"), StrOrNull(p, "dataEntity")),
                "duty"            => h.GenerateDuty(Str(p, "name"), StrArray(p, "privileges"), StrOrNull(p, "label")),
                "role"            => h.GenerateRole(Str(p, "name"), StrArray(p, "duties"), StrArray(p, "privileges"), StrOrNull(p, "label")),
                "entity"          => h.GenerateEntity(Str(p, "name"), Str(p, "table"), StrArray(p, "fields"), StrOrNull(p, "entityCategory")),
                "extension"       => h.GenerateExtension(Str(p, "extensionKind"), Str(p, "target"), StrOrNull(p, "suffix")),
                "event-handler"   => h.GenerateEventHandler(Str(p, "name"), StrOrNull(p, "sourceKind"),
                                        StrOrNull(p, "sourceObject"), StrOrNull(p, "event"), StrOrNull(p, "method")),
                "view"            => h.GenerateView(Str(p, "name"), StrOrNull(p, "query"), StrArray(p, "fields"),
                                        StrArray(p, "computed"), StrOrNull(p, "label"), StrOrNull(p, "configurationKey")),
                "map"             => h.GenerateMap(Str(p, "name"), StrArray(p, "fields"), StrArray(p, "mapTo"), StrOrNull(p, "label")),
                "systest"         => h.GenerateSysTest(Str(p, "name"), StrOrNull(p, "dataAreaId"), Bool(p, "atl"),
                                        StrOrNull(p, "targetClass"), StrOrNull(p, "targetTable"), StrArray(p, "methods")),
                "migration-script" => h.GenerateMigrationScript(Str(p, "name"), StrOrNull(p, "sourceTable"),
                                        StrOrNull(p, "targetTable"), StrOrNull(p, "mode"), Int(p, "batchSize", 0)),
                "custom-service"  => h.GenerateCustomService(Str(p, "name"), StrOrNull(p, "className"),
                                        StrOrNull(p, "externalName"), StrOrNull(p, "groupName"),
                                        StrArray(p, "operationSpecs"), StrOrNull(p, "contractParam")),
                "number-sequence" => h.GenerateNumberSequence(Str(p, "name"), StrOrNull(p, "edt"), StrOrNull(p, "edtLabel"),
                                        StrOrNull(p, "scope"), StrOrNull(p, "table")),
                "workflow"        => h.GenerateWorkflow(Str(p, "name"), StrOrNull(p, "table"), StrOrNull(p, "approvalName"),
                                        StrOrNull(p, "taskName"), StrOrNull(p, "category"), StrOrNull(p, "documentMenuItem"),
                                        StrOrNull(p, "submitMenuItem"), StrOrNull(p, "documentClass"), StrOrNull(p, "query")),
                "report"          => h.GenerateReport(Str(p, "name"), StrOrNull(p, "dpClass"), StrOrNull(p, "tmpTable"),
                                        StrOrNull(p, "datasetName"), StrOrNull(p, "caption"), StrArray(p, "fields"),
                                        StrArray(p, "parameters"), Bool(p, "preProcess"), StrOrNull(p, "controllerType"),
                                        Bool(p, "uiBuilder")),
                "report-extension" => h.GenerateReportExtension(StrOr(p, "pattern", ""), StrOrNull(p, "dpClass"),
                                        StrOrNull(p, "tmpTable"), StrOrNull(p, "datasetAccessor"), StrOrNull(p, "report"),
                                        StrOrNull(p, "design"), StrOrNull(p, "documentType"), StrOrNull(p, "baseController"),
                                        StrOrNull(p, "controller"), StrOrNull(p, "suffix")),
                "find-methods"    => h.GenerateFindMethods(StrOr(p, "table", Str(p, "name")), StrArray(p, "keys"),
                                        !Bool(p, "noExists"), !Bool(p, "noFindRecId")),
                "table-relation"  => h.GenerateTableRelation(StrOr(p, "table", Str(p, "name")), StrArray(p, "fields")),
                "form-clone"      => h.GenerateFormClone(Str(p, "name"), StrOrNull(p, "from"), StrArray(p, "rebind")),
                "datasource-method" => h.GenerateFormMethod(StrOr(p, "form", Str(p, "name")), StrOrNull(p, "dataSource"),
                                        null, StrOrNull(p, "method"), StrOrNull(p, "returnType"), StrOrNull(p, "body")),
                "control-method"  => h.GenerateFormMethod(StrOr(p, "form", Str(p, "name")), null, StrOrNull(p, "control"),
                                        StrOrNull(p, "method"), StrOrNull(p, "returnType"), StrOrNull(p, "body")),
                _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                        $"Unknown objectType '{Str(p, "objectType")}' for generate_object.",
                        "Write: table, class, coc, form. XML-only: edt, enum, query, sysoperation, business-event, runbase, "
                        + "security-policy, menu-item, privilege, duty, role, entity, extension, event-handler, view, map, "
                        + "systest, migration-script, custom-service, number-sequence, workflow, report, report-extension, "
                        + "find-methods, table-relation, form-clone, datasource-method, control-method."),
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

        new Descriptor("modify_object",
            "Structured edits to an EXISTING object beyond its method bodies, via D365FO.Bridge (Windows VM, " +
            "requires D365FO_BRIDGE_ENABLED=1). `action` names the edit — the same twenty the CLI's `d365fo " +
            "modify` sub-commands carry:\n" +
            "• property — set `member` (Label, ConfigurationKey, TableGroup, …) to `value` on any kind\n" +
            "• add-field / remove-field / rename-field — a field on table `name`; on add, `type` is the EDT and " +
            "decides the concrete AxTableField subtype (`label`, `mandatory` optional); on rename, `newName`\n" +
            "• add-index / remove-index — an index `member` over `fields` (order is the key order); unique " +
            "unless `allowDuplicates`, `alternateKey` marks it an alternate key\n" +
            "• add-relation / remove-relation — a foreign key `member` to `relatedTable`.`relatedField`\n" +
            "• add-field-group / remove-field-group — a field group `member` over `fields`\n" +
            "• add-delete-action / remove-delete-action — `relatedTable` governed with `deleteAction` " +
            "(Cascade | Restricted | CascadeRestricted | None); the remove form is addressed by that table\n" +
            "• add-enum-value / remove-enum-value — a value `member` on base enum `name` (positional; never " +
            "writes an ordinal, which is what breaks when another model inserts a value ahead of it)\n" +
            "• add-control / remove-control — control `member` of `type` (Grid, Group, TabPage, String, …) on " +
            "form `name`, optionally inside `parent` and bound to `dataSource`/`dataField`; remove takes " +
            "everything nested under it too\n" +
            "• add-query-range / remove-query-range — a range on field `member` of query `name`, in " +
            "`dataSourceName` (optional when the query has one datasource); `rangeValue` is the range " +
            "EXPRESSION, and empty is legal — that is a range whose value is set at run time\n" +
            "• add-entry-point / remove-entry-point — grant/revoke `member` on security privilege `name`, " +
            "with `entryPointType` (MenuItemDisplay, Form, …) and `access` (Read | Update | Create | Correct | " +
            "Delete | Invoke)\n" +
            "BATCHING: `operations: [{operation, member, …}]` applies several edits to the SAME object in one " +
            "read-edit-write — one bridge round trip, one journal entry, and no intermediate state published " +
            "(a table is never briefly on disk carrying a field no index covers). Steps inherit `kind`, `name` " +
            "and `model`; a step that refuses discards the whole batch with nothing written.\n" +
            "EXTENSION FALLBACK: when the object's model is not in D365FO_CUSTOM_MODELS the edit is written to " +
            "the <Target>.<Suffix> extension in a custom model instead of the object itself, creating that " +
            "extension if needed, and says so in `warnings`. Force it with `extensionSuffix`, or refuse the " +
            "in-place path entirely with `requireExtension`. Every write is journaled — revert with " +
            "undo_last_modification. No on-disk fallback: fails BRIDGE_REQUIRED when the bridge is unavailable.",
            Schema(("action", "string", false), ("kind", "string", true), ("name", "string", true),
                   ("member", "string", false), ("value", "string", false), ("type", "string", false),
                   ("label", "string", false), ("mandatory", "boolean", false), ("parent", "string", false),
                   ("dataSource", "string", false), ("dataField", "string", false), ("model", "string", false),
                   ("fields", "array", false), ("relatedTable", "string", false), ("relatedField", "string", false),
                   ("deleteAction", "string", false), ("allowDuplicates", "boolean", false),
                   ("alternateKey", "boolean", false), ("newName", "string", false),
                   ("dataSourceName", "string", false), ("rangeValue", "string", false),
                   ("entryPointType", "string", false), ("access", "string", false),
                   ("operations", "object-array", false),
                   ("extensionSuffix", "string", false), ("extensionModel", "string", false),
                   ("requireExtension", "boolean", false)),
            ModifyObjectCall),

        new Descriptor("suggest_edt",
            "Suggest indexed EDTs for a field name using similarity heuristics. Returns confidence-ranked candidates.",
            Schema(("fieldName", "string", true), ("limit", "integer", false)),
            (h, p) => h.SuggestEdt(Str(p, "fieldName"), Int(p, "limit", 5))),

        // ---- Analysis ----

        new Descriptor("analyze",
            "Cross-index analysis via `mode`: integration (OData/DMF readiness of data entities — duplicate PublicEntityName, " +
            "missing staging table, zero-field entities) · impact (downstream consumers of `object`: CoC wrappers, event " +
            "handlers, extensions, form datasources, data entities, queries) · report (aggregated integration surface: OData " +
            "entities, custom services, business events, workflow types, batch jobs) · completeness (walk `path` — a model " +
            "folder, a PackagesLocalDirectory or one AOT XML file — and report references that resolve to nothing: " +
            "MISSING_DUTY, MISSING_PRIVILEGE, MISSING_EDT, MISSING_LABEL; narrow with `skipLabels`/`skipEdts`/" +
            "`skipSecurity`. This is the only mode that reads the working tree, so it needs a local path). " +
            "`model` scopes integration/report.",
            Schema(("mode", "string", true), ("object", "string", false), ("model", "string", false),
                   ("path", "string", false), ("skipLabels", "boolean", false),
                   ("skipEdts", "boolean", false), ("skipSecurity", "boolean", false)),
            (h, p) => StrOr(p, "mode", "integration").ToLowerInvariant() switch
            {
                "impact" => h.AnalyzeImpact(Str(p, "object")),
                "report" => h.ReportIntegrations(StrOrNull(p, "model")),
                "completeness" => h.AnalyzeCompleteness(Str(p, "path"), Bool(p, "skipLabels"),
                                    Bool(p, "skipEdts"), Bool(p, "skipSecurity")),
                _        => h.AnalyzeIntegration(StrOrNull(p, "model")),
            }),

        new Descriptor("prepare",
            "Single-round context aggregator that issues a 30-min grounding token. " +
            "`mode=change` (extend/modify an existing `object`): signature + CoC eligibility, existing wrappers, " +
            "strategy, naming check (`proposedName`/`prefix`), similar objects — set `method` for a specific method. " +
            "`mode=create` (new object `name` of `type`): collision check, naming, similar objects, EDT suggestions " +
            "for `fields[]`, reusable labels, mined property defaults. " +
            "`mode=test` (SysTest for class `object`): methods worth covering with real signatures, test classes " +
            "already covering the target, TestEssentials reference check (`modelName`), the scaffold call and the " +
            "red-first cycle.",
            Schema(("mode", "string", true), ("object", "string", false), ("name", "string", false),
                   ("type", "string", false), ("goal", "string", false), ("method", "string", false),
                   ("proposedName", "string", false), ("prefix", "string", false), ("fields", "array", false),
                   ("modelName", "string", false)),
            (h, p) => StrOr(p, "mode", "change").ToLowerInvariant() switch
            {
                "create" => h.PrepareCreate(StrOr(p, "name", Str(p, "object")), StrOr(p, "type", "class"),
                                StrOrNull(p, "goal"), StrArray(p, "fields"), StrOrNull(p, "prefix")),
                "test"   => h.PrepareTest(StrOr(p, "object", Str(p, "name")), StrOrNull(p, "goal"),
                                StrOrNull(p, "method"), StrOrNull(p, "modelName")),
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

        new Descriptor("index_sync",
            "Re-index ONE model after an edit made outside this server — in Visual Studio, by a git pull, by "
            + "a colleague. Name the `model`, or pass a `path` to any file inside it and the model is read off "
            + "the packages layout. Writes through `generate_object` / `modify_object` already refresh the "
            + "index, so this is for changes this process did not make.\n"
            + "A model is the unit, not a file: the index writer replaces a model's rows atomically, which is "
            + "what keeps re-extraction idempotent — handing it one object would delete the rest of that model. "
            + "A custom model takes seconds; naming a large standard model is a minutes-long call, and the "
            + "result reports how long it took. Re-indexing EVERYTHING is `d365fo index refresh` on a shell: "
            + "it walks every package, which is not something to wait on in a tool call.",
            Schema(("model", "string", false), ("path", "string", false),
                   ("packagesPath", "string", false), ("indexSource", "boolean", false)),
            (h, p) => D365FO.Core.Index.IndexSync.Sync(
                StrOrNull(p, "model"), StrOrNull(p, "path"), StrOrNull(p, "packagesPath"),
                databasePath: null, indexSource: Bool(p, "indexSource"))),

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

        // ---- SDLC (Windows D365FO VM only) ----

        new Descriptor("sdlc",
            "Run the Windows-only D365FO developer tools and read their output as structured "
            + "results. `action`:\n"
            + "• build — MSBuild over `project` (`configuration`, default Debug; `msbuild` to "
            + "override the executable). Returns per-diagnostic X++ compiler findings — object, "
            + "member, line, column, message and a fix hint — not a log tail, and says when xppc "
            + "reports stale symbols from a previous incremental build (which needs a Full Build, "
            + "not a retry). Pass `xppcLog` to also parse Dynamics.AX.<Model>.xppc.log. A failed "
            + "build still returns its diagnostics; the failure is reported as a `build-failed` "
            + "warning rather than an error envelope that would throw them away.\n"
            + "• sync — database synchronisation (SyncEngine.exe); `full` for a full sync rather "
            + "than the partial list.\n"
            + "• test — SysTestConsole.exe over `testClasses`, with `granularity` "
            + "(Default|UnitTest|ScenarioTest), `parallel`, and `resultsPath` for the runner's XML "
            + "result document. Ask for the results document: the verdict comes from it, not from "
            + "the exit code — a run that dies half way still exits 0 with its remaining cases "
            + "marked pending.\n"
            + "• bp-check — Microsoft Best Practices (xppbp.exe) over `model`; `packagesPath` / "
            + "`metadataPath` for a UDE layout where the framework and the model store are "
            + "different directories.\n"
            + "Every action needs a Windows D365FO VM and refuses elsewhere with "
            + "UNSUPPORTED_PLATFORM. This is the answer to \"does what I just wrote compile?\", "
            + "which is the question that follows every write.",
            Schema(("action", "string", true), ("project", "string", false), ("configuration", "string", false),
                   ("msbuild", "string", false), ("xppcLog", "string", false),
                   ("full", "boolean", false), ("tool", "string", false),
                   ("testClasses", "array", false), ("granularity", "string", false),
                   ("resultsPath", "string", false), ("parallel", "boolean", false),
                   ("model", "string", false), ("packagesPath", "string", false),
                   ("metadataPath", "string", false)),
            (h, p) =>
            {
                var action = StrOr(p, "action", "").ToLowerInvariant();
                var guard = D365FO.Core.Ops.SdlcRunner.WindowsGuard($"sdlc(action={action})");
                if (guard is not null) return guard;

                return action switch
                {
                    "build" => D365FO.Core.Ops.SdlcRunner.Build(
                        StrOrNull(p, "msbuild"), StrOrNull(p, "project"),
                        StrOr(p, "configuration", "Debug"), StrOrNull(p, "xppcLog")),
                    "sync" or "db-sync" => D365FO.Core.Ops.SdlcRunner.Sync(StrOrNull(p, "tool"), Bool(p, "full")),
                    "test" or "systest" => D365FO.Core.Ops.SdlcRunner.RunTests(
                        StrOrNull(p, "tool"), StrArray(p, "testClasses"), StrOrNull(p, "granularity"),
                        StrOrNull(p, "resultsPath"), Bool(p, "parallel")).Result,
                    "bp-check" or "bp" => D365FO.Core.Ops.SdlcRunner.BpCheck(
                        StrOrNull(p, "model"), StrOrNull(p, "tool"),
                        StrOrNull(p, "packagesPath"), StrOrNull(p, "metadataPath")),
                    _ => D365FO.Core.ToolResult<object>.Fail("BAD_INPUT",
                            $"Unknown action '{Str(p, "action")}' for sdlc.",
                            "Use one of: build, sync, test, bp-check."),
                };
            }),

        new Descriptor("delete_object",
            "Remove an AOT object and journal the removal so `undo_last_modification` can put it back. "
            + "Name `kind` and `name`, then exactly one of: `installTo` (the model — deletes through the live "
            + "metadata provider, requires D365FO_BRIDGE_ENABLED=1) or `path` (deletes that XML file, `model` "
            + "optional for the journal entry). The pre-image is captured BEFORE the delete and a delete that "
            + "cannot capture one is refused — a deletion nothing can undo is not one to make by accident. "
            + "The index is NOT refreshed automatically; the result says so.",
            Schema(("kind", "string", true), ("name", "string", true), ("installTo", "string", false),
                   ("path", "string", false), ("model", "string", false)),
            (h, p) => D365FO.Core.Journal.AotObjectDeleter.Delete(
                Str(p, "kind"), Str(p, "name"), StrOrNull(p, "installTo"),
                StrOrNull(p, "path"), StrOrNull(p, "model"))),

        new Descriptor("journal_list",
            "Inspect the modification-journal stack (most-recent-first) without reverting anything — " +
            "see what `undo_last_modification` would act on.",
            Schema(("limit", "integer", false)),
            (h, p) => h.JournalList(Int(p, "limit", 50))),
    };

    /// <summary>
    /// Enforces the <c>additionalProperties: false</c> every tool's schema already
    /// declares. Neither transport checked it, so a client that misspelled a key
    /// ("tabel" for "table") had it silently dropped and the handler ran as though
    /// the argument were simply absent — answering confidently from an input the
    /// caller never gave. Mirrors the CLI's <c>StrictParsing</c> (see
    /// <c>D365FO.Cli.CliApp</c>): unknown input fails loudly on both surfaces.
    /// </summary>
    /// <returns>An error message naming the offending key, or null when the arguments are clean.</returns>
    public static string? FindUnknownArgument(in Descriptor descriptor, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (descriptor.InputSchema["properties"] is not JsonObject properties) return null;

        foreach (var prop in args.EnumerateObject())
        {
            // JSON Schema property names are case-sensitive, and so is the binder
            // that reads them (JsonElement.TryGetProperty) — match the same way.
            if (properties.ContainsKey(prop.Name)) continue;

            var known = string.Join(", ", properties.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal));
            return $"Unknown argument '{prop.Name}' for tool '{descriptor.Name}'. Accepted arguments: {known}.";
        }
        return null;
    }

    // ---- JSON helpers ----

    /// <summary>
    /// Bind and dispatch <c>modify_object</c>: every operation the engine defines, plus the
    /// batched form.
    /// </summary>
    /// <remarks>
    /// The action is resolved through <see cref="ObjectModifyEngine.TryParseOperation"/> and an
    /// unresolved one is refused. It used to fall back to <c>SetProperty</c>, so the sixteen
    /// operations this tool did not know were not merely unreachable: <c>action="add-index"</c>
    /// set a property named after the index and reported success.
    /// </remarks>
    private static object ModifyObjectCall(ToolHandlers h, JsonElement p)
    {
        var kind = Str(p, "kind");
        var name = Str(p, "name");
        var model = StrOrNull(p, "model");

        if (p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("operations", out var operations)
            && operations.ValueKind == JsonValueKind.Array)
        {
            List<ObjectModifyEngine.ModifyRequest> steps;
            try
            {
                steps = BatchStepParser.Parse(operations, kind, name, model);
            }
            catch (Exception ex)
            {
                return D365FO.Core.ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"`operations` is not a usable step array: {ex.Message}",
                    "Each step is {\"operation\":\"<name>\", \"member\":\"<name>\", …}. Operation names are the "
                    + $"same as `action`: {string.Join(", ", ObjectModifyEngine.OperationNames)}.");
            }

            if (steps.Count == 0)
                return D365FO.Core.ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "`operations` is empty.");

            return h.ModifyObject(Request(p, ObjectModifyEngine.Operation.SetProperty, kind, name, model) with
            {
                Member = "batch",
                Batch = steps,
            });
        }

        if (!ObjectModifyEngine.TryParseOperation(Str(p, "action"), out var operation))
            return D365FO.Core.ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                string.IsNullOrWhiteSpace(Str(p, "action"))
                    ? "`action` is required (or pass `operations` for a batch)."
                    : $"Unknown action '{Str(p, "action")}' for modify_object.",
                $"Use one of: {string.Join(", ", ObjectModifyEngine.OperationNames)}.");

        return h.ModifyObject(Request(p, operation, kind, name, model));
    }

    private static ObjectModifyEngine.ModifyRequest Request(
        JsonElement p, ObjectModifyEngine.Operation operation, string kind, string name, string? model)
        => new()
        {
            Operation = operation,
            Kind = kind,
            ObjectName = name,
            Member = Str(p, "member"),
            Value = StrOrNull(p, "value"),
            Type = StrOrNull(p, "type"),
            Label = StrOrNull(p, "label"),
            Mandatory = Bool(p, "mandatory"),
            Parent = StrOrNull(p, "parent"),
            DataSource = StrOrNull(p, "dataSource"),
            DataField = StrOrNull(p, "dataField"),
            Model = model,
            Fields = StrArray(p, "fields"),
            RelatedTable = StrOrNull(p, "relatedTable"),
            RelatedField = StrOrNull(p, "relatedField"),
            DeleteAction = StrOrNull(p, "deleteAction"),
            AllowDuplicates = Bool(p, "allowDuplicates"),
            AlternateKey = Bool(p, "alternateKey"),
            NewName = StrOrNull(p, "newName"),
            DataSourceName = StrOrNull(p, "dataSourceName") ?? StrOrNull(p, "dataSource"),
            RangeValue = StrOrNull(p, "rangeValue"),
            EntryPointType = StrOrNull(p, "entryPointType") ?? StrOrNull(p, "type"),
            Access = StrOrNull(p, "access"),
            ExtensionSuffix = StrOrNull(p, "extensionSuffix"),
            ExtensionModel = StrOrNull(p, "extensionModel"),
            RequireExtension = Bool(p, "requireExtension"),
        };

    private static JsonObject Schema(params (string name, string type, bool required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var (n, t, r) in props)
        {
            // "object-array" is an array of objects (modify_object's `operations` steps);
            // a plain "array" is an array of strings, which is every other array here.
            var node = new JsonObject { ["type"] = t == "object-array" ? "array" : t };
            if (t == "array") node["items"] = new JsonObject { ["type"] = "string" };
            if (t == "object-array") node["items"] = new JsonObject { ["type"] = "object" };
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
