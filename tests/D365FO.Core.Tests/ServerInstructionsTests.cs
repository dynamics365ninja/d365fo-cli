using System.Text.RegularExpressions;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The <c>instructions</c> the SDK stdio server (the default transport) sends in
/// <c>initialize</c>. It sent none, so a client without the skill had 32 tool schemas and no
/// routing guidance.
/// </summary>
public class ServerInstructionsTests
{
    [Fact]
    public void Sdk_server_sends_routing_instructions_in_initialize()
    {
        var options = McpServerHost.BuildOptions(new ToolHandlers(new MetadataRepository(":memory:")));

        Assert.False(string.IsNullOrWhiteSpace(options.ServerInstructions));
        Assert.Equal(ServerInstructions.Routing, options.ServerInstructions);
    }

    /// <summary>A client re-reads the instructions on every turn, so they stay short.</summary>
    [Fact]
    public void Routing_instructions_stay_under_1500_characters()
    {
        Assert.True(ServerInstructions.Routing.Length < 1500,
            $"ServerInstructions.Routing is {ServerInstructions.Routing.Length} characters.");
    }

    /// <summary>Guidance that names a tool the server does not publish sends the client nowhere.</summary>
    [Fact]
    public void Every_tool_the_routing_instructions_name_is_published()
    {
        var tools = ToolCatalog.All.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var named = Regex.Matches(ServerInstructions.Routing, "`([a-z_]+)`")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, name => Assert.Contains(name, tools));
    }

    /// <summary>
    /// A tool the guidance tells the client to pass the grounding token to must accept it:
    /// an unknown argument is refused with BAD_INPUT, so naming one that does not
    /// (modify_object) would make the client's write fail.
    /// </summary>
    [Fact]
    public void Every_tool_told_to_take_the_grounding_token_accepts_it()
    {
        var line = ServerInstructions.Routing.Split('\n').Single(l => l.Contains("passing that token"));
        var named = Regex.Matches(line.Substring(0, line.IndexOf("passing that token", StringComparison.Ordinal)), "`([a-z_]+)`")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, name =>
        {
            var schema = ToolCatalog.All.Single(d => d.Name == name).InputSchema;
            Assert.True(schema["properties"] is System.Text.Json.Nodes.JsonObject p && p.ContainsKey("groundingToken"),
                $"{name} does not accept groundingToken.");
        });
    }
}
