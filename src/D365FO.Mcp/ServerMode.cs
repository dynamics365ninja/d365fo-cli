namespace D365FO.Mcp;

/// <summary>
/// Server mode configuration, resolved from the <c>MCP_SERVER_MODE</c>
/// environment variable / <c>settings.json</c> key (via
/// <see cref="D365FO.Core.D365FoSettings.Resolve(string)"/>).
///
/// Mirrors upstream <c>d365fo-mcp-server</c>'s <c>serverMode.ts</c>: a hybrid
/// deployment can run an Azure-hosted instance in <see cref="ReadOnly"/> mode
/// (search / analysis over the shared index) alongside a local companion
/// instance in <see cref="WriteOnly"/> mode (scaffolding + label writes that
/// need the local package tree). <see cref="Full"/> is the default — a single
/// process exposing every tool, for local single-machine use.
/// </summary>
public enum McpServerMode
{
    Full,
    ReadOnly,
    WriteOnly,
}

/// <summary>
/// Single source of truth for which <see cref="ToolCatalog"/> entries are
/// exposed / callable in a given <see cref="McpServerMode"/>. Both the
/// <c>tools/list</c> response and the <c>tools/call</c> runtime gate in
/// <see cref="StdioDispatcher"/> and <see cref="McpServerHost"/> call
/// <see cref="IsToolAllowed"/> so the advertised catalog and call-time
/// enforcement can never drift apart.
/// </summary>
public static class ServerModeConfig
{
    /// <summary>
    /// Tools that need the local package tree / local process configuration
    /// and therefore cannot run against a shared, filesystem-less deployment
    /// (e.g. an Azure App Service instance with no D365FO packages on disk):
    /// <list type="bullet">
    ///   <item><description><c>generate_object</c> — writes AOT XML to
    ///   <c>installTo</c>/<c>out</c> on the local disk for the table/class/coc/form
    ///   objectTypes.</description></item>
    ///   <item><description><c>labels</c> — mixes read actions (search/fts/info) with
    ///   write actions (create/rename/delete) that write label files to disk; kept
    ///   as one entry, the same call <see cref="ToolCatalog.WriteTools"/> already
    ///   makes for write-confirmation purposes.</description></item>
    ///   <item><description><c>get_workspace_info</c> — reports the local
    ///   <c>D365FO_*</c> path configuration of this process.</description></item>
    ///   <item><description><c>get_method</c> — reads raw X++ source directly off
    ///   disk at the indexed <c>SourcePath</c> (not from SQLite), so it needs the
    ///   local package tree to be reachable.</description></item>
    ///   <item><description><c>modify_method</c> / <c>modify_object</c> — every edit
    ///   round-trips through the local <c>D365FO.Bridge</c> process, which loads
    ///   <c>IMetadataProvider</c> over the on-disk package tree. Both also mutate live
    ///   metadata, so a read-only deployment must not advertise them at all: see
    ///   <see cref="WriteToolsAreLocal"/>.</description></item>
    ///   <item><description><c>undo_last_modification</c> — replays journal entries back
    ///   through the same disk/bridge write path that produced them.</description></item>
    ///   <item><description><c>journal_list</c> — reads <c>&lt;index-dir&gt;/journal/</c>,
    ///   which is only populated on the instance that did the writing. Read-only itself,
    ///   but it belongs with the writer: a write-only companion needs it to inspect what
    ///   it just did, and a shared read-only instance has no journal to report.</description></item>
    /// </list>
    /// Excluded in <see cref="McpServerMode.ReadOnly"/>; the only tools exposed in
    /// <see cref="McpServerMode.WriteOnly"/>.
    /// </summary>
    public static readonly HashSet<string> LocalTools = new(StringComparer.Ordinal)
    {
        "generate_object", "labels", "get_workspace_info", "get_method",
        "modify_method", "modify_object", "undo_last_modification", "journal_list",
    };

    /// <summary>
    /// Every tool that mutates state is local, so <see cref="McpServerMode.ReadOnly"/>
    /// never advertises or accepts one. Exposed for the test that locks the invariant —
    /// the four bridge-backed write tools were originally absent from
    /// <see cref="LocalTools"/>, which let a read-only deployment advertise (and run)
    /// live metadata edits.
    /// </summary>
    public static IEnumerable<string> WriteToolsAreLocal =>
        ToolCatalog.WriteTools.Where(t => !LocalTools.Contains(t));

    /// <summary>
    /// Parse the <c>MCP_SERVER_MODE</c> value. Accepts <c>full</c> (default),
    /// <c>read-only</c>/<c>readonly</c>, <c>write-only</c>/<c>writeonly</c>
    /// (case-insensitive). Unrecognized values fall back to <see cref="McpServerMode.Full"/>.
    /// </summary>
    public static McpServerMode Resolve(string? raw)
    {
        var v = (raw ?? "full").Trim().ToLowerInvariant();
        return v switch
        {
            "read-only" or "readonly" => McpServerMode.ReadOnly,
            "write-only" or "writeonly" => McpServerMode.WriteOnly,
            _ => McpServerMode.Full,
        };
    }

    /// <summary>The canonical wire/display string for a mode (round-trips through <see cref="Resolve"/>).</summary>
    public static string ToWireString(McpServerMode mode) => mode switch
    {
        McpServerMode.ReadOnly => "read-only",
        McpServerMode.WriteOnly => "write-only",
        _ => "full",
    };

    /// <summary>
    /// True when <paramref name="toolName"/> may be advertised/called under
    /// <paramref name="mode"/>.
    /// </summary>
    public static bool IsToolAllowed(McpServerMode mode, string toolName) => mode switch
    {
        McpServerMode.ReadOnly => !LocalTools.Contains(toolName),
        McpServerMode.WriteOnly => LocalTools.Contains(toolName),
        _ => true,
    };
}
