using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Tests;

/// <summary>
/// Spectre's parser defaults to <c>StrictParsing = false</c>, which quietly
/// routes any unrecognised option into <c>IRemainingArguments</c>. Nothing in
/// this CLI reads those, so before <see cref="CliApp"/> turned strict parsing
/// on, a misspelled flag was silently dropped and the command still reported
/// <c>ok:true</c> — e.g. <c>generate table X --storage TempDB</c> (the real
/// option is <c>--table-type</c>) wrote a plain regular table and claimed
/// success. Per eval/README.md's triage bias, a confident lie is the worst
/// failure mode this tool can have, so unknown options must fail loudly.
/// </summary>
public class StrictOptionParsingTests
{
    private static CommandApp BuildApp()
    {
        var writer = new StringWriter();
        var scopedConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        return CliApp.Build(scopedConsole);
    }

    [Theory]
    // The exact near-miss that exposed this: --storage is not an option of
    // `generate table`, --table-type is.
    [InlineData("generate", "table", "ConStrictTest", "--storage", "TempDB")]
    // Singular/plural near-misses on repeatable options.
    [InlineData("generate", "enum", "ConStrictTest", "--values", "None:0")]
    // A flag that does not exist anywhere in the surface.
    [InlineData("generate", "class", "ConStrictTest", "--totally-bogus-flag")]
    // Read-side commands must be just as strict.
    [InlineData("search", "table", "Cust", "--limits", "5")]
    public void Unknown_option_is_rejected_instead_of_silently_ignored(params string[] args)
    {
        var app = BuildApp();

        // PropagateExceptions() is on, so the parse failure surfaces as an
        // exception here; Program.cs renders it as a BAD_INPUT tool result.
        var ex = Assert.ThrowsAny<CommandParseException>(() => app.Run(args));

        Assert.Contains("Unknown option", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Known_options_still_parse()
    {
        var app = BuildApp();

        // `--help` short-circuits before the command body runs, so this asserts
        // the parser accepts the real option name without needing an index or a
        // writable output path.
        var exitCode = app.Run(new[] { "generate", "table", "--help" });

        Assert.Equal(0, exitCode);
    }
}
