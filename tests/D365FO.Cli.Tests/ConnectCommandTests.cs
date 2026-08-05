using System.Text.Json.Nodes;
using D365FO.Cli.Commands.Connect;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Config-merge and URL-normalisation rules of `d365fo connect`. These are the
/// parts that can destroy a developer's other MCP entries, so they are tested
/// directly rather than through a live server.
/// </summary>
public class ConnectCommandTests
{
    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void Creates_the_section_when_the_file_does_not_exist_yet()
    {
        var res = ConnectCommand.MergeServerEntry(null, "mcpServers", "d365fo", "https://host", null, force: false);

        Assert.Null(res.Error);
        Assert.False(res.Replaced);
        var entry = Parse(res.Json!)["mcpServers"]!["d365fo"]!;
        Assert.Equal("http", (string?)entry["type"]);
        Assert.Equal("https://host/mcp", (string?)entry["url"]);
        Assert.Null(entry["headers"]);
    }

    [Fact]
    public void Preserves_every_other_server_and_top_level_key()
    {
        // The whole point: these files are shared with the developer's other MCP
        // servers, and hand-editing them is what loses entries.
        const string existing = """
        {
          "someOtherTopLevelKey": { "keepMe": true },
          "mcpServers": {
            "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] }
          }
        }
        """;

        var res = ConnectCommand.MergeServerEntry(existing, "mcpServers", "d365fo", "https://host", null, force: false);

        Assert.Null(res.Error);
        var root = Parse(res.Json!);
        Assert.True((bool)root["someOtherTopLevelKey"]!["keepMe"]!);
        Assert.Equal("npx", (string?)root["mcpServers"]!["github"]!["command"]);
        Assert.Equal("https://host/mcp", (string?)root["mcpServers"]!["d365fo"]!["url"]);
    }

    [Fact]
    public void Refuses_to_replace_an_existing_entry_without_force()
    {
        const string existing = """{ "mcpServers": { "d365fo": { "url": "https://old/mcp" } } }""";

        var res = ConnectCommand.MergeServerEntry(existing, "mcpServers", "d365fo", "https://new", null, force: false);

        Assert.Null(res.Json);
        Assert.Equal("ENTRY_EXISTS", res.Error!.Code);
    }

    [Fact]
    public void Replaces_an_existing_entry_with_force()
    {
        const string existing = """{ "mcpServers": { "d365fo": { "url": "https://old/mcp", "stale": 1 } } }""";

        var res = ConnectCommand.MergeServerEntry(existing, "mcpServers", "d365fo", "https://new", null, force: true);

        Assert.Null(res.Error);
        Assert.True(res.Replaced);
        var entry = Parse(res.Json!)["mcpServers"]!["d365fo"]!;
        Assert.Equal("https://new/mcp", (string?)entry["url"]);
        Assert.Null(entry["stale"]); // fully replaced, not shallow-merged
    }

    [Fact]
    public void A_different_name_adds_a_second_entry_alongside_the_first()
    {
        const string existing = """{ "mcpServers": { "d365fo": { "url": "https://prod/mcp" } } }""";

        var res = ConnectCommand.MergeServerEntry(existing, "mcpServers", "d365fo-local", "http://localhost:3000", null, force: false);

        Assert.Null(res.Error);
        var servers = Parse(res.Json!)["mcpServers"]!;
        Assert.Equal("https://prod/mcp", (string?)servers["d365fo"]!["url"]);
        Assert.Equal("http://localhost:3000/mcp", (string?)servers["d365fo-local"]!["url"]);
    }

    [Fact]
    public void Api_key_becomes_the_X_Api_Key_header()
    {
        var res = ConnectCommand.MergeServerEntry(null, "servers", "d365fo", "https://host", "s3cret", force: false);

        Assert.Equal("s3cret", (string?)Parse(res.Json!)["servers"]!["d365fo"]!["headers"]!["X-Api-Key"]);
    }

    [Fact]
    public void Refuses_to_overwrite_a_config_it_cannot_parse()
    {
        var res = ConnectCommand.MergeServerEntry("{ this is not json", "mcpServers", "d365fo", "https://host", null, force: true);

        Assert.Null(res.Json);
        Assert.Equal("CONFIG_UNPARSEABLE", res.Error!.Code);
    }

    [Fact]
    public void Tolerates_comments_and_trailing_commas_that_editors_allow()
    {
        // VS Code writes JSONC; refusing it would be a false alarm.
        const string existing = """
        {
          // the github server
          "servers": { "github": { "command": "npx" }, },
        }
        """;

        var res = ConnectCommand.MergeServerEntry(existing, "servers", "d365fo", "https://host", null, force: false);

        Assert.Null(res.Error);
        Assert.Equal("npx", (string?)Parse(res.Json!)["servers"]!["github"]!["command"]);
    }

    [Fact]
    public void Refuses_when_the_section_key_is_not_an_object()
    {
        var res = ConnectCommand.MergeServerEntry("""{ "mcpServers": "oops" }""", "mcpServers", "d365fo", "https://host", null, force: true);

        Assert.Null(res.Json);
        Assert.Equal("CONFIG_UNEXPECTED_SHAPE", res.Error!.Code);
    }

    [Theory]
    [InlineData("https://host", "https://host")]
    [InlineData("https://host/", "https://host")]
    [InlineData("https://host/mcp", "https://host")]
    [InlineData("https://host/mcp/", "https://host")]
    [InlineData("http://localhost:3000", "http://localhost:3000")]
    [InlineData("http://localhost:3000/mcp", "http://localhost:3000")]
    [InlineData("https://host/prefix", "https://host/prefix")]
    [InlineData("https://host/prefix/mcp", "https://host/prefix")]
    public void Normalises_the_base_url(string input, string expected)
    {
        // People paste the endpoint they configured, not the root.
        Assert.True(ConnectCommand.TryNormalizeBaseUrl(input, out var baseUrl, out _));
        Assert.Equal(expected, baseUrl);
    }

    [Theory]
    [InlineData("host:8080")]
    [InlineData("ftp://host")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Rejects_input_that_is_not_an_absolute_http_url(string input)
    {
        Assert.False(ConnectCommand.TryNormalizeBaseUrl(input, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
