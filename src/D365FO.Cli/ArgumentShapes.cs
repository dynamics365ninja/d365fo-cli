namespace D365FO.Cli;

/// <summary>
/// Accepts the argument shapes callers reach for first, before Spectre parses them:
/// <c>search &lt;QUERY&gt;</c> runs <c>search any &lt;QUERY&gt;</c>,
/// <c>security &lt;NAME&gt;</c> runs <c>security any &lt;NAME&gt;</c>,
/// <c>search form|menu-item|map &lt;NAME&gt;</c> runs <c>search any &lt;NAME&gt; --kind &lt;kind&gt;</c>,
/// and <c>get class|table|form &lt;NAME&gt; --source</c> runs <c>read class|table|form &lt;NAME&gt;</c>.
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
/// <para>
/// The kind and <c>--source</c> rewrites come from the same kind of evidence: commands agents
/// guessed in real sessions. <c>search form CustTable</c> used to become
/// <c>search any form CustTable</c>, which failed on the stray second positional, and
/// <c>get class X --source</c> failed on the unknown option. Both now run the command that
/// answers them, and every option they carry is passed on to it — never dropped.
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
    /// Kinds <c>search any --kind</c> filters on that have no <c>search</c> sub-command of their
    /// own, keyed by the spelling a caller types and mapped to the <c>--kind</c> value.
    /// <c>ArgumentShapesTests</c> holds every value to the kinds the repository can filter on.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> SearchAnyKinds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["form"] = "form",
            ["menu-item"] = "menuitem",
            ["menuitem"] = "menuitem",
            ["map"] = "map",
        };

    /// <summary><c>get</c> sub-commands whose full source <c>read</c> returns.</summary>
    private static readonly HashSet<string> SourceKinds = new(StringComparer.OrdinalIgnoreCase) { "class", "table", "form" };

    /// <summary>
    /// Returns <paramref name="args"/> with the default sub-command inserted when the
    /// invocation is a bare <c>&lt;branch&gt; &lt;NAME&gt;</c>, or rewritten to the command that
    /// answers a guessed shape (see the type remarks); otherwise the same args.
    /// </summary>
    public static string[] Normalize(string[] args)
    {
        if (args.Length < 2) return args;

        if (SourceRead(args) is { } read) return read;
        if (KindSearch(args) is { } search) return search;

        if (!Branches.TryGetValue(args[0], out var branch)) return args;

        var next = args[1];
        if (next.StartsWith('-')) return args;           // options, --help: Spectre's call
        if (branch.SubCommands.Contains(next)) return args;

        return [args[0], branch.Default, .. args[1..]];
    }

    /// <summary>
    /// <c>search form ContosoDeal --limit 5</c> → <c>search any ContosoDeal --limit 5 --kind form</c>.
    /// Only with a name after the kind (a bare <c>search form</c> still searches for "form"), and
    /// never when the caller passed <c>--kind</c> too: two filters is a question to refuse, not
    /// one to answer by dropping either.
    /// </summary>
    private static string[]? KindSearch(string[] args)
    {
        if (args.Length < 3) return null;
        if (!string.Equals(args[0], "search", StringComparison.OrdinalIgnoreCase)) return null;
        if (!SearchAnyKinds.TryGetValue(args[1], out var kind)) return null;
        if (args[2].StartsWith('-')) return null;
        if (args.Any(a => a.StartsWith("--kind", StringComparison.OrdinalIgnoreCase))) return null;

        return ["search", "any", .. args[2..], "--kind", kind];
    }

    /// <summary>
    /// <c>get class X --source</c> → <c>read class X</c>. <c>get</c> answers with metadata and
    /// <c>read</c> with the X++, so the flag names the other command. Every other option is passed
    /// on, so one <c>read</c> does not accept fails loudly there.
    /// </summary>
    private static string[]? SourceRead(string[] args)
    {
        if (args.Length < 3) return null;
        if (!string.Equals(args[0], "get", StringComparison.OrdinalIgnoreCase)) return null;
        if (!SourceKinds.Contains(args[1])) return null;

        var rest = args[2..];
        if (!rest.Any(IsSourceFlag)) return null;

        return ["read", args[1].ToLowerInvariant(), .. rest.Where(a => !IsSourceFlag(a))];
    }

    private static bool IsSourceFlag(string arg) => string.Equals(arg, "--source", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The hint for a parse failure. When the parser rejected a sub-command (or a name landed
    /// where a branch expects its sub-command, as in <c>get ContosoDealTable</c>), the hint lists
    /// the sub-commands that do exist, inline, with the closest one first when there is one —
    /// so the next call can be the right one rather than a <c>--help</c> lookup.
    /// </summary>
    public static string HintFor(IReadOnlyList<string> args, string parseMessage)
    {
        const string generic = "Run the command with --help to see the options it actually accepts.";
        if (args.Count == 0 || args[0].StartsWith('-')) return generic;

        // An unknown top-level command.
        if (IsUnknownCommand(parseMessage, args[0]) && !IsKnownTopLevel(args[0]))
        {
            var top = TopLevelCommands();
            return $"`{args[0]}` is not a d365fo command.{DidYouMean(args[0], top, w => $"d365fo {w}")} "
                 + $"Commands: {string.Join(", ", top)}.";
        }

        if (args.Count < 2 || args[1].StartsWith('-')) return generic;
        if (!IsUnknownCommand(parseMessage, args[1])) return generic;

        // Spectre reports a stray positional after a leaf (`version foo`) with the same
        // message; only a branch takes a sub-command.
        var subs = SubCommandsOf(args[0]);
        if (subs.Count == 0) return generic;

        return $"`{args[0]}` has no sub-command '{args[1]}'.{DidYouMean(args[1], subs, s => $"d365fo {args[0]} {s}")} "
             + $"Sub-commands of `{args[0]}`: {string.Join(", ", subs)}. "
             + $"If '{args[1]}' is a name, put the sub-command before it: `d365fo {args[0]} <sub-command> {args[1]}`. "
             + $"`d365fo {args[0]} <sub-command> --help` shows one sub-command's arguments.";
    }

    private static bool IsUnknownCommand(string parseMessage, string token) =>
        parseMessage.StartsWith($"Unknown command '{token}'", StringComparison.OrdinalIgnoreCase);

    // The trimmed binary cannot reflect over Spectre, so the surface is read off the
    // published manifest: a branch is a first word some command path continues past.
    private static IReadOnlyList<string> SubCommandsOf(string branch)
    {
        var subs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Branches.TryGetValue(branch, out var known)) subs.UnionWith(known.SubCommands);
        foreach (var c in Commands.Agent.SchemaCommand.Commands())
        {
            var words = c.Command.Split(' ');
            if (words.Length >= 2 && string.Equals(words[0], branch, StringComparison.OrdinalIgnoreCase))
                subs.Add(words[1]);
        }
        return subs.ToList();
    }

    private static IReadOnlyList<string> TopLevelCommands() =>
        Commands.Agent.SchemaCommand.Commands()
            .Select(c => c.Command.Split(' ')[0])
            .Concat(Branches.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(w => w, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool IsKnownTopLevel(string word) =>
        TopLevelCommands().Contains(word, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// " Did you mean `…`?" for the closest candidate, or empty when nothing is close: within an
    /// edit distance of a third of the word (at least 1, at most 3), or a candidate that contains
    /// the word or is contained in it (<c>field</c> → <c>fields</c>, <c>tables-by-field</c>).
    /// </summary>
    internal static string DidYouMean(string word, IReadOnlyList<string> candidates, Func<string, string> render)
    {
        var w = word.ToLowerInvariant();
        var budget = Math.Clamp(w.Length / 3, 1, 3);

        var best = candidates
            .Select(c => (Candidate: c, Distance: EditDistance(w, c.ToLowerInvariant())))
            .Where(x => x.Distance <= budget
                        || (w.Length >= 4 && (x.Candidate.Contains(w, StringComparison.OrdinalIgnoreCase)
                                              || w.Contains(x.Candidate, StringComparison.OrdinalIgnoreCase)
                                                 && x.Candidate.Length >= 4)))
            .OrderBy(x => x.Distance)
            .ThenBy(x => x.Candidate, StringComparer.Ordinal)
            .Select(x => x.Candidate)
            .FirstOrDefault();

        return best is null ? "" : $" Did you mean `{render(best)}`?";
    }

    /// <summary>Levenshtein distance, two rows.</summary>
    internal static int EditDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
