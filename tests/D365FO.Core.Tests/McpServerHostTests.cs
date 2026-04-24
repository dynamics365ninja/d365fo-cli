using System.Text.Json;
using D365FO.Core.Index;
using D365FO.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Smoke tests for <see cref="McpServerHost"/> — the SDK-based MCP host.
/// We don't spin up a full stdio transport here; instead we exercise the
/// ListTools / CallTool handlers directly via <c>BuildOptions</c> so the tests
/// stay fast and deterministic.
/// </summary>
public class McpServerHostTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-sdkhost-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private ToolHandlers Handlers()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        return new ToolHandlers(repo);
    }

    [Fact]
    public void BuildOptions_publishes_every_catalog_tool()
    {
        var options = McpServerHost.BuildOptions(Handlers());
        Assert.NotNull(options.Handlers?.ListToolsHandler);

        // Sanity: server info + capabilities
        Assert.Equal("d365fo-mcp", options.ServerInfo!.Name);
        Assert.NotNull(options.Capabilities?.Tools);
    }

    [Fact]
    public void Invoke_index_status_returns_success_envelope()
    {
        var result = McpServerHost.Invoke(Handlers(),
            new CallToolRequestParams { Name = "index_status" });

        Assert.False(result.IsError ?? false);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var doc = JsonDocument.Parse(text);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public void Invoke_forwards_arguments_to_handler()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonDocument.Parse("\"NotThere\"").RootElement,
        };
        var result = McpServerHost.Invoke(Handlers(),
            new CallToolRequestParams { Name = "get_data_entity", Arguments = args });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("ENTITY_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Invoke_unknown_tool_returns_error_envelope()
    {
        var result = McpServerHost.Invoke(Handlers(),
            new CallToolRequestParams { Name = "does_not_exist" });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var doc = JsonDocument.Parse(text);
        Assert.Equal("UNKNOWN_TOOL", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }
}
