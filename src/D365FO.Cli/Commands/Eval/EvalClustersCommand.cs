using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>Ranked failure clusters over the corpus — what the eval-improver agent works from.</summary>
public sealed class EvalClustersCommand : Command<EvalClustersCommand.Settings>
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
        var clusters = EvalCluster.Rank(records);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            clusters = clusters.Select(c => new
            {
                classification = c.Classification,
                caseId = c.CaseId,
                count = c.Count,
                sampleNote = c.SampleNote,
            }),
            count = clusters.Count,
        }));
    }
}
