using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>--help</c> of every registered command and branch renders.
/// </summary>
/// <remarks>
/// <c>modify batch --help</c> crashed with "Encountered malformed markup tag at position 137":
/// the <c>--operations</c> description held a literal JSON array, and Spectre renders option
/// descriptions as markup, where <c>[…]</c> is a style tag. <c>GenerateHelpRenderTests</c> only
/// covers the <c>generate</c> sub-commands, so every other branch could ship the same crash.
/// This walks the registered surface instead of a list, so a new command is covered the day it
/// is added.
/// </remarks>
public class CommandHelpRenderTests
{
    public static TheoryData<string> EveryCommandPath()
    {
        var data = new TheoryData<string>();
        var leaves = CliSurface.Leaves().Select(l => l.Path).ToList();
        // The branches render their own help (the COMMANDS table) too.
        var branches = leaves.Where(p => p.Contains(' ')).Select(p => p[..p.LastIndexOf(' ')]).Distinct();
        foreach (var path in leaves.Concat(branches).Distinct(StringComparer.Ordinal)) data.Add(path);
        return data;
    }

    // A scoped console rather than the AnsiConsole.Console static: other test classes capture
    // console output in parallel (see GenerateHelpRenderTests).
    private static CommandApp BuildApp(StringWriter help) => CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
        Out = new AnsiConsoleOutput(help),
    }));

    [Theory]
    [MemberData(nameof(EveryCommandPath))]
    public void Help_of_every_command_renders_without_a_markup_exception(string path)
    {
        var help = new StringWriter();

        var exit = BuildApp(help).Run([.. path.Split(' '), "--help"]);

        Assert.Equal(0, exit);
        Assert.Contains("USAGE", help.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Modify_batch_help_shows_the_json_example_literally()
    {
        var help = new StringWriter();

        Assert.Equal(0, BuildApp(help).Run(["modify", "batch", "--help"]));

        // The description wraps across lines; the escaped brackets must come out as plain `[{`.
        var flat = string.Concat(help.ToString().Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("[{\"operation\":\"add-field\"", flat);
    }
}
