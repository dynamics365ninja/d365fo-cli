using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Pure-function tests for <see cref="ServerModeConfig"/> — the single source
/// of truth both <see cref="StdioDispatcher"/> and <see cref="McpServerHost"/>
/// call to gate <c>tools/list</c> / <c>tools/call</c> against
/// <c>MCP_SERVER_MODE</c>. Security-relevant, so covered independent of any
/// transport.
/// </summary>
public class ServerModeConfigTests
{
    [Theory]
    [InlineData(null, McpServerMode.Full)]
    [InlineData("", McpServerMode.Full)]
    [InlineData("full", McpServerMode.Full)]
    [InlineData("read-only", McpServerMode.ReadOnly)]
    [InlineData("readonly", McpServerMode.ReadOnly)]
    [InlineData("READ-ONLY", McpServerMode.ReadOnly)]
    [InlineData("write-only", McpServerMode.WriteOnly)]
    [InlineData("writeonly", McpServerMode.WriteOnly)]
    [InlineData("WriteOnly", McpServerMode.WriteOnly)]
    [InlineData("bogus", McpServerMode.Full)]
    public void Resolve_parses_known_values_and_falls_back_to_full(string? raw, McpServerMode expected)
    {
        Assert.Equal(expected, ServerModeConfig.Resolve(raw));
    }

    [Theory]
    [InlineData(McpServerMode.Full, "full")]
    [InlineData(McpServerMode.ReadOnly, "read-only")]
    [InlineData(McpServerMode.WriteOnly, "write-only")]
    public void ToWireString_round_trips_through_Resolve(McpServerMode mode, string wire)
    {
        Assert.Equal(wire, ServerModeConfig.ToWireString(mode));
        Assert.Equal(mode, ServerModeConfig.Resolve(wire));
    }

    [Fact]
    public void Full_mode_allows_every_tool()
    {
        foreach (var d in ToolCatalog.All)
            Assert.True(ServerModeConfig.IsToolAllowed(McpServerMode.Full, d.Name), d.Name);
    }

    [Theory]
    [InlineData("generate_object")]
    [InlineData("labels")]
    [InlineData("get_workspace_info")]
    [InlineData("get_method")]
    public void ReadOnly_mode_excludes_local_tools(string toolName)
    {
        Assert.False(ServerModeConfig.IsToolAllowed(McpServerMode.ReadOnly, toolName));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("get_object_info")]
    [InlineData("index_status")]
    [InlineData("models")]
    public void ReadOnly_mode_allows_non_local_tools(string toolName)
    {
        Assert.True(ServerModeConfig.IsToolAllowed(McpServerMode.ReadOnly, toolName));
    }

    [Theory]
    [InlineData("generate_object")]
    [InlineData("labels")]
    [InlineData("get_workspace_info")]
    [InlineData("get_method")]
    public void WriteOnly_mode_allows_only_local_tools(string toolName)
    {
        Assert.True(ServerModeConfig.IsToolAllowed(McpServerMode.WriteOnly, toolName));
    }

    [Theory]
    [InlineData("search")]
    [InlineData("get_object_info")]
    [InlineData("index_status")]
    [InlineData("models")]
    public void WriteOnly_mode_excludes_non_local_tools(string toolName)
    {
        Assert.False(ServerModeConfig.IsToolAllowed(McpServerMode.WriteOnly, toolName));
    }

    /// <summary>
    /// ReadOnly and WriteOnly must partition the full catalog without gaps or
    /// overlaps — every tool lands in exactly one side, so a tool can never
    /// silently disappear from both a read-only and a write-only deployment.
    /// </summary>
    [Fact]
    public void ReadOnly_and_WriteOnly_partition_the_full_catalog()
    {
        foreach (var d in ToolCatalog.All)
        {
            var ro = ServerModeConfig.IsToolAllowed(McpServerMode.ReadOnly, d.Name);
            var wo = ServerModeConfig.IsToolAllowed(McpServerMode.WriteOnly, d.Name);
            Assert.True(ro ^ wo, $"{d.Name} must be allowed in exactly one of read-only/write-only (was ro={ro}, wo={wo}).");
        }
    }
}
