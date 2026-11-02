namespace D365FO.Cli;

/// <summary>
/// The global <c>--no-refresh</c> flag: skip re-indexing the model a write touched.
/// </summary>
/// <remarks>
/// Global rather than declared on each write command, because there are dozens of them
/// (every <c>generate</c> leaf, <c>modify</c>, <c>delete</c>, <c>undo</c>, the label writers) and a flag
/// one of them forgot would be accepted by its siblings and rejected by it. On a command that
/// writes nothing it changes nothing, which is also what it promises.
/// </remarks>
public static class AutoRefreshArgs
{
    public const string Flag = "--no-refresh";

    /// <summary>
    /// Remove every <c>--no-refresh</c> token before a literal <c>--</c> (everything after it is left
    /// alone, as <c>--profile</c> does). Returns the remaining arguments and whether it was present.
    /// </summary>
    public static (string[] Args, bool NoRefresh) Strip(string[] args)
    {
        var rest = new List<string>(args.Length);
        var found = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--")
            {
                rest.AddRange(args[i..]);
                break;
            }
            if (string.Equals(args[i], Flag, StringComparison.OrdinalIgnoreCase)) { found = true; continue; }
            rest.Add(args[i]);
        }
        return (rest.ToArray(), found);
    }
}
