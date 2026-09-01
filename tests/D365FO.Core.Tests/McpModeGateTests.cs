using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Call-time and list-time enforcement of <see cref="ServerModeConfig.IsToolAllowed"/>
/// across both MCP entry points — the hand-rolled <see cref="StdioDispatcher"/>
/// (also reused by <see cref="HttpServerHost"/>) and the SDK-based
/// <see cref="McpServerHost"/>. A disallowed call must fail with
/// <see cref="D365FoErrorCodes.ModeNotAllowed"/>, never silently succeed.
/// </summary>
public class McpModeGateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-modegate-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
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

    // ---- StdioDispatcher (and by extension HttpServerHost, which reuses Dispatch) ----

    [Fact]
    public async Task ReadOnly_mode_omits_local_tools_from_tools_list()
    {
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.ReadOnly);
        using var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""" + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();

        Assert.DoesNotContain("generate_object", names);
        Assert.DoesNotContain("labels", names);
        Assert.DoesNotContain("get_workspace_info", names);
        Assert.DoesNotContain("get_method", names);
        // Bridge-backed edits mutate live metadata — a read-only deployment must not
        // even advertise them.
        Assert.DoesNotContain("modify_method", names);
        Assert.DoesNotContain("modify_object", names);
        Assert.DoesNotContain("undo_last_modification", names);
        Assert.Contains("search", names);
        Assert.Contains("index_status", names);
    }

    /// <summary>
    /// The invariant behind the list above: no tool that writes may be reachable in
    /// read-only mode. Asserted over <see cref="ToolCatalog.WriteTools"/> rather than a
    /// hand-kept list, so adding a write tool without classifying it fails here instead
    /// of silently shipping in a read-only deployment.
    /// </summary>
    [Fact]
    public void No_write_tool_is_reachable_in_read_only_mode()
    {
        Assert.Empty(ServerModeConfig.WriteToolsAreLocal);

        foreach (var tool in ToolCatalog.WriteTools)
        {
            Assert.False(ServerModeConfig.IsToolAllowed(McpServerMode.ReadOnly, tool),
                $"{tool} writes but is allowed in read-only mode");
            Assert.True(ServerModeConfig.IsToolAllowed(McpServerMode.WriteOnly, tool),
                $"{tool} writes but is not available in write-only mode");
        }
    }

    [Fact]
    public async Task ReadOnly_mode_rejects_a_bridge_write_call_with_ModeNotAllowed()
    {
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.ReadOnly);
        var req = """{"jsonrpc":"2.0","id":9,"method":"tools/call","params":{"name":"modify_object","arguments":{"action":"property","kind":"table","name":"CustTable","member":"Label","value":"x"}}}""";
        using var input = new StringReader(req + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, err.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task WriteOnly_mode_exposes_the_modify_and_journal_surface()
    {
        // A write-only companion instance is where the edits happen, so it must carry
        // the whole write→inspect→undo loop, not just generate_object.
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.WriteOnly);
        using var input = new StringReader("""{"jsonrpc":"2.0","id":10,"method":"tools/list"}""" + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();

        Assert.Contains("modify_method", names);
        Assert.Contains("modify_object", names);
        Assert.Contains("undo_last_modification", names);
        Assert.Contains("journal_list", names);
    }

    [Fact]
    public async Task WriteOnly_mode_tools_list_contains_only_local_tools()
    {
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.WriteOnly);
        using var input = new StringReader("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""" + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.True(ServerModeConfig.LocalTools.Contains(name!), $"{name} should not be listed in write-only mode");
    }

    [Fact]
    public async Task ReadOnly_mode_rejects_tools_call_to_local_tool_with_ModeNotAllowed()
    {
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.ReadOnly);
        var req = """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_workspace_info","arguments":{}}}""";
        using var input = new StringReader(req + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, err.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task WriteOnly_mode_rejects_tools_call_to_readonly_tool_with_ModeNotAllowed()
    {
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.WriteOnly);
        var req = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""";
        using var input = new StringReader(req + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, err.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Full_mode_still_allows_local_and_readonly_tools()
    {
        var dispatcher = new StdioDispatcher(Handlers(), McpServerMode.Full);
        var req = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"get_workspace_info","arguments":{}}}""";
        using var input = new StringReader(req + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.TryGetProperty("result", out _));
    }

    // ---- McpServerHost (SDK-based path) ----

    [Fact]
    public void BuildToolList_ReadOnly_mode_excludes_local_tools()
    {
        var tools = McpServerHost.BuildToolList(McpServerMode.ReadOnly);
        var names = tools.Select(t => t.Name).ToHashSet();

        Assert.DoesNotContain("generate_object", names);
        Assert.DoesNotContain("labels", names);
        Assert.Contains("search", names);
    }

    [Fact]
    public void BuildOptions_ReadOnly_mode_still_reports_full_ServerInfo()
    {
        // Mode filtering only narrows the tool list, not server identity/capabilities.
        var options = McpServerHost.BuildOptions(Handlers(), McpServerMode.ReadOnly);
        Assert.Equal("d365fo-mcp", options.ServerInfo!.Name);
        Assert.NotNull(options.Handlers?.ListToolsHandler);
        Assert.NotNull(options.Handlers?.CallToolHandler);
    }

    [Fact]
    public void Invoke_ReadOnly_mode_rejects_local_tool_with_ModeNotAllowed()
    {
        var result = McpServerHost.Invoke(Handlers(), McpServerMode.ReadOnly,
            new CallToolRequestParams { Name = "labels", Arguments = new Dictionary<string, JsonElement>() });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var doc = JsonDocument.Parse(text);
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Invoke_WriteOnly_mode_rejects_readonly_tool_with_ModeNotAllowed()
    {
        var result = McpServerHost.Invoke(Handlers(), McpServerMode.WriteOnly,
            new CallToolRequestParams { Name = "index_status" });

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var doc = JsonDocument.Parse(text);
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void Invoke_default_mode_parameter_is_Full_and_allows_everything()
    {
        // Backward-compat overload (no mode arg) must keep behaving like Full.
        var result = McpServerHost.Invoke(Handlers(), new CallToolRequestParams { Name = "get_workspace_info" });
        Assert.False(result.IsError ?? false);
    }
}
