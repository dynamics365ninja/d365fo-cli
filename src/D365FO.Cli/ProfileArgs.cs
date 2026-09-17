using D365FO.Core;

namespace D365FO.Cli;

/// <summary>
/// Entry-point handling of the global <c>--profile &lt;name&gt;</c> option and the
/// "selected profile must exist" guardrail (issue #210). Kept out of
/// <c>Program.cs</c> so it can be unit-tested without re-entering the Spectre
/// app in-process.
/// </summary>
public static class ProfileArgs
{
    /// <summary>
    /// Commands that are allowed to run while the selected profile does not
    /// exist: the ones that create/select profiles, plus diagnostics and help,
    /// which must keep working so the user can find out what is wrong.
    /// </summary>
    private static readonly HashSet<string> MissingProfileAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "init", "doctor", "version", "help", "completion",
    };

    /// <summary>
    /// Strip <c>--profile</c> from <paramref name="args"/>, publish the selection
    /// (<see cref="D365FoProfiles.FlagProfile"/> + the <c>D365FO_PROFILE</c> process
    /// env var) and run the missing-profile guardrail. Returns the args Spectre
    /// should parse, or an error to print instead of running anything.
    /// </summary>
    public static (string[] Args, ToolError? Error) Apply(string[] args)
    {
        var (rest, profile, parseError) = D365FoProfiles.StripProfileArg(args);
        if (parseError is not null)
            return (rest, new ToolError(D365FoErrorCodes.BadInput, parseError,
                "Usage: d365fo --profile <name> <command> ... (see 'd365fo config list')."));

        if (profile is not null)
        {
            D365FoProfiles.FlagProfile = profile;
            if (D365FoProfiles.IsValidName(profile))
                Environment.SetEnvironmentVariable(D365FoProfiles.ProfileKey, profile);
        }

        if (IsExempt(rest)) return (rest, null);
        return (rest, D365FoProfiles.CheckActive());
    }

    /// <summary>
    /// True when the invocation is help/version output or a command in
    /// <see cref="MissingProfileAllowed"/>. The command is the first token
    /// that is not an option — Spectre accepts no options before it except
    /// help/version.
    /// </summary>
    internal static bool IsExempt(IReadOnlyList<string> args)
    {
        if (args.Any(a => a is "-h" or "--help" or "-?")) return true;
        foreach (var a in args)
        {
            if (a is "-v" or "--version") return true;
            if (a.StartsWith('-')) continue;
            return MissingProfileAllowed.Contains(a);
        }
        return true; // bare `d365fo` prints help
    }
}
