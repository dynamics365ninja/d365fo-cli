using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core;
using D365FO.Core.Index;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace D365FO.Mcp;

/// <summary>
/// Hosts the MCP server backed by the official
/// <c>ModelContextProtocol</c> SDK. The dispatcher's responsibilities shrink
/// to:
///   • publishing <see cref="ToolCatalog"/> entries as MCP tools,
///   • routing <c>tools/call</c> to <see cref="ToolHandlers"/>,
///   • wrapping the structured <see cref="ToolResult{T}"/> envelope into an
///     MCP <see cref="CallToolResult"/> with a single text content block.
/// Everything else (framing, lifecycle, error envelopes, notifications) is
/// handled by the SDK.
/// </summary>
public static class McpServerHost
{
    private const string ServerName = "d365fo-mcp";
    private static readonly string ServerVersion = D365FO.Core.ProductVersion.Informational;

    public static Task RunStdioAsync(string? databasePath = null, ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();
        return RunStdioAsync(new ToolHandlers(repo), ToolFilter.FromSettings(), loggerFactory, ct);
    }

    public static Task RunStdioAsync(ToolHandlers handlers, ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
        => RunStdioAsync(handlers, McpServerMode.Full, loggerFactory, ct);

    public static async Task RunStdioAsync(ToolHandlers handlers, ToolFilter filter, ILoggerFactory? loggerFactory = null, CancellationToken ct = default)
    {
        loggerFactory ??= LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

        var options = BuildOptions(handlers, filter);
        var transport = new StdioServerTransport(options, loggerFactory);
        var server = McpServer.Create(transport, options, loggerFactory, serviceProvider: null);
        await server.RunAsync(ct);
    }

    /// <summary>
    /// The <c>tools/list</c> payload for <paramref name="filter"/> — every
    /// <see cref="ToolCatalog"/> entry <see cref="ToolFilter.IsAllowed"/>
    /// permits (the mode and the toolsets; every tool when null). Split out from <see cref="BuildOptions"/> (which wraps
    /// this in an SDK <see cref="ListToolsHandler"/> delegate) so the mode
    /// filtering can be asserted directly in tests without constructing an SDK
    /// <c>RequestContext</c>.
    /// </summary>
    public static List<Tool> BuildToolList(ToolFilter? filter = null) =>
        ToolCatalog.All
            .Where(d => (filter ?? ToolFilter.Full).IsAllowed(d.Name))
            .Select(d => new Tool
            {
                Name = d.Name,
                Description = d.Description,
                InputSchema = JsonSerializer.SerializeToElement((JsonNode)d.InputSchema),
                // The same hints the legacy transport sends (ToolCatalog.AnnotationsFor). Without
                // them a client on the default transport sees every tool as un-annotated, so it
                // cannot tell delete_object from search when it decides what to confirm.
                Annotations = new ToolAnnotations
                {
                    Title = (string?)ToolCatalog.AnnotationsFor(d)["title"],
                    ReadOnlyHint = d.Effect == ToolCatalog.ToolEffect.Read,
                    DestructiveHint = d.Effect == ToolCatalog.ToolEffect.Destructive,
                    IdempotentHint = d.Effect != ToolCatalog.ToolEffect.Destructive,
                    OpenWorldHint = false,
                },
            }).ToList();

    public static McpServerOptions BuildOptions(ToolHandlers handlers, ToolFilter? filter = null)
    {
        var resolved = filter ?? ToolFilter.Full;
        var tools = BuildToolList(resolved);

        return new McpServerOptions
        {
            ServerInfo = new Implementation { Name = ServerName, Version = ServerVersion },
            ServerInstructions = resolved.InstructionsNote() is { } note
                ? ServerInstructions.Routing + "\n" + note
                : ServerInstructions.Routing,
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
            },
            Handlers = new McpServerHandlers
            {
                ListToolsHandler = (_, _) =>
                    ValueTask.FromResult(new ListToolsResult { Tools = tools }),
                CallToolHandler = (ctx, _) => ValueTask.FromResult(Invoke(handlers, resolved, ctx.Params)),
            },
        };
    }

    public static CallToolResult Invoke(ToolHandlers handlers, CallToolRequestParams? request)
        => Invoke(handlers, McpServerMode.Full, request);

    public static CallToolResult Invoke(ToolHandlers handlers, ToolFilter filter, CallToolRequestParams? request)
    {
        if (request is null)
            return ErrorResult("BAD_REQUEST", "Missing tools/call parameters.");

        var descriptor = ToolCatalog.All.FirstOrDefault(d => d.Name == request.Name);
        if (descriptor.Name is null)
            return ErrorResult("UNKNOWN_TOOL", $"Unknown tool: {request.Name}");

        if (!filter.IsAllowed(request.Name))
            return ErrorResult(D365FoErrorCodes.ModeNotAllowed, filter.RefusalMessage(request.Name));

        var args = SerializeArguments(request.Arguments);

        if (ToolCatalog.FindUnknownArgument(descriptor, args) is { } argError)
            return ErrorResult(D365FoErrorCodes.BadInput, argError);

        object raw;
        try
        {
            raw = ToolCatalog.InvokeWithIndexRefresh(descriptor, handlers, args);
        }
        catch (Exception ex)
        {
            raw = ToolResult<object>.Fail("HANDLER_THREW", ex.Message, ex.GetType().Name);
        }

        var body = D365Json.Serialize(raw);
        var isError = raw is ToolResult<object> tr && !tr.Ok;

        // This transport does not dedup-cache, but the cache is process-wide: flush it so a
        // dispatcher sharing the process never serves a pre-write answer.
        CallDedup.InvalidateAfter(request.Name);

        return new CallToolResult
        {
            IsError = isError,
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = body },
            },
        };
    }

    private static JsonElement SerializeArguments(IDictionary<string, JsonElement>? args)
    {
        var obj = new JsonObject();
        if (args is not null)
        {
            foreach (var kvp in args)
                obj[kvp.Key] = JsonNode.Parse(kvp.Value.GetRawText());
        }
        return JsonSerializer.SerializeToElement((JsonNode)obj);
    }

    private static CallToolResult ErrorResult(string code, string message)
    {
        var body = D365Json.Serialize(ToolResult<object>.Fail(code, message));
        return new CallToolResult
        {
            IsError = true,
            Content = new List<ContentBlock> { new TextContentBlock { Text = body } },
        };
    }
}
