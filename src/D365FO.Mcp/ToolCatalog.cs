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
internal static class ToolCatalog
{
    internal readonly record struct Descriptor(
        string Name,
        string Description,
        JsonObject InputSchema,
        Func<ToolHandlers, JsonElement, object> Invoke);

    internal static IReadOnlyList<Descriptor> All { get; } = new[]
    {
        new Descriptor("search_classes",
            "Find X++ classes by substring match on the class name.",
            Schema(("query", "string", true), ("model", "string", false), ("limit", "integer", false)),
            (h, p) => h.SearchClasses(Str(p, "query"), StrOrNull(p, "model"), Int(p, "limit", 50))),

        new Descriptor("search_tables",
            "Find AxTable objects by substring.",
            Schema(("query", "string", true), ("model", "string", false), ("limit", "integer", false)),
            (h, p) => h.SearchTables(Str(p, "query"), StrOrNull(p, "model"), Int(p, "limit", 50))),

        new Descriptor("search_edts",
            "Find Extended Data Types by substring.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchEdts(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("search_enums",
            "Find base enums by substring.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchEnums(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("search_labels",
            "Search label files for a key or value substring. Values are sanitised unless raw=true.",
            Schema(("query", "string", true), ("languages", "array", false), ("limit", "integer", false), ("raw", "boolean", false)),
            (h, p) => h.SearchLabels(Str(p, "query"), StrArray(p, "languages"), Int(p, "limit", 100), Bool(p, "raw"))),

        new Descriptor("search_labels_fts",
            "Rank-sorted SQLite FTS5 search over label values/keys. Supports phrase queries (\"customer invoice\"), NEAR, column filters like Value:customer.",
            Schema(("query", "string", true), ("languages", "array", false), ("limit", "integer", false), ("raw", "boolean", false)),
            (h, p) => h.SearchLabelsFts(Str(p, "query"), StrArray(p, "languages"), Int(p, "limit", 100), Bool(p, "raw"))),

        new Descriptor("get_table_details",
            "Return fields + relations for a table.",
            Schema(("name", "string", true)),
            (h, p) => h.GetTable(Str(p, "name"))),

        new Descriptor("get_edt_details",
            "Return a single EDT definition.",
            Schema(("name", "string", true)),
            (h, p) => h.GetEdt(Str(p, "name"))),

        new Descriptor("get_class_details",
            "Return class metadata: extends, methods, flags.",
            Schema(("name", "string", true)),
            (h, p) => h.GetClass(Str(p, "name"))),

        new Descriptor("get_enum_details",
            "Return enum header + values.",
            Schema(("name", "string", true)),
            (h, p) => h.GetEnum(Str(p, "name"))),

        new Descriptor("get_menu_item",
            "Resolve a menu item to the object it launches.",
            Schema(("name", "string", true)),
            (h, p) => h.GetMenuItem(Str(p, "name"))),

        new Descriptor("get_label",
            "Fetch one label entry by (file, language, key).",
            Schema(("file", "string", true), ("language", "string", true), ("key", "string", true), ("raw", "boolean", false)),
            (h, p) => h.GetLabel(Str(p, "file"), Str(p, "language"), Str(p, "key"), Bool(p, "raw"))),

        new Descriptor("find_coc_extensions",
            "Find Chain-of-Command extensions for a target class (optionally scoped to method).",
            Schema(("target", "string", true), ("method", "string", false)),
            (h, p) => h.FindCoc(Str(p, "target"), StrOrNull(p, "method"))),

        new Descriptor("get_security_coverage_for_object",
            "Return Role→Duty→Privilege routes that grant access to an object.",
            Schema(("object", "string", true), ("type", "string", false)),
            (h, p) => h.GetSecurity(Str(p, "object"), StrOr(p, "type", "Menuitem"))),

        new Descriptor("get_table_relations",
            "Return inbound / outbound FK relations for a table.",
            Schema(("table", "string", true)),
            (h, p) => h.GetTableRelations(Str(p, "table"))),

        new Descriptor("find_usages",
            "Substring-match any indexed entity (Tables/Classes/EDTs/Enums/MenuItems).",
            Schema(("symbol", "string", true), ("limit", "integer", false)),
            (h, p) => h.FindUsages(Str(p, "symbol"), Int(p, "limit", 100))),

        new Descriptor("index_status",
            "Current row counts of every entity table.",
            Schema(),
            (h, _) => h.IndexStatus()),

        // ---- Parity: forms / queries / views / entities / reports / services / workflows ----

        new Descriptor("get_form",
            "Return form metadata: data sources, source path.",
            Schema(("name", "string", true)),
            (h, p) => h.GetForm(Str(p, "name"))),

        new Descriptor("search_queries",
            "Find AxQuery objects by substring.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchQueries(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("get_query",
            "Return query metadata (data sources).",
            Schema(("name", "string", true)),
            (h, p) => h.GetQuery(Str(p, "name"))),

        new Descriptor("search_views",
            "Find AxView objects by substring.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchViews(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("get_view",
            "Return view metadata (fields + source query).",
            Schema(("name", "string", true)),
            (h, p) => h.GetView(Str(p, "name"))),

        new Descriptor("search_data_entities",
            "Find data entities by AOT name, public entity name, or collection name.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchDataEntities(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("get_data_entity",
            "Return data entity metadata (public names, staging table, fields).",
            Schema(("name", "string", true)),
            (h, p) => h.GetDataEntity(Str(p, "name"))),

        new Descriptor("search_reports",
            "Find SSRS reports by substring.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchReports(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("get_report",
            "Return report metadata (datasets).",
            Schema(("name", "string", true)),
            (h, p) => h.GetReport(Str(p, "name"))),

        new Descriptor("search_services",
            "Find AxService objects by substring.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchServices(Str(p, "query"), Int(p, "limit", 50))),

        new Descriptor("get_service",
            "Return service metadata (class + operations).",
            Schema(("name", "string", true)),
            (h, p) => h.GetService(Str(p, "name"))),

        new Descriptor("get_service_group",
            "Return service group metadata (member services).",
            Schema(("name", "string", true)),
            (h, p) => h.GetServiceGroup(Str(p, "name"))),

        new Descriptor("search_workflow_types",
            "Find AxWorkflowType objects by name or document class.",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchWorkflowTypes(Str(p, "query"), Int(p, "limit", 50))),

        // ---- Security details ----

        new Descriptor("get_security_role",
            "Return a security role (duties + privileges).",
            Schema(("name", "string", true)),
            (h, p) => h.GetSecurityRole(Str(p, "name"))),

        new Descriptor("get_security_duty",
            "Return a security duty (privileges).",
            Schema(("name", "string", true)),
            (h, p) => h.GetSecurityDuty(Str(p, "name"))),

        new Descriptor("get_security_privilege",
            "Return a security privilege (entry points).",
            Schema(("name", "string", true)),
            (h, p) => h.GetSecurityPrivilege(Str(p, "name"))),

        // ---- Models ----

        new Descriptor("list_models",
            "List every indexed model with publisher / layer / custom flag.",
            Schema(),
            (h, _) => h.ListModels()),

        new Descriptor("get_model_dependencies",
            "Return dependsOn / dependedBy for a model.",
            Schema(("name", "string", true)),
            (h, p) => h.GetModelDependencies(Str(p, "name"))),

        // ---- Extensions & handlers ----

        new Descriptor("find_extensions",
            "Find Table/Form/Enum/EDT _Extension objects targeting a given name.",
            Schema(("target", "string", true), ("kind", "string", false)),
            (h, p) => h.FindExtensions(Str(p, "target"), StrOrNull(p, "kind"))),

        new Descriptor("find_event_subscribers",
            "Find DataEventHandler / SubscribesTo handlers bound to a source object.",
            Schema(("source", "string", true), ("sourceKind", "string", false)),
            (h, p) => h.FindEventSubscribers(Str(p, "source"), StrOrNull(p, "sourceKind"))),

        // ---- Labels ----

        new Descriptor("resolve_label",
            "Resolve a label token like @SYS12345 across languages.",
            Schema(("token", "string", true), ("languages", "array", false), ("raw", "boolean", false)),
            (h, p) => h.ResolveLabel(Str(p, "token"), StrArray(p, "languages"), Bool(p, "raw"))),

        // ---- Table details pieces ----

        new Descriptor("get_table_methods",
            "Return methods declared on a table.",
            Schema(("table", "string", true)),
            (h, p) => h.GetTableMethods(Str(p, "table"))),

        new Descriptor("get_table_indexes",
            "Return indexes defined on a table.",
            Schema(("table", "string", true)),
            (h, p) => h.GetTableIndexes(Str(p, "table"))),

        new Descriptor("get_table_delete_actions",
            "Return delete actions defined on a table.",
            Schema(("table", "string", true)),
            (h, p) => h.GetTableDeleteActions(Str(p, "table"))),

        // ---- Heuristics & workspace ----

        new Descriptor("search_any",
            "Scope-agnostic substring search across every indexed kind (tables, classes, EDTs, enums, menu items, forms, queries, views, data entities, reports, services, workflows).",
            Schema(("query", "string", true), ("limit", "integer", false)),
            (h, p) => h.SearchAny(Str(p, "query"), Int(p, "limit", 100))),

        new Descriptor("suggest_edt",
            "Suggest indexed EDTs for a field name using similarity heuristics. Returns confidence-ranked candidates.",
            Schema(("fieldName", "string", true), ("limit", "integer", false)),
            (h, p) => h.SuggestEdt(Str(p, "fieldName"), Int(p, "limit", 5))),

        new Descriptor("get_workspace_info",
            "Return the effective D365FO_* settings the server is using (paths, custom-model patterns, label languages).",
            Schema(),
            (h, _) => h.GetWorkspaceInfo()),

        new Descriptor("stats",
            "Aggregate counters over the index: totals + top-N tables (by fields), top-N classes (by methods), top-N CoC targets, and per-model counts.",
            Schema(("topN", "integer", false)),
            (h, p) => h.Stats(Int(p, "topN", 10))),

        new Descriptor("batch_search",
            "Run several scope-agnostic searches in a single call. Returns a result-per-query block.",
            Schema(("queries", "array", true), ("limit", "integer", false)),
            (h, p) => h.BatchSearch(StrArray(p, "queries") ?? Array.Empty<string>(), Int(p, "limit", 50))),

        new Descriptor("validate_object_naming",
            "Static naming-rule check (PascalCase, length, character set, extension suffix, optional publisher prefix). No index access required.",
            Schema(("kind", "string", true), ("name", "string", true), ("prefix", "string", false)),
            (h, p) => h.ValidateObjectNaming(Str(p, "kind"), Str(p, "name"), StrOrNull(p, "prefix"))),

        new Descriptor("get_table_extension_info",
            "Return all TableExtensions targeting a given table.",
            Schema(("table", "string", true)),
            (h, p) => h.GetTableExtensionInfo(Str(p, "table"))),

        new Descriptor("analyze_extension_points",
            "Enumerate existing extensions / event handlers / CoC targeting an object and suggest the least-invasive strategy for a new change.",
            Schema(("target", "string", true)),
            (h, p) => h.AnalyzeExtensionPoints(Str(p, "target"))),

        new Descriptor("lint",
            "In-process Best-Practice gate. Categories: table-no-index, ext-named-not-attributed, string-without-edt.",
            Schema(("categories", "array", false), ("onlyCustomModels", "boolean", false)),
            (h, p) => h.Lint(StrArray(p, "categories"), Bool(p, "onlyCustomModels", true))),

        new Descriptor("create_label",
            "Create or update a key=value entry in a *.label.txt file. Fails with KEY_EXISTS unless overwrite=true.",
            Schema(("file", "string", true), ("key", "string", true), ("value", "string", true), ("overwrite", "boolean", false)),
            (h, p) => h.CreateLabel(Str(p, "file"), Str(p, "key"), Str(p, "value"), Bool(p, "overwrite"))),

        new Descriptor("rename_label",
            "Rename a label key in place, preserving its value.",
            Schema(("file", "string", true), ("oldKey", "string", true), ("newKey", "string", true), ("overwrite", "boolean", false)),
            (h, p) => h.RenameLabel(Str(p, "file"), Str(p, "oldKey"), Str(p, "newKey"), Bool(p, "overwrite"))),

        new Descriptor("delete_label",
            "Delete a label entry from a *.label.txt file.",
            Schema(("file", "string", true), ("key", "string", true)),
            (h, p) => h.DeleteLabel(Str(p, "file"), Str(p, "key"))),
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
}
