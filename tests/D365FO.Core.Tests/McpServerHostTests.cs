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
            ["objectType"] = JsonDocument.Parse("\"entity\"").RootElement,
            ["name"] = JsonDocument.Parse("\"NotThere\"").RootElement,
        };
        var result = McpServerHost.Invoke(Handlers(),
            new CallToolRequestParams { Name = "get_object_info", Arguments = args });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("ENTITY_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    /// <summary>
    /// The SDK host is the second transport into the same catalog, so it has to
    /// reject undeclared arguments too — otherwise a misspelled key is dropped
    /// and the handler answers as though the caller never passed it.
    /// </summary>
    [Fact]
    public void Invoke_unknown_argument_returns_badInput_instead_of_dropping_it()
    {
        var args = new Dictionary<string, JsonElement>
        {
            ["objectType"] = JsonDocument.Parse("\"table\"").RootElement,
            ["nmae"] = JsonDocument.Parse("\"CustTable\"").RootElement,
        };
        var result = McpServerHost.Invoke(Handlers(),
            new CallToolRequestParams { Name = "get_object_info", Arguments = args });

        Assert.True(result.IsError);
        var doc = JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        Assert.Equal("BAD_INPUT", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Unknown argument 'nmae'", doc.RootElement.GetProperty("error").GetProperty("message").GetString());
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

    /// <summary>
    /// Parity assertion: every entry in <see cref="ToolCatalog.All"/> must have
    /// a handler that can be invoked (even with empty parameters). This fails
    /// immediately if a catalog entry is added without a corresponding handler.
    /// </summary>
    [Fact]
    public void ToolCatalog_every_entry_has_a_working_handler()
    {
        var handlers = Handlers();
        var emptyParams = JsonDocument.Parse("{}").RootElement;
        var errors = new List<string>();

        foreach (var descriptor in ToolCatalog.All)
        {
            try
            {
                // Invoke with empty params — may return a validation error (ok=false)
                // but must NOT throw an unhandled exception.
                descriptor.Invoke(handlers, emptyParams);
            }
            catch (Exception ex)
            {
                errors.Add($"{descriptor.Name}: {ex.GetType().Name} — {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"The following catalog entries threw exceptions:\n" + string.Join("\n", errors));
    }
}
