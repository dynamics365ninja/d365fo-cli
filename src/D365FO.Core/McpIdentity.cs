// <copyright file="McpIdentity.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core;

/// <summary>
/// The names that tell this repository's MCP adapter apart from the sibling
/// <c>d365fo-mcp-server</c> project, whose npm package and command are <c>d365fo-mcp</c>.
/// </summary>
public static class McpIdentity
{
    /// <summary>This adapter: its command, MCP <c>serverInfo.name</c> and <c>/health</c> service.</summary>
    public const string AdapterName = "d365fo-cli-mcp";

    /// <summary>The <c>service</c> the sibling server reports on <c>/health</c>.</summary>
    public const string SiblingServiceName = "d365fo-mcp-server";
}
