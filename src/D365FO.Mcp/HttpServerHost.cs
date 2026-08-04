using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using D365FO.Core;
using D365FO.Core.Index;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace D365FO.Mcp;

/// <summary>
/// Streamable-HTTP-lite transport for a shared team deployment (e.g. an Azure
/// App Service instance a whole team points their MCP client at, instead of
/// each developer running a local <c>d365fo-mcp</c> stdio process).
///
/// This deliberately mirrors upstream <c>d365fo-mcp-server</c>'s
/// <c>CustomHttpTransport</c>: one JSON-RPC request in, one JSON-RPC response
/// out per <c>POST /mcp</c> call — <em>not</em> the official MCP SDK's
/// session/SSE-based streamable-HTTP transport (<c>ModelContextProtocol.AspNetCore</c>'s
/// <c>MapMcp()</c>), which assumes a stateful, long-lived client connection
/// with an <c>Mcp-Session-Id</c> and reconnectable SSE stream. A shared
/// read-only Azure instance fielding short independent tool calls from several
/// developers' MCP clients doesn't need — and shouldn't pay the complexity
/// tax of — that session model, and layering our own <c>X-Api-Key</c> auth and
/// rate-limit gates on top of it would fight the SDK's own request pipeline.
/// A minimal ASP.NET Core app gives full control over both for a fraction of
/// the code.
///
/// Routing itself is NOT reimplemented here: every request is handed to
/// <see cref="StdioDispatcher.Dispatch(System.Text.Json.JsonElement)"/>, the
/// same method the stdio transport uses, so tool routing, the
/// <c>MCP_SERVER_MODE</c> gate, and the duplicate-call dedup cache are
/// identical across transports by construction. This class only adds the
/// HTTP-specific concerns: <c>X-Api-Key</c> auth, rate limiting, and a
/// liveness probe.
/// </summary>
public static class HttpServerHost
{
    public const string ApiKeyHeaderName = "X-Api-Key";
    private const string RateLimiterPolicy = "d365fo-mcp-http";

