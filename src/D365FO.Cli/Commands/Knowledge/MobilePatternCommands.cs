using D365FO.Core;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

// `d365fo mobile-pattern` — warehouse scanner screens.
//
// The list deliberately leads with the framework decision rather than the recipes, because that
// is the one choice the platform will not let you take back cheaply: the same screens are built
// by two frameworks, and building on the wrong one is a rewrite.

/// <summary><c>d365fo mobile-pattern list</c> — the framework decision, then the recipes.</summary>
public sealed class MobilePatternListCommand : Command<MobilePatternListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--framework <NAME>")]
        [System.ComponentModel.Description("Show only one framework's recipes: process-guide | work-execute-display | configuration.")]
        public string? Framework { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            PatternCatalogAnswers.MobileList(settings.Framework));
}

/// <summary><c>d365fo mobile-pattern spec</c> — one recipe in full.</summary>
public sealed class MobilePatternSpecCommand : Command<MobilePatternSpecCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<RECIPE>")]
        [System.ComponentModel.Description("Recipe id: processguide-flow | processguide-page-control | processguide-page-replace | processguide-step-insert | app-step-identity | legacy-workexecutedisplay | gs1-scan-input")]
        public string Recipe { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            PatternCatalogAnswers.MobileSpec(settings.Recipe));
}
