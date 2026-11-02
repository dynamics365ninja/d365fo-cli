namespace D365FO.Cli;

/// <summary>
/// Accepts the argument shapes callers reach for first, before Spectre parses them:
/// <c>search &lt;QUERY&gt;</c> runs <c>search any &lt;QUERY&gt;</c>, and
/// <c>security &lt;NAME&gt;</c> runs <c>security any &lt;NAME&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both branches demand a sub-command, so <c>search ContosoDeal</c> failed with
/// "Unknown command 'ContosoDeal'" and a hint to read <c>--help</c>. Measured over a month
/// of agent sessions on a real installation, 16 of 46 invocations were such <c>--help</c>
/// lookups. A branch default command is not an option: Spectre 0.49 only runs a branch
/// default when no further token follows, and reports any positional after the branch
/// as an unknown command.
/// </para>
/// <para>
/// The rewrite is deliberately narrow: only when the token straight after the branch is
/// not an option and not one of the branch's own sub-commands. Every strict form parses
/// exactly as before, and a query that happens to equal a sub-command name
/// (<c>search class</c>) stays the sub-command. Kept out of <c>Program.cs</c> so it can
/// be unit-tested, like <see cref="ProfileArgs"/>.
/// </para>
/// </remarks>
public static class ArgumentShapes
{
    /// <summary>
    /// Branches that take a bare name, the sub-command it runs, and the branch's
    /// registered sub-commands. <c>ArgumentShapesTests</c> holds each list to the
    /// registered surface, so a new sub-command cannot be swallowed as a query.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Default, IReadOnlySet<string> SubCommands)> Branches =
        new Dictionary<string, (string, IReadOnlySet<string>)>(StringComparer.OrdinalIgnoreCase)
        {
            ["search"] = ("any", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "class", "table", "edt", "enum", "label", "query", "view", "entity", "report",
                "service", "workflow", "any", "batch", "business-event", "security-policy",
                "configuration-key", "tile", "workspace",
            }),
            ["security"] = ("any", new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "role", "duty", "privilege", "coverage", "any",
            }),
        };

    /// <summary>
    /// Returns <paramref name="args"/> with the default sub-command inserted when the
    /// invocation is a bare <c>&lt;branch&gt; &lt;NAME&gt;</c>; otherwise the same args.
    /// </summary>
    public static string[] Normalize(string[] args)
    {
        if (args.Length < 2) return args;
        if (!Branches.TryGetValue(args[0], out var branch)) return args;

        var next = args[1];
        if (next.StartsWith('-')) return args;           // options, --help: Spectre's call
        if (branch.SubCommands.Contains(next)) return args;

        return [args[0], branch.Default, .. args[1..]];
    }

    /// <summary>
    /// The hint for a parse failure. When a name landed where a branch expects its
    /// sub-command (<c>get ContosoDealTable</c>), say that and name the fix, rather than the
    /// generic "see --help".
    /// </summary>
    public static string HintFor(IReadOnlyList<string> args, string parseMessage)
    {
        const string generic = "Run the command with --help to see the options it actually accepts.";
        if (args.Count < 2 || args[1].StartsWith('-')) return generic;
        if (!parseMessage.StartsWith($"Unknown command '{args[1]}'", StringComparison.OrdinalIgnoreCase)) return generic;
        // Spectre reports a stray positional after a leaf (`version foo`) with the same
        // message; only a branch takes a sub-command.
        if (!IsBranch(args[0])) return generic;

        return $"`{args[0]}` takes a sub-command before the name, e.g. `d365fo {args[0]} <sub-command> {args[1]}`. "
             + $"Run `d365fo {args[0]} --help` to list them.";
    }

    // The trimmed binary cannot reflect over Spectre, so branches are read off the
    // published manifest: a branch is a first word some command path continues past.
    private static bool IsBranch(string word) =>
        Branches.ContainsKey(word)
        || Commands.Agent.SchemaCommand.Commands().Any(c =>
            c.Command.StartsWith(word + " ", StringComparison.OrdinalIgnoreCase));
}
