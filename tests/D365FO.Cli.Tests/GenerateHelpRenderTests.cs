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
    public static IEnumerable<object[]> GenerateSubcommands => new[]
    {
        "table", "class", "coc", "form", "datasource-method", "control-method", "simple-list",
        "entity", "extension", "event-handler", "privilege", "duty", "role", "report",
        "sysoperation", "number-sequence", "workflow", "menu-item", "edt", "enum", "query",
        "view", "map", "business-event", "custom-service", "migration-script", "runbase",
        "security-policy", "systest",
    }.Select(name => new object[] { name });

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
}
