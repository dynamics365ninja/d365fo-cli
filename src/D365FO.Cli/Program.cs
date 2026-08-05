using D365FO.Cli;
using Spectre.Console.Cli;

var app = CliApp.Build();

try
{
    return await app.RunAsync(args);
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
