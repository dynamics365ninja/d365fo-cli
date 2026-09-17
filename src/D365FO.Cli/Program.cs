using D365FO.Cli;
using Spectre.Console.Cli;

// Global `--profile <name>` (issue #210). Spectre.Console.Cli has no real
// global options, so it is peeled off here before parsing. The name is also
// exported as D365FO_PROFILE so child processes (daemon, bridge, eval
// sub-runs) resolve against the same profile.
var (cliArgs, profileError) = ProfileArgs.Apply(args);
if (profileError is not null)
{
    Console.Error.WriteLine(D365FO.Core.D365Json.Serialize(D365FO.Core.ToolResult<object>.Fail(
        profileError.Code, profileError.Message, profileError.Hint)));
    return 2;
}

var app = CliApp.Build();

try
{
    return await app.RunAsync(cliArgs);
}
catch (CommandParseException ex)
{
    // StrictParsing is on (see CliApp.Build), so an unknown/misspelled option
    // lands here instead of being silently swallowed. Render it as a normal
    // BAD_INPUT tool result rather than an UNHANDLED crash, so an agent reading
    // the JSON gets the same shape it gets for every other input mistake.
    Console.Error.WriteLine(D365FO.Core.D365Json.Serialize(
        D365FO.Core.ToolResult<object>.Fail(
            "BAD_INPUT",
            ex.Message,
            "Run the command with --help to see the options it actually accepts.")));
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine(D365FO.Core.D365Json.Serialize(
        D365FO.Core.ToolResult<object>.Fail("UNHANDLED", ex.Message, ex.GetType().FullName)));
    return 2;
}
