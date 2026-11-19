using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #249 item 7: <c>MCP_TOOLSETS</c> narrows the advertised tools, so a client that only reads
/// metadata does not pay for every write tool's schema on each turn.
/// </summary>
public sealed class ToolsetTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-toolsets-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext);
    }

    private ToolHandlers Handlers()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        return new ToolHandlers(repo);
    }

    private static ToolFilter Filter(string toolsets, McpServerMode mode = McpServerMode.Full)
    {
        var enabled = Toolsets.Resolve(toolsets, out var error);
        Assert.Null(error);
        return new ToolFilter(mode, enabled);
    }

    private static async Task<JsonElement> Send(StdioDispatcher dispatcher, string request)
    {
        using var input = new StringReader(request + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);
        return JsonDocument.Parse(output.ToString()).RootElement.Clone();
    }

    private async Task<(List<string> Names, int SchemaChars)> ToolsList(ToolFilter filter)
    {
        var root = await Send(new StdioDispatcher(Handlers(), filter),
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        var tools = root.GetProperty("result").GetProperty("tools");
        return (tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList(), tools.GetRawText().Length);
    }

    [Fact]
    public void Every_tool_is_in_exactly_one_toolset_and_every_listed_tool_exists()
    {
        var catalog = ToolCatalog.All.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var listed = Toolsets.Groups.SelectMany(g => g.Tools).ToList();

        Assert.Equal(listed.Count, listed.Distinct(StringComparer.Ordinal).Count());
        Assert.All(listed, t => Assert.Contains(t, catalog));
        Assert.All(catalog, t => Assert.Contains(t, listed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("read, ALL")]
    public void Unset_empty_or_all_enables_every_tool(string? raw)
    {
        Assert.Null(Toolsets.Resolve(raw, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void Groups_and_single_tools_combine_case_insensitively()
    {
        var tools = Toolsets.Resolve("Read; prepare validate", out var error);

        Assert.Null(error);
        Assert.Contains("search", tools!);
        Assert.Contains("prepare", tools!);
        Assert.Contains("validate", tools!);
        Assert.DoesNotContain("generate_object", tools!);
    }

    [Fact]
    public void An_unknown_entry_is_an_error_naming_it_and_the_valid_toolsets()
    {
        Assert.Null(Toolsets.Resolve("read,reed", out var error));
        Assert.Contains("'reed'", error);
        Assert.Contains("knowledge", error);
        Assert.Throws<InvalidOperationException>(() => ToolFilter.FromSettings(McpServerMode.Full, "reed"));
    }

    [Fact]
    public async Task The_read_toolset_lists_only_its_tools_at_under_a_third_of_the_full_schema()
    {
        var (all, allChars) = await ToolsList(ToolFilter.Full);
        var (read, readChars) = await ToolsList(Filter("read"));

        Assert.Equal(ToolCatalog.All.Count, all.Count);
        Assert.Equal(Toolsets.Groups.Single(g => g.Name == "read").Tools.Order(), read.Order());
        Assert.True(readChars * 3 < allChars, $"read toolset is {readChars} of {allChars} schema characters");
    }

    [Fact]
    public async Task Toolsets_intersect_with_the_server_mode()
    {
        var (names, _) = await ToolsList(Filter("read", McpServerMode.ReadOnly));

        Assert.Contains("search", names);
        Assert.DoesNotContain("labels", names);      // local: read-only mode drops it
        Assert.DoesNotContain("get_method", names);  // reads from disk: local
        Assert.DoesNotContain("prepare", names);     // not in the read toolset
    }

    [Fact]
    public async Task A_tool_outside_the_toolsets_is_refused_at_call_time_with_the_setting_named()
    {
        var root = await Send(new StdioDispatcher(Handlers(), Filter("read")),
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"generate_object","arguments":{}}}""");

        var error = root.GetProperty("error");
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed, error.GetProperty("data").GetProperty("code").GetString());
        Assert.Contains("MCP_TOOLSETS", error.GetProperty("message").GetString());
        Assert.Contains("'author'", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_tool_inside_the_toolsets_still_runs()
    {
        var root = await Send(new StdioDispatcher(Handlers(), Filter("index")),
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");

        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Initialize_names_the_tools_this_server_does_not_offer()
    {
        var root = await Send(new StdioDispatcher(Handlers(), Filter("read")),
            """{"jsonrpc":"2.0","id":4,"method":"initialize","params":{}}""");
        var instructions = root.GetProperty("result").GetProperty("instructions").GetString()!;

        Assert.Contains("does not offer", instructions);
        Assert.Contains("generate_object", instructions);
        Assert.Null(ToolFilter.Full.InstructionsNote());
    }

    [Fact]
    public void The_sdk_transport_lists_and_gates_the_same_way()
    {
        var filter = Filter("read");

        Assert.Equal(Toolsets.Groups.Single(g => g.Name == "read").Tools.Order(),
            McpServerHost.BuildToolList(filter).Select(t => t.Name).Order());
        Assert.Contains("does not offer", McpServerHost.BuildOptions(Handlers(), filter).ServerInstructions);

        var result = McpServerHost.Invoke(Handlers(), filter, new CallToolRequestParams { Name = "sdlc" });
        Assert.True(result.IsError);
        Assert.Contains(D365FoErrorCodes.ModeNotAllowed, ((TextContentBlock)result.Content[0]).Text);
    }
}
