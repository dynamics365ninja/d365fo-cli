using Spectre.Console;

namespace D365FO.Cli;

/// <summary>
/// Picks the console the CLI renders help and tables through.
/// </summary>
/// <remarks>
/// Spectre decides ANSI support from the terminal type, not from where stdout goes, so
/// under Git Bash or any <c>TERM=xterm*</c> shell <c>d365fo search --help &gt; help.txt</c>
/// wrote colour escapes into the file. Agents capture output exactly that way and pay for
/// every escape sequence as tokens. Output that is redirected, or a caller that asks for no
/// colour (<c>NO_COLOR</c>, <c>TERM=dumb</c>), now gets plain text. An interactive terminal
/// is unchanged.
/// </remarks>
public static class ConsoleSetup
{
    /// <summary>
    /// True when output must be plain: stdout is redirected, <c>NO_COLOR</c> is set to any
    /// non-empty value (https://no-color.org), or <c>TERM</c> is <c>dumb</c>.
    /// </summary>
    public static bool PlainOutputWanted(bool stdoutRedirected, string? noColor, string? term) =>
        stdoutRedirected
        || !string.IsNullOrEmpty(noColor)
        || string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);

    /// <summary>A console that writes to <paramref name="writer"/> without any ANSI escape.</summary>
    /// <remarks>
    /// Spectre's default profile enrichers run after <c>Ansi = No</c> is applied and turn ANSI
    /// back on under CI hosts (<c>GITHUB_ACTIONS</c>, <c>TF_BUILD</c>, …), which is exactly
    /// where output is captured. They are switched off, and the capabilities pinned, so the
    /// answer does not depend on the host environment.
    /// </remarks>
    public static IAnsiConsole Plain(TextWriter writer)
    {
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        console.Profile.Capabilities.Ansi = false;
        console.Profile.Capabilities.Links = false;
        return console;
    }

    /// <summary>
    /// The plain console for this process when <see cref="PlainOutputWanted"/> says so,
    /// otherwise null (keep Spectre's own detection).
    /// </summary>
    public static IAnsiConsole? ForThisProcess() =>
        PlainOutputWanted(
            Console.IsOutputRedirected,
            Environment.GetEnvironmentVariable("NO_COLOR"),
            Environment.GetEnvironmentVariable("TERM"))
            ? Plain(Console.Out)
            : null;
}
