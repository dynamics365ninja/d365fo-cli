using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>Aggregate scoreboard over the corpus of run records (eval/corpus/runs/).</summary>
public sealed class EvalReportCommand : Command<EvalReportCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        var records = EvalCorpusStore.ReadAll(EvalPaths.CorpusRunsDir(root!));
        var report = EvalReport.Build(records);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            totalRuns = report.TotalRuns,
            goldenPassRate = report.GoldenPassRate,
            byTier = report.ByTier,
            classificationCounts = report.ClassificationCounts,
        }));
    }
}
