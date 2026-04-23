using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core;
using D365FO.Core.Index;

namespace D365FO.Mcp;

/// <summary>
/// JSON-RPC 2.0 server implementing the subset of the
/// <a href="https://modelcontextprotocol.io">Model Context Protocol</a>
/// required by mainstream MCP clients (Claude Desktop, Cursor, VS Code
/// Copilot MCP).
///
/// Methods implemented:
/// <list type="bullet">
///   <item><c>initialize</c> — handshake, returns capabilities + serverInfo.</item>
///   <item><c>initialized</c> / <c>notifications/initialized</c> — ack, ignored.</item>
///   <item><c>ping</c> — returns empty object.</item>
///   <item><c>tools/list</c> — lists every tool in <see cref="ToolCatalog"/>.</item>
///   <item><c>tools/call</c> — invokes a tool by name; response follows the MCP
///   content schema (<c>content[0].type == "text"</c>, text body is the
///   serialised <see cref="ToolResult{T}"/>).</item>
/// </list>
/// Frames are newline-delimited UTF-8 JSON on stdio — MCP's default stdio
/// transport. This is intentionally dependency-free; swapping in the official
/// C# SDK later is mechanical because the <see cref="ToolHandlers"/> surface
/// stays identical.
/// </summary>
public sealed class StdioDispatcher
{
    private const string ProtocolVersion = "2024-11-05";
    private const string ServerName = "d365fo-mcp";
    private const string ServerVersion = "0.1.0-dev";

    private readonly ToolHandlers _handlers;

    public StdioDispatcher(ToolHandlers handlers) => _handlers = handlers;

    public static StdioDispatcher CreateDefault(string? databasePath = null)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
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

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                await WriteAsync(output, ErrorResponse(null, -32700, "Parse error: " + ex.Message), ct);
                continue;
            }

            var response = Dispatch(root);
            if (response is not null)
                await WriteAsync(output, response, ct);
        }
    }

    private JsonObject? Dispatch(JsonElement root)
    {
        JsonNode? idNode = null;
        try
        {
            if (root.TryGetProperty("id", out var id))
                idNode = JsonNode.Parse(id.GetRawText());
        }
        catch { /* non-fatal */ }

        string method;
        try
        {
            method = root.GetProperty("method").GetString() ?? "";
        }
        catch
        {
            return ErrorResponse(idNode, -32600, "Invalid request: missing method");
        }

        JsonElement paramsEl = default;
        if (root.TryGetProperty("params", out var p)) paramsEl = p;

        // Notifications (no id) never get a reply, per JSON-RPC 2.0.
        bool isNotification = idNode is null;

        try
        {
            switch (method)
            {
                case "initialize":
                    return Success(idNode, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject
                        {
                            ["tools"] = new JsonObject { ["listChanged"] = false },
                        },
                        ["serverInfo"] = new JsonObject
                        {
                            ["name"] = ServerName,
                            ["version"] = ServerVersion,
                        },
                    });

                case "initialized":
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                case "ping":
                    return isNotification ? null : Success(idNode, new JsonObject());

                case "tools/list":
                    return Success(idNode, BuildToolsList());

                case "tools/call":
                    return HandleToolsCall(idNode, paramsEl);

                default:
                    return isNotification ? null : ErrorResponse(idNode, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex)
        {
            return ErrorResponse(idNode, -32603, "Internal error: " + ex.Message);
        }
    }

    private static JsonObject BuildToolsList()
    {
        var arr = new JsonArray();
        foreach (var d in ToolCatalog.All)
        {
            arr.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["inputSchema"] = (JsonNode)d.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    private JsonObject HandleToolsCall(JsonNode? idNode, JsonElement paramsEl)
    {
        if (paramsEl.ValueKind != JsonValueKind.Object)
            return ErrorResponse(idNode, -32602, "tools/call requires params object.");
        var name = paramsEl.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var args = paramsEl.TryGetProperty("arguments", out var a) ? a : default;

        var descriptor = ToolCatalog.All.FirstOrDefault(d => d.Name == name);
        if (descriptor.Name is null)
            return ErrorResponse(idNode, -32602, $"Unknown tool: {name}");

        object raw;
        try
        {
            raw = descriptor.Invoke(_handlers, args);
        }
        catch (Exception ex)
        {
            raw = ToolResult<object>.Fail("HANDLER_THREW", ex.Message, ex.GetType().Name);
        }

        var body = D365Json.Serialize(raw);
        bool isError = raw is ToolResult<object> tr && !tr.Ok;

        return Success(idNode, new JsonObject
        {
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = body },
            },
            ["isError"] = isError,
        });
    }

    // ---- JSON-RPC envelopes ----

    private static JsonObject Success(JsonNode? id, JsonNode result) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? JsonValue.Create<object?>(null),
            ["result"] = result,
        };

    private static JsonObject ErrorResponse(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) err["data"] = data;
        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone() ?? JsonValue.Create<object?>(null),
            ["error"] = err,
        };
    }

    private static async Task WriteAsync(TextWriter output, JsonObject envelope, CancellationToken ct)
    {
        var line = envelope.ToJsonString();
        await output.WriteLineAsync(line.AsMemory(), ct);
        await output.FlushAsync(ct);
    }
}
