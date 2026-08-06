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
    private readonly McpServerMode _mode;

    public StdioDispatcher(ToolHandlers handlers, McpServerMode mode = McpServerMode.Full)
    {
        _handlers = handlers;
        _mode = mode;
    }

    /// <summary>
    /// Builds the default dispatcher against the environment-configured index.
    /// <paramref name="mode"/> defaults to the resolved <c>MCP_SERVER_MODE</c>
    /// setting (env var / settings.json), so every transport built on top of
    /// this factory — stdio, the daemon socket/pipe, and the HTTP host — honors
    /// the same mode without each caller re-resolving it.
    /// </summary>
    public static StdioDispatcher CreateDefault(string? databasePath = null, McpServerMode? mode = null)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        var resolvedMode = mode ?? ServerModeConfig.Resolve(D365FoSettings.Resolve("MCP_SERVER_MODE"));
        return new StdioDispatcher(new ToolHandlers(repo), resolvedMode);
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

    /// <summary>
    /// Processes a single decoded JSON-RPC request/notification object and
    /// returns the response envelope, or <c>null</c> for notifications (which
    /// per JSON-RPC 2.0 never get a reply). Public so non-stdio transports —
    /// currently <see cref="HttpServerHost"/>, one JSON-RPC message per HTTP
    /// POST — can reuse the exact same routing, mode gate, and dedup cache as
    /// the stdio/daemon transports instead of re-implementing them.
    /// </summary>
    public JsonObject? Dispatch(JsonElement root)
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
                        // Spec-defined field clients surface to the model once, before any
                        // tool call. Carries the rule canon so it is not re-paid per tool
                        // description — see ServerInstructions.
                        ["instructions"] = ServerInstructions.Text,
                    });

                case "initialized":
                case "notifications/initialized":
                case "notifications/cancelled":
                    return null;

                case "ping":
                    return isNotification ? null : Success(idNode, new JsonObject());

                case "tools/list":
                    return Success(idNode, BuildToolsList(_mode));

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

    private static JsonObject BuildToolsList(McpServerMode mode)
    {
        var arr = new JsonArray();
        foreach (var d in ToolCatalog.All)
        {
            // Tools disallowed under the resolved MCP_SERVER_MODE are omitted
            // from tools/list entirely — advertising a tool tools/call would
            // then reject with MODE_NOT_ALLOWED just confuses clients that
            // build a UI from this list.
            if (!ServerModeConfig.IsToolAllowed(mode, d.Name)) continue;
            arr.Add(new JsonObject
            {
                ["name"] = d.Name,
                ["description"] = d.Description,
                ["inputSchema"] = (JsonNode)d.InputSchema.DeepClone(),
                ["annotations"] = ToolCatalog.AnnotationsFor(d),
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

        // Call-time mode gate: even if a client cached an earlier tools/list
        // (or calls a tool name it already knew about), a disallowed tool
        // must never actually run under the resolved MCP_SERVER_MODE.
        if (!ServerModeConfig.IsToolAllowed(_mode, name))
        {
            return ErrorResponse(idNode, -32602,
                $"Tool '{name}' is not available in '{ServerModeConfig.ToWireString(_mode)}' mode.",
                new JsonObject { ["code"] = D365FoErrorCodes.ModeNotAllowed });
        }

        // Reject arguments the tool's schema does not declare, before the call is
        // dedup-cached or run — a misspelled key must not be silently dropped.
        if (ToolCatalog.FindUnknownArgument(descriptor, args) is { } argError)
        {
            return ErrorResponse(idNode, -32602, argError,
                new JsonObject { ["code"] = D365FoErrorCodes.BadInput });
        }

        // Duplicate-call dedup (agentic-loop mitigation): repeated identical
        // read calls are answered from a 60 s cache with a loop hint.
        var dedupable = !CallDedup.ExcludedTools.Contains(name) && !ToolCatalog.WriteTools.Contains(name);
        var dedupKey = dedupable
            ? CallDedup.Key(name, args.ValueKind == JsonValueKind.Undefined ? "{}" : args.GetRawText())
            : null;
        if (dedupKey is not null && CallDedup.TryGet(dedupKey) is { } cached)
        {
            return Success(idNode, new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = cached.Body + CallDedup.LoopHint },
                },
                ["isError"] = cached.IsError,
            });
        }

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
        if (dedupKey is not null) CallDedup.Store(dedupKey, body, isError);

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
