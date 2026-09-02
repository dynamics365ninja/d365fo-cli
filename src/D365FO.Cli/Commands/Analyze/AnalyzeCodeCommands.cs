using D365FO.Core.Analysis;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Analyze;

// The three `analyze` modes the gap analysis lists as missing: learn from the installation
// instead of from training data. All three read the same corpus of X++ method bodies and all
// three report how much of it they could actually see — an empty answer from an unread corpus
// is not the same answer as an empty one from a read corpus, and only one of them means "no".

/// <summary><c>d365fo analyze patterns</c> — which APIs a scenario usually reaches for.</summary>
public sealed class AnalyzePatternsCommand : Command<AnalyzePatternsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<SCENARIO>")]
        [System.ComponentModel.Description("What you are about to do, in the words the code would use: \"number sequence\", \"posting\", \"batch job\".")]
        public string Scenario { get; init; } = "";

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Restrict to one model — e.g. your own, to see how THIS codebase does it.")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 20;
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            CodeAnalysis.Patterns(RepoFactory.Create(), settings.Scenario, settings.Model, settings.Limit));
}

/// <summary><c>d365fo analyze implementations</c> — who else implements this method.</summary>
public sealed class AnalyzeImplementationsCommand : Command<AnalyzeImplementationsCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<METHOD>")]
        [System.ComponentModel.Description("Method name, e.g. validateWrite. Read before writing an override or a CoC wrapper.")]
        public string Method { get; init; } = "";

        [CommandOption("--model <NAME>")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 20;
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            CodeAnalysis.Implementations(RepoFactory.Create(), settings.Method, settings.Model, settings.Limit));
}

/// <summary><c>d365fo analyze api-usage</c> — how an API is constructed and called.</summary>
public sealed class AnalyzeApiUsageCommand : Command<AnalyzeApiUsageCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<API>")]
        [System.ComponentModel.Description("Class or API name, e.g. NumberSeq, DocumentManagement, SysMailerFactory.")]
        public string Api { get; init; } = "";

        [CommandOption("--model <NAME>")]
        public string? Model { get; init; }

        [CommandOption("-l|--limit <N>")]
        public int Limit { get; init; } = 25;
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            CodeAnalysis.ApiUsage(RepoFactory.Create(), settings.Api, settings.Model, settings.Limit));
}
