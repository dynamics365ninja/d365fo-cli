using D365FO.Core;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

// `d365fo table-pattern` — the decision layer over `generate table`.
//
// The patterns, their canonical TableGroup and their default fields already existed, but only
// as an argument `generate table --pattern` accepts. An agent choosing a shape had no way to see
// what the choices ARE or what each one implies, so it either guessed a pattern name (and got a
// refusal listing valid values, one round trip wasted) or skipped --pattern entirely and got a
// table with no TableGroup at all.

/// <summary><c>d365fo table-pattern list</c> — every pattern, what it is for, what it presets.</summary>
public sealed class TablePatternListCommand : Command<TablePatternListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output), PatternCatalogAnswers.TableList());
}

/// <summary><c>d365fo table-pattern spec</c> — one pattern in full.</summary>
public sealed class TablePatternSpecCommand : Command<TablePatternSpecCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<PATTERN>")]
        [System.ComponentModel.Description("Pattern name or a known alias — master, setup, config, transactional, worksheet-header, lookup, …")]
        public string Pattern { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            PatternCatalogAnswers.TableSpec(settings.Pattern));
}
