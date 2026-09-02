using System.Text.RegularExpressions;
using D365FO.Cli.Commands.Agent;
using D365FO.Core.Bridge;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The CLI and the MCP server are two faces of one tool. This is what holds them together.
/// </summary>
/// <remarks>
/// <para>
/// Nothing checked that claim, and both halves had drifted from it. The JSON manifest
/// <c>d365fo schema</c> publishes — the map an agent reads to translate between the shell
/// surface and the MCP surface — listed 130-odd commands while the app registered 200, so the
/// entire <c>modify</c> write surface, the knowledge corpus, the BP-moniker catalog and three
/// pattern catalogs were undiscoverable through it. It also claimed routes that did not exist
/// (<c>object_patterns(action=analyze)</c>) and denied ones that did (<c>validate xpp</c> is
/// <c>validate(mode=xpp)</c>).
/// </para>
/// <para>
/// So the rule these tests enforce is: every registered command is either published in the
/// manifest or declared here as deliberately out of it; every MCP route the manifest names is
/// one the tool really dispatches; and every MCP tool is reachable from some command or
/// declared MCP-only. Adding a command without deciding which side of that line it falls on
/// fails the build, which is the only thing that has ever kept a hand-written inventory honest.
/// </para>
/// </remarks>
public class CliMcpParityTests
{
    /// <summary>
    /// Commands deliberately absent from the published manifest, each with the reason.
    /// </summary>
    /// <remarks>
    /// The manifest is the agent-facing catalog, not a full <c>--help</c>. What belongs out of
    /// it is machinery for maintaining this repository or this installation — an agent working
    /// on X++ has no use for it, and every entry costs tokens on the turn that reads the
    /// manifest.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> OutOfScope = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eval list"] = "Eval harness — maintains this repository's own test corpus, not the user's X++.",
        ["eval run"] = "Eval harness.",
        ["eval score"] = "Eval harness.",
        ["eval capture"] = "Eval harness.",
        ["eval report"] = "Eval harness.",
        ["eval clusters"] = "Eval harness.",
        ["eval coverage"] = "Eval harness.",
        ["eval knowledge"] = "Eval harness.",
        ["eval verify-build"] = "Eval harness.",
        ["completion"] = "Shell integration: emits a completion script for the user's shell, not an operation on metadata.",
        ["connect"] = "Editor setup: points an MCP client at a deployed server. An agent already connected has nothing to do with it.",
        ["daemon warmup"] = "Daemon internals; start/stop/status are published, warm-up is a performance detail.",
        ["knowledge audit"] = "Proves this repository's own knowledge corpus against an installation — a maintenance gate, not a lookup.",
        ["bp-moniker extract"] = "Rebuilds the shipped BP catalog from an installation; like `index extract`, an operator task.",
        ["index cross-check"] = "Reports where this tool's catalogs are narrower than the installation — a maintenance report.",
        ["index optimize"] = "Index housekeeping (VACUUM/ANALYZE).",
        ["index export"] = "Index transport for CI caching.",
        ["index import"] = "Index transport for CI caching.",
        ["validate form-pattern-repair"] = "Alias of `form-pattern repair`, published under its own branch.",
        ["oracle sweep"] = "Oracle harness — runs this tool's own validator over an installation to find rules that "
            + "fire on correct Microsoft code. A measurement of the tool, not an operation on the user's X++.",
        ["oracle census"] = "Oracle harness — measures what shipped AOT XML actually contains, which is where this "
            + "tool's metadata facts come from.",
        ["oracle members"] = "Oracle harness — the per-member half of the census.",
        ["oracle probe"] = "Oracle harness — compiles a throwaway model with xppc to check a scaffold against the "
            + "real compiler. An agent that wants its work compiled has `build`, which builds the model it "
            + "installed into rather than a temporary copy of it.",
        ["oracle runtime"] = "Oracle harness — diagnoses whether the SysTest runner is wired to a database and can "
            + "tell a passing test from a failing one. Configures a Microsoft-installed file; an operator task.",
    };

    /// <summary>
    /// MCP tools with no CLI command behind them, each with the reason. Kept small on purpose:
    /// a tool that only MCP can reach is a capability the CLI's own users cannot have.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> McpOnly = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // (empty — every tool is reachable from a command)
    };

    /// <summary>
    /// Published commands that deliberately have no MCP route, each with the reason.
    /// </summary>
    /// <remarks>
    /// This is the direction that rots quietly. A manifest entry with an empty <c>mcpTool</c>
    /// list reads as "nothing to say" rather than "decided", which is how the whole `modify`
    /// surface, every pattern catalog and sixteen scaffolds came to be shell-only without anyone
    /// choosing that. Making the empty list require an entry here turns each one into a claim
    /// someone had to write down.
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> CliOnly = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["daemon start"] = "Runs a long-lived local process. An MCP server IS the long-lived process; starting a second one from inside it is not a tool call.",
        ["daemon stop"] = "See `daemon start`.",
        ["daemon status"] = "See `daemon start`.",
        ["init"] = "Interactive first-run setup: detects PackagesLocalDirectory, writes a profile, offers to extract. It asks questions, which a tool call cannot answer.",
        ["doctor"] = "Diagnoses the local environment — paths, bridge, index, versions. `get_workspace_info` reports the resolved configuration, which is the part of it a remote caller can act on.",
        ["version"] = "Reports the assembly version of the shell binary. The MCP server reports its own in `initialize`.",
        ["index build"] = "Creates the index database. First-run setup, and the server needs a database to have been started at all.",
        ["index extract"] = "Walks the whole PackagesLocalDirectory — minutes of filesystem work, which is the wrong shape for a call someone waits on. The narrow form an agent actually needs is `index sync`, which re-reads one model and IS exposed (`index_sync`).",
        ["index refresh"] = "The incremental form of `index extract`, and the same reason. `index sync` is its per-model counterpart over MCP.",
        ["schema"] = "Publishes this manifest — the shell surface described for an agent that has a shell. An MCP client reads `tools/list` instead.",
        ["agent-prompt"] = "Emits the CLI-first system prompt. Same reason as `schema`: it describes the shell surface to a harness that will use it.",
    };

    private static IReadOnlyList<string> RegisteredCommands() =>
        CliSurface.Leaves().Select(l => l.Path).ToList();

    private static SchemaCommand.CommandSpec[] Manifest() => SchemaCommand.Commands();

    [Fact]
    public void Every_registered_command_is_published_or_declared_out_of_scope()
    {
        var published = Manifest().Select(c => c.Command).ToHashSet(StringComparer.Ordinal);

        var undeclared = RegisteredCommands()
            .Where(path => !published.Contains(path) && !OutOfScope.ContainsKey(path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(undeclared.Count == 0,
            "These commands are registered but not published in `d365fo schema --full`, and not declared "
            + "out of scope in CliMcpParityTests.OutOfScope — so an agent reading the manifest cannot find "
            + "them:\n  " + string.Join("\n  ", undeclared));
    }

    [Fact]
    public void Every_manifest_entry_names_a_command_the_app_registers()
    {
        var registered = RegisteredCommands().ToHashSet(StringComparer.Ordinal);

        var phantom = Manifest()
            .Select(c => c.Command)
            .Where(command => !registered.Contains(command))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(phantom.Count == 0,
            "The manifest publishes commands the app does not register — an agent that runs one gets "
            + "'Unknown command':\n  " + string.Join("\n  ", phantom));
    }

    [Fact]
    public void No_exclusion_outlives_the_command_it_excludes()
    {
        var registered = RegisteredCommands().ToHashSet(StringComparer.Ordinal);

        var stale = OutOfScope.Keys
            .Where(path => !registered.Contains(path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(stale.Count == 0,
            "These are declared out of scope but no longer registered — drop them from OutOfScope:\n  "
            + string.Join("\n  ", stale));
    }

    /// <summary>
    /// A manifest claim like <c>analyze (mode=impact)</c> has to be a call that actually routes.
    /// </summary>
    /// <remarks>
    /// The tool name is checked against the catalog, and each discriminator value against the
    /// tool's own description — the descriptions enumerate every value they dispatch, which is
    /// what an agent reads before choosing one. A value the description does not mention is
    /// either unreachable or undocumented, and both are defects.
    /// </remarks>
    [Fact]
    public void Every_mcp_route_the_manifest_claims_is_one_the_tool_dispatches()
    {
        var tools = ToolCatalog.All.ToDictionary(d => d.Name, d => d.Description, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var spec in Manifest())
        foreach (var claim in spec.McpTool)
        {
            var match = Regex.Match(claim, @"^(?<tool>[a-z_0-9]+)(\s*\((?<args>.*)\))?$");
            if (!match.Success)
            {
                problems.Add($"{spec.Command}: '{claim}' is not '<tool>' or '<tool> (<key>=<value>, …)'.");
                continue;
            }

            var tool = match.Groups["tool"].Value;
            if (!tools.TryGetValue(tool, out var description))
            {
                problems.Add($"{spec.Command}: names MCP tool '{tool}', which the catalog does not publish.");
                continue;
            }

            foreach (var token in Discriminators(match.Groups["args"].Value))
            {
                if (!description.Contains(token, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"{spec.Command}: claims '{claim}', but '{tool}' never mentions '{token}'.");
            }
        }

        Assert.True(problems.Count == 0,
            "The manifest maps CLI commands onto MCP calls that do not exist:\n  "
            + string.Join("\n  ", problems));
    }

    /// <summary>The values a claim's parenthesis names, e.g. "mode=artifact,type=role" → artifact, role.</summary>
    private static IEnumerable<string> Discriminators(string args)
    {
        if (string.IsNullOrWhiteSpace(args)) yield break;

        foreach (var part in args.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // "objectType=table" → table; "queries[]" → queries; "relations" → relations.
            var value = part.Contains('=') ? part[(part.IndexOf('=') + 1)..] : part;
            value = value.Replace("[]", "", StringComparison.Ordinal).Trim();

            // "action=search/fts" is one claim covering two actions.
            foreach (var alternative in value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (alternative.Length > 0) yield return alternative;
        }
    }

    [Fact]
    public void Every_mcp_tool_is_reachable_from_the_cli_or_declared_mcp_only()
    {
        var claimed = Manifest()
            .SelectMany(c => c.McpTool)
            .Select(claim => claim.Split('(', 2)[0].Trim())
            .ToHashSet(StringComparer.Ordinal);

        var orphans = ToolCatalog.All
            .Select(d => d.Name)
            .Where(name => !claimed.Contains(name) && !McpOnly.ContainsKey(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(orphans.Count == 0,
            "These MCP tools are named by no CLI command in the manifest and are not declared MCP-only — "
            + "either the CLI cannot do what MCP can, or the manifest forgot to say it can:\n  "
            + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Every_published_command_has_an_mcp_route_or_a_reason_not_to()
    {
        var unexplained = Manifest()
            .Where(c => c.McpTool.Length == 0 && !CliOnly.ContainsKey(c.Command))
            .Select(c => c.Command)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(unexplained.Count == 0,
            "These commands are published with no MCP route and no reason for it. Either route them, or "
            + "say in CliMcpParityTests.CliOnly why the shell is the only place they belong:\n  "
            + string.Join("\n  ", unexplained));

        var stale = CliOnly.Keys
            .Where(command => Manifest().Any(c => c.Command == command && c.McpTool.Length > 0))
            .ToList();

        Assert.True(stale.Count == 0,
            "These are declared CLI-only but the manifest now names an MCP route for them — drop them from "
            + "CliOnly: " + string.Join(", ", stale));
    }

    [Fact]
    public void No_mcp_only_declaration_outlives_its_tool()
    {
        var tools = ToolCatalog.All.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var stale = McpOnly.Keys.Where(name => !tools.Contains(name)).ToList();

        Assert.True(stale.Count == 0,
            "Declared MCP-only but no longer a tool: " + string.Join(", ", stale));
    }

    /// <summary>
    /// The write surface, checked against the engine rather than against either surface's idea
    /// of it.
    /// </summary>
    /// <remarks>
    /// The engine grew from five operations to twenty; the CLI gained a sub-command for each and
    /// <c>modify_object</c> kept dispatching the four it was born with, silently treating every
    /// other <c>action</c> as <c>property</c>. Enumerating the enum is what makes "both surfaces
    /// or neither" a fact instead of an intention.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ModifyOperations))]
    public void Every_modify_operation_is_reachable_from_both_surfaces(string operation)
    {
        Assert.Contains($"modify {operation}", RegisteredCommands());

        var modifyObject = ToolCatalog.All.Single(d => d.Name == "modify_object");
        Assert.Contains(operation, modifyObject.Description);

        Assert.True(ObjectModifyEngine.TryParseOperation(operation, out _),
            $"'{operation}' is the engine's own name for the operation and does not parse back to it.");
    }

    public static TheoryData<string> ModifyOperations()
    {
        var data = new TheoryData<string>();
        foreach (var name in ObjectModifyEngine.OperationNames) data.Add(name);
        return data;
    }

    /// <summary>
    /// Every scaffold the CLI can produce is one an agent on the MCP surface can produce too.
    /// </summary>
    /// <remarks>
    /// Seventeen of the thirty-three <c>generate</c> sub-commands had an objectType and sixteen
    /// did not, which is not a smaller tool but a differently-shaped one: an agent that had
    /// picked MCP could not scaffold a report, a workflow, a view, a map or a SysTest, and the
    /// only sign of it was "Unknown objectType" for something the tool does.
    /// </remarks>
    [Fact]
    public void Every_generate_subcommand_is_reachable_over_mcp()
    {
        var byCommand = Manifest().ToDictionary(c => c.Command, c => c.McpTool, StringComparer.Ordinal);

        var unreachable = RegisteredCommands()
            .Where(path => path.StartsWith("generate ", StringComparison.Ordinal))
            .Where(path => !byCommand.TryGetValue(path, out var claims)
                           || !claims.Any(c => c.StartsWith("generate_object", StringComparison.Ordinal)))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        Assert.True(unreachable.Count == 0,
            "These scaffolds exist only on the shell surface — generate_object has no objectType for "
            + "them:\n  " + string.Join("\n  ", unreachable));
    }

    /// <summary>An unknown action must fail, not quietly become a property write.</summary>
    [Fact]
    public void An_unknown_modify_action_is_refused()
    {
        Assert.False(ObjectModifyEngine.TryParseOperation("add-widget", out _));
        Assert.False(ObjectModifyEngine.TryParseOperation("", out _));
        Assert.False(ObjectModifyEngine.TryParseOperation(null, out _));

        // The spelling the CLI sub-command uses for SetProperty must resolve — the batch parser
        // derived the enum name instead and refused the one operation whose names differ.
        Assert.True(ObjectModifyEngine.TryParseOperation("property", out var property));
        Assert.Equal(ObjectModifyEngine.Operation.SetProperty, property);
    }
}
