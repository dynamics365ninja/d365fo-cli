using System.Text.Json;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

public class McpDispatcherTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-mcp-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private async Task<List<JsonDocument>> Roundtrip(params string[] requests)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var dispatcher = new StdioDispatcher(new ToolHandlers(repo));

        using var input = new StringReader(string.Join('\n', requests) + '\n');
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonDocument.Parse(s))
            .ToList();
    }

    [Fact]
    public async Task Initialize_returns_protocol_version_and_capabilities()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        var doc = Assert.Single(resp);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").GetProperty("tools").ValueKind == JsonValueKind.Object);
    }

    [Fact]
    public async Task ToolsList_returns_non_empty_catalog()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var doc = Assert.Single(resp);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 10);
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("search_classes", names);
        Assert.Contains("get_enum_details", names);
        Assert.Contains("index_status", names);
    }

    [Fact]
    public async Task UnknownMethod_returns_jsonrpc_error()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":3,"method":"does/not/exist"}""");
        var doc = Assert.Single(resp);
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_wraps_tool_result_in_content_block()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");
        var doc = Assert.Single(resp);
        var content = doc.RootElement.GetProperty("result").GetProperty("content");
        var first = content[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        var payload = JsonDocument.Parse(first.GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Notification_does_not_get_a_reply()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Empty(resp);
    }
}
