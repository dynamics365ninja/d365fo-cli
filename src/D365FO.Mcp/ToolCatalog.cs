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

    private static string[]? StrArray(JsonElement p, string name)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array) return null;
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String)
                 .Select(x => x.GetString()!).ToArray();
    }
}
