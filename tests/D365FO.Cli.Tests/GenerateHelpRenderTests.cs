using D365FO.Core.ObjectTypes;
using Spectre.Console;

namespace D365FO.Cli.Tests;

/// <summary>
/// `--help` renders every [CommandOption]/[CommandArgument] Description through
/// Spectre.Console markup, where a bare '[' or ']' is parsed as a style tag
/// rather than literal text. A description like "&lt;name&gt;[:&lt;label&gt;]" (unescaped)
/// crashes with "Could not find color or style" instead of printing help —
/// found via `generate view --help` while authoring further eval cases.
/// Literal brackets must be doubled ("[[:&lt;label&gt;]]").
/// </summary>
public class GenerateHelpRenderTests
{
    // Driven off GenerateSurface rather than a second hand-kept list: that table is
    // what the K/E/T coverage report treats as "what the tool can build", so a name
    // in it that no subcommand answers to would silently overstate coverage. Here it
    // fails loudly instead — `generate <name> --help` returns non-zero.
    public static IEnumerable<object[]> GenerateSubcommands =>
        GenerateSurface.All.Select(c => new object[] { c.Name });

    [Theory]
    [MemberData(nameof(GenerateSubcommands))]
    public void Help_renders_without_a_markup_exception(string subcommand)
    {
        // Route through a scoped IAnsiConsole (ConfigureConsole) rather than the
        // global AnsiConsole.Console static — this test class is not the only one
        // that captures console output, and xUnit runs different test classes in
        // parallel by default, so mutating global state here would race with them.
        var writer = new StringWriter();
        var scopedConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });
        var app = CliApp.Build(scopedConsole);

        var exitCode = app.Run(new[] { "generate", subcommand, "--help" });

        Assert.Equal(0, exitCode);
    }

    /// <summary>
    /// The other direction: a subcommand registered in <c>CliApp</c> but missing from
    /// <see cref="GenerateSurface"/> would be invisible to the coverage report — the
    /// tool would build something no leaf tracks. Read off the branch's own help
    /// output so the check cannot be satisfied by editing a second list.
    /// </summary>
    [Fact]
    public void Every_registered_generate_subcommand_has_a_capability_leaf()
    {
        var writer = new StringWriter();
        var scopedConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        Assert.Equal(0, CliApp.Build(scopedConsole).Run(["generate", "--help"]));

        // Only the COMMANDS section: its lines look like
        // "    table            Create a new AxTable." Everything above it is usage
        // and options, where a bare word would be an argument name, not a subcommand.
        var help = writer.ToString().Replace("\r\n", "\n");
        var commandsAt = help.IndexOf("COMMANDS:", StringComparison.OrdinalIgnoreCase);
        Assert.True(commandsAt >= 0, "`generate --help` printed no COMMANDS section:\n" + help);

        // Descriptions wrap onto continuation lines that are indented further, and a
        // wrapped word can look exactly like a command name. Only the shallowest
        // indent in the section is the command column.
        var candidates = System.Text.RegularExpressions.Regex
            // "    extension <KIND> <TARGET>        Create a Table/Form/Edt/Enum extension"
            .Matches(help[commandsAt..], @"(?m)^( +)([a-z][a-z0-9-]*)((?: <[A-Z_]+>)*)\s{2,}\S")
            .Select(m => (Indent: m.Groups[1].Value.Length, Name: m.Groups[2].Value))
            .ToList();

        Assert.NotEmpty(candidates);
        var commandColumn = candidates.Min(c => c.Indent);
        var listed = candidates
            .Where(c => c.Indent == commandColumn)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(listed);
        var missing = listed.Except(GenerateSurface.All.Select(c => c.Name), StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"Registered but absent from GenerateSurface: {string.Join(", ", missing)}");
    }
}