    /// <summary>Runtime configuration for <see cref="Build"/>.</summary>
    public sealed record Options(
        int Port,
        string? ApiKey,
        McpServerMode Mode,
        int RateLimitPermits = 60,
        TimeSpan? RateLimitWindow = null)
    {
        public TimeSpan Window => RateLimitWindow ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>
    /// Resolves settings from the environment — same precedence chain as
    /// <see cref="StdioDispatcher.CreateDefault"/> (env var → settings.json →
    /// default) via <see cref="D365FoSettings.Resolve(string)"/> — and runs the
    /// HTTP host until <paramref name="ct"/> is cancelled.
    /// </summary>
    public static async Task RunAsync(string? databasePath, int port, CancellationToken ct = default)
    {
        var settings = D365FoSettings.FromEnvironment(databasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(settings.DatabasePath);
        repo.EnsureSchema();

        var mode = ServerModeConfig.Resolve(D365FoSettings.Resolve("MCP_SERVER_MODE"));
        var apiKey = D365FoSettings.Resolve("API_KEY");
        var handlers = new ToolHandlers(repo);
        var dispatcher = new StdioDispatcher(handlers, mode);

        var options = new Options(port, apiKey, mode);
        var app = Build(dispatcher, handlers, options);
        app.Urls.Clear();
        app.Urls.Add($"http://0.0.0.0:{port}");
        // WebApplication.RunAsync(string?) shadows IHost.RunAsync(CancellationToken)
        // with a same-arity overload on a different parameter type, so the
        // cancellable overload has to be reached through the IHost interface.
        await ((IHost)app).RunAsync(ct);
    }

    /// <summary>
    /// Builds (but does not start) the configured <see cref="WebApplication"/>.
    /// Split out from <see cref="RunAsync"/> so tests can start it on an
    /// ephemeral port and exercise it with a real <see cref="HttpClient"/>
    /// instead of parsing raw socket frames.
    /// </summary>
    public static WebApplication Build(StdioDispatcher dispatcher, ToolHandlers handlers, Options options)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Fixed-window limiter keyed by API key when present, else remote IP.
        // Good enough for "don't let one client hammer a shared instance" —
        // not a distributed limiter, but this is a single-process App Service
        // deployment, so in-memory state is fine.
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, token) =>
            {
                ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                ctx.HttpContext.Response.ContentType = "application/json";
                await ctx.HttpContext.Response.WriteAsync(
                    D365Json.Serialize(ToolResult<object>.Fail(D365FoErrorCodes.RateLimited,
                        "Rate limit exceeded. Try again later.")),
                    token);
            };
            o.AddPolicy(RateLimiterPolicy, httpContext =>
            {
                var key = httpContext.Request.Headers[ApiKeyHeaderName].FirstOrDefault()
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = options.RateLimitPermits,
                    Window = options.Window,
                    QueueLimit = 0,
                });
            });
        });

        var app = builder.Build();

        if (string.IsNullOrEmpty(options.ApiKey))
        {
            // Deliberate warning, not a hard failure: local/dev usage (e.g.
            // hitting the HTTP transport from localhost during development)
            // is a legitimate use case without an API key. Production
            // deployments must set API_KEY themselves — see the "HTTP
            // transport" section of docs/MIGRATION_FROM_MCP.md.
            app.Logger.LogWarning(
                "API_KEY is not set. The /mcp endpoint on port {Port} is running WITHOUT authentication " +
                "— set the API_KEY environment variable before exposing this server outside localhost.",
                options.Port);
        }

        app.UseRateLimiter();

        app.MapGet("/health", () =>
        {
            bool indexReachable;
            try { indexReachable = handlers.IndexStatus().Ok; }
            catch { indexReachable = false; }

            return Results.Json(new
            {
                status = "ok",
                mode = ServerModeConfig.ToWireString(options.Mode),
                indexReachable,
            });
        });

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (!string.IsNullOrEmpty(options.ApiKey))
            {
                var provided = ctx.Request.Headers[ApiKeyHeaderName].FirstOrDefault();
                if (!ConstantTimeEquals(provided, options.ApiKey))
                {
                    await WriteError(ctx, StatusCodes.Status401Unauthorized,
                        D365FoErrorCodes.Unauthorized, "Missing or invalid X-Api-Key header.");
                    return;
                }
            }

            JsonElement root;
            try
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                root = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                await WriteError(ctx, StatusCodes.Status400BadRequest,
                    D365FoErrorCodes.BadInput, "Invalid JSON: " + ex.Message);
                return;
            }

            // Same dispatch StdioDispatcher.RunAsync uses per line — identical
            // routing, MCP_SERVER_MODE gate, and dedup cache as stdio.
            var response = dispatcher.Dispatch(root);
            ctx.Response.ContentType = "application/json";
            if (response is null)
            {
                // JSON-RPC notification (no id) — never gets a reply.
                ctx.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }
            await ctx.Response.WriteAsync(response.ToJsonString(), ctx.RequestAborted);
        }).RequireRateLimiting(RateLimiterPolicy);

        return app;
    }

    private static Task WriteError(HttpContext ctx, int statusCode, string code, string message)
    {
        ctx.Response.StatusCode = statusCode;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(D365Json.Serialize(ToolResult<object>.Fail(code, message)), ctx.RequestAborted);
    }

    /// <summary>Timing-safe comparison so API key checks don't leak length/prefix via response latency.</summary>
    private static bool ConstantTimeEquals(string? provided, string expected)
    {
        if (provided is null) return false;
        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        // CryptographicOperations.FixedTimeEquals requires equal-length spans;
        // a length mismatch is itself not sensitive (only a byte-for-byte
        // match should short-circuit meaningfully), so compare lengths first.
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
