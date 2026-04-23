using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;

namespace D365FO.Mcp;

/// <summary>
/// Minimal stdio MCP-like server. Full MCP protocol (initialize/list_tools/
/// call_tool with JSON-RPC) is delegated to a future commit that plugs the
/// official C# SDK on top of <see cref="ToolHandlers"/>. For now this class
/// exposes a hand-rolled newline-delimited JSON dispatch that proves the
/// coexistence wiring: one shared Core, two transports.
/// </summary>
public sealed class StdioDispatcher
{
    private readonly ToolHandlers _handlers;

    public StdioDispatcher(ToolHandlers handlers) => _handlers = handlers;

    public static StdioDispatcher CreateDefault(string? databasePath = null)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.DatabasePath)!);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        return new StdioDispatcher(new ToolHandlers(repo));
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken ct = default)
    {
        string? line;
        while (!ct.IsCancellationRequested && (line = await input.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string response;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var tool = root.GetProperty("tool").GetString() ?? "";
                var args = root.TryGetProperty("args", out var a) ? a : default;
                var result = Invoke(tool, args);
                response = D365Json.Serialize(result);
            }
            catch (Exception ex)
            {
                response = D365Json.Serialize(ToolResult<object>.Fail(
                    "BAD_REQUEST", ex.Message, ex.GetType().Name));
            }
            await output.WriteLineAsync(response);
            await output.FlushAsync(ct);
        }
    }

    private ToolResult<object> Invoke(string tool, JsonElement args)
    {
        string Arg(string name) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
                ? v.GetString() ?? "" : "";

        int ArgInt(string name, int dflt) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v)
                && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : dflt;

        return tool switch
        {
            "search_classes"   => _handlers.SearchClasses(Arg("query"), NullIfEmpty(Arg("model")), ArgInt("limit", 50)),
            "get_table_details"=> _handlers.GetTable(Arg("name")),
            "get_edt_details"  => _handlers.GetEdt(Arg("name")),
            "get_class_details"=> _handlers.GetClass(Arg("name")),
            "find_coc_extensions" => _handlers.FindCoc(Arg("class"), NullIfEmpty(Arg("method"))),
            "search_labels"    => _handlers.SearchLabels(Arg("query"), null, ArgInt("limit", 100)),
            "get_security_coverage_for_object"
                               => _handlers.GetSecurity(Arg("object"), string.IsNullOrEmpty(Arg("type")) ? "Menuitem" : Arg("type")),
            "get_table_relations" => _handlers.GetTableRelations(Arg("table")),
            _ => ToolResult<object>.Fail("UNKNOWN_TOOL", $"Tool '{tool}' is not registered."),
        };

        static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;
    }
}
