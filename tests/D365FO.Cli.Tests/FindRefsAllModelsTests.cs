using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>find refs --all-models</c> widens the text scan to every model. It means nothing to the
/// xref query or next to <c>--model</c>, so it is refused there rather than quietly dropped.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class FindRefsAllModelsTests
{
    [Theory]
    [InlineData("--xref")]
    [InlineData("--model", "TestModel")]
    public void All_models_is_refused_where_it_cannot_be_honoured(params string[] extra)
    {
        var args = new[] { "find", "refs", "FmVehicleService", "--all-models", "--output", "json" }.Concat(extra).ToArray();

        var output = Run(args, out var code);

        Assert.NotEqual(0, code);
        Assert.Contains("BAD_INPUT", output, StringComparison.Ordinal);
        Assert.Contains("all-models", output, StringComparison.OrdinalIgnoreCase);
    }

    private static string Run(string[] args, out int code)
    {
        var app = CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
        }));
        var originalOut = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            code = app.Run(args);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return writer.ToString();
    }
}
