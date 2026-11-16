namespace D365FO.Mcp;

/// <summary>
/// Named groups of <see cref="ToolCatalog"/> tools, selected with <c>MCP_TOOLSETS</c> (env var or
/// <c>settings.json</c>) or <c>d365fo-mcp --toolsets</c>, issue #249 item 7. A host injects every
/// advertised tool's schema into every turn (about 50k characters for the full catalog), so a
/// client that only reads metadata pays for <c>generate_object</c> and <c>modify_object</c> on each
/// message. <c>MCP_TOOLSETS=read</c> advertises the lookup tools only, about a fifth of that.
/// </summary>
/// <remarks>
/// Every tool belongs to exactly one group (<c>ToolsetTests</c> enforces it), so a new tool cannot
/// ship unreachable from every profile but <c>all</c>. The selection is a schema-size control, not a
/// safety one: <c>read</c> keeps <c>labels</c>, whose create/delete actions write label files.
/// <c>MCP_SERVER_MODE=read-only</c> is the safety control, and the two intersect.
/// </remarks>
public static class Toolsets
{
    /// <summary>The setting that selects the toolsets.</summary>
    public const string SettingKey = "MCP_TOOLSETS";

    /// <summary>The groups, in the order they are documented.</summary>
    public static IReadOnlyList<(string Name, string Purpose, string[] Tools)> Groups { get; } =
    [
        ("read", "Look up metadata: objects, X++ source, where-used, labels, security, models.",
            ["search", "batch_get_info", "get_object_info", "get_method", "find_references",
             "find_tables_by_field", "extension_info", "security_info", "labels", "models"]),
        ("knowledge", "Rules, patterns, build-error explanations, EDT and naming suggestions.",
            ["get_knowledge", "object_patterns", "explain_build_error", "suggest_edt", "validate_object_naming"]),
        ("author", "Prepare, generate, modify and delete objects, and undo those writes.",
            ["prepare", "generate_object", "modify_method", "modify_object", "delete_object",
             "undo_last_modification", "journal_list"]),
        ("check", "Validate and lint X++, analyze and verify projects.",
            ["validate", "lint", "analyze", "verify_project"]),
        ("index", "Index status, statistics, history, re-sync and workspace paths.",
            ["stats", "index_status", "index_sync", "index_history", "get_workspace_info"]),
        ("sdlc", "Build, database sync, tests and BP check through the D365FO developer tools.",
            ["sdlc"]),
    ];

    /// <summary>
    /// Resolve a comma- or space-separated selection. Each entry is a group name, a tool name, or
    /// <c>all</c>. Empty, unset or <c>all</c> means every tool (null). An unknown entry is an
    /// error rather than ignored: a typo must not start a server with fewer tools than meant,
    /// nor quietly with all of them.
    /// </summary>
    public static IReadOnlySet<string>? Resolve(string? raw, out string? error)
    {
        error = null;
        var entries = (raw ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0) return null;

        var tools = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        foreach (var entry in entries)
        {
            if (entry.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
            var group = Groups.FirstOrDefault(g => g.Name.Equals(entry, StringComparison.OrdinalIgnoreCase));
            if (group.Name is not null) { tools.UnionWith(group.Tools); continue; }
            var tool = ToolCatalog.All.FirstOrDefault(d => d.Name.Equals(entry, StringComparison.OrdinalIgnoreCase));
            if (tool.Name is not null) { tools.Add(tool.Name); continue; }
            unknown.Add(entry);
        }

        if (unknown.Count > 0)
        {
            error = $"{SettingKey}: unknown toolset or tool '{string.Join("', '", unknown)}'. "
                + $"Toolsets: all, {string.Join(", ", Groups.Select(g => g.Name))}; a tool name also works.";
            return null;
        }
        return tools;
    }

    /// <summary>The group a tool belongs to, or null for a tool no group lists.</summary>
    public static string? GroupOf(string tool) =>
        Groups.FirstOrDefault(g => g.Tools.Contains(tool, StringComparer.Ordinal)).Name;
}

/// <summary>
/// Which tools a server advertises and runs: the <see cref="McpServerMode"/> gate intersected with
/// the <see cref="Toolsets"/> selection. Both transports list and gate through
/// <see cref="IsAllowed"/>, so a tool outside the selection is neither advertised nor callable,
/// even by a client that cached an older <c>tools/list</c>. A bare <see cref="McpServerMode"/>
/// converts to a filter with every toolset enabled.
/// </summary>
public sealed record ToolFilter(McpServerMode Mode, IReadOnlySet<string>? Enabled = null)
{
    /// <summary>Every tool: <see cref="McpServerMode.Full"/>, all toolsets.</summary>
    public static ToolFilter Full { get; } = new(McpServerMode.Full);

    public static implicit operator ToolFilter(McpServerMode mode) => new(mode);

    /// <summary>
    /// The filter the settings select (<c>MCP_SERVER_MODE</c>, <c>MCP_TOOLSETS</c>);
    /// <paramref name="mode"/> and <paramref name="toolsets"/> override them when given.
    /// Throws on an unknown toolset, with the message <see cref="Toolsets.Resolve"/> gives.
    /// </summary>
    public static ToolFilter FromSettings(McpServerMode? mode = null, string? toolsets = null)
    {
        var resolvedMode = mode ?? ServerModeConfig.Resolve(D365FO.Core.D365FoSettings.Resolve("MCP_SERVER_MODE"));
        var enabled = Toolsets.Resolve(toolsets ?? D365FO.Core.D365FoSettings.Resolve(Toolsets.SettingKey), out var error);
        if (error is not null) throw new InvalidOperationException(error);
        return new ToolFilter(resolvedMode, enabled);
    }

    /// <summary>True when <paramref name="tool"/> is advertised and callable.</summary>
    public bool IsAllowed(string tool) =>
        ServerModeConfig.IsToolAllowed(Mode, tool) && (Enabled is null || Enabled.Contains(tool));

    /// <summary>Why <paramref name="tool"/> is refused: the mode, or the toolset selection.</summary>
    public string RefusalMessage(string tool) =>
        !ServerModeConfig.IsToolAllowed(Mode, tool)
            ? $"Tool '{tool}' is not available in '{ServerModeConfig.ToWireString(Mode)}' mode."
            : $"Tool '{tool}' is not enabled on this server: {Toolsets.SettingKey} does not include it "
              + $"(it is in the '{Toolsets.GroupOf(tool) ?? tool}' toolset).";

    /// <summary>
    /// A line for the <c>initialize</c> instructions when the selection hides tools, so the model
    /// does not reach for one the routing text names but the server does not offer. Null when
    /// every toolset is enabled.
    /// </summary>
    public string? InstructionsNote()
    {
        if (Enabled is null) return null;
        var hidden = ToolCatalog.All.Select(d => d.Name).Where(n => !IsAllowed(n)).ToList();
        return hidden.Count == 0
            ? null
            : $"This server does not offer: {string.Join(", ", hidden)}. Use the d365fo CLI for those, or ask the user to enable them ({Toolsets.SettingKey}).";
    }
}
