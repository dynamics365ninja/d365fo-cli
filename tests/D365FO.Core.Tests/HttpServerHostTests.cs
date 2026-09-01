using System.Net;
using System.Text;
using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Mcp;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Integration tests for <see cref="HttpServerHost"/> — the streamable-HTTP-lite
/// transport for a shared team deployment. Starts a real Kestrel instance on an
/// ephemeral port and drives it with <see cref="HttpClient"/> so auth, mode
/// gating, and rate limiting are exercised through the actual ASP.NET Core
/// pipeline rather than by calling handler methods directly.
///
/// Prioritizes the security-relevant paths per the task brief: missing key,
/// wrong key, and the mode gate over the wire.
/// </summary>
public class HttpServerHostTests : IAsyncLifetime
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-http-{Guid.NewGuid():N}.sqlite");
    private WebApplication? _app;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_app is not null) await _app.StopAsync();
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private async Task<(HttpClient client, string baseUrl)> StartAsync(HttpServerHost.Options options)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var handlers = new ToolHandlers(repo);
        var dispatcher = new StdioDispatcher(handlers, options.Mode);

        _app = HttpServerHost.Build(dispatcher, handlers, options);
        _app.Urls.Add("http://127.0.0.1:0");
        await _app.StartAsync();

        var baseUrl = _app.Urls.First();
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return (client, baseUrl);
    }

    // ---- /health ----

    [Fact]
    public async Task Health_returns_ok_status_mode_and_index_reachability_without_auth()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: "secret", McpServerMode.ReadOnly));

        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("read-only", doc.RootElement.GetProperty("mode").GetString());
        Assert.True(doc.RootElement.GetProperty("indexReachable").GetBoolean());
    }

    // ---- auth ----

    [Fact]
    public async Task Mcp_without_api_key_configured_allows_calls_without_header()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: null, McpServerMode.Full));

        var resp = await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Mcp_with_api_key_configured_rejects_missing_header_with_401_and_Unauthorized_code()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: "secret-key", McpServerMode.Full));

        var resp = await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(D365FoErrorCodes.Unauthorized, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mcp_with_api_key_configured_rejects_wrong_header_value_with_401()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: "secret-key", McpServerMode.Full));
        client.DefaultRequestHeaders.Add(HttpServerHost.ApiKeyHeaderName, "wrong-key");

        var resp = await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Mcp_with_correct_api_key_succeeds()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: "secret-key", McpServerMode.Full));
        client.DefaultRequestHeaders.Add(HttpServerHost.ApiKeyHeaderName, "secret-key");

        var resp = await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Health_never_requires_api_key_even_when_configured()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: "secret-key", McpServerMode.Full));

        var resp = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- mode gate over HTTP ----

    [Fact]
    public async Task Mcp_ReadOnly_mode_rejects_local_tool_call_with_ModeNotAllowed()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: null, McpServerMode.ReadOnly));

        var resp = await PostRpc(client,
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"get_workspace_info","arguments":{}}}""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // JSON-RPC error, not an HTTP-level failure

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, doc.RootElement.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Mcp_ReadOnly_mode_tools_list_omits_local_tools()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: null, McpServerMode.ReadOnly));

        var resp = await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();

        Assert.DoesNotContain("generate_object", names);
        Assert.Contains("search", names);
    }

    // ---- rate limiting ----

    [Fact]
    public async Task Mcp_rate_limits_after_permit_exhausted_with_429_and_RateLimited_code()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(
            0, ApiKey: null, McpServerMode.Full, RateLimitPermits: 2, RateLimitWindow: TimeSpan.FromMinutes(5)));

        var r1 = await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        var r2 = await PostRpc(client, """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        var r3 = await PostRpc(client, """{"jsonrpc":"2.0","id":3,"method":"ping"}""");

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal((HttpStatusCode)429, r3.StatusCode);

        var doc = JsonDocument.Parse(await r3.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(D365FoErrorCodes.RateLimited, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Health_is_not_subject_to_the_mcp_rate_limiter()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(
            0, ApiKey: null, McpServerMode.Full, RateLimitPermits: 1, RateLimitWindow: TimeSpan.FromMinutes(5)));

        // Exhaust the /mcp limiter …
        await PostRpc(client, """{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        var limited = await PostRpc(client, """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal((HttpStatusCode)429, limited.StatusCode);

        // … /health must still be reachable.
        var health = await client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ---- malformed request ----

    [Fact]
    public async Task Mcp_invalid_json_returns_400_with_BadInput_code()
    {
        var (client, _) = await StartAsync(new HttpServerHost.Options(0, ApiKey: null, McpServerMode.Full));

        var resp = await client.PostAsync("/mcp", new StringContent("{not json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(D365FoErrorCodes.BadInput, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private static Task<HttpResponseMessage> PostRpc(HttpClient client, string json) =>
        client.PostAsync("/mcp", new StringContent(json, Encoding.UTF8, "application/json"));
}
