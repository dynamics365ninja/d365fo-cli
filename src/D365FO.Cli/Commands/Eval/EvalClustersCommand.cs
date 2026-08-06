using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>Ranked failure clusters over the corpus — what the eval-improver agent works from.</summary>
public sealed class EvalClustersCommand : Command<EvalClustersCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--actionable")]
        [System.ComponentModel.Description("Only TOOL_DEFECT / VALIDATOR_GAP / KNOWLEDGE_GAP — the classes that become a fix.")]
        public bool Actionable { get; init; }

        [CommandOption("--top <N>")]
        [System.ComponentModel.Description("Keep only the N highest-ranked clusters.")]
        public int? Top { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        var records = EvalCorpusStore.ReadAll(EvalPaths.CorpusRunsDir(root!));
        var (cases, _) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));

        // Cases whose artifact legitimately references something a minimal offline
        // index cannot contain report referencesClean:false by design. Ranking those
        // as failures would bury every real cluster under known, documented noise.
        var expectedReferenceGaps = cases
            .Where(c => c.Tags.Contains(EvalTriage.KnownReferenceGapTag, StringComparer.OrdinalIgnoreCase))
            .Select(c => c.Id)
            .ToList();

        var clusters = EvalCluster.Rank(records, expectedReferenceGaps, settings.Actionable);
        if (settings.Top is int top && top > 0) clusters = clusters.Take(top).ToList();

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            clusters = clusters.Select(c => new
            {
                classification = c.Classification,
                caseId = c.CaseId,
                count = c.Count,
                actionable = c.Actionable,
                dimensions = c.Dimensions,
                sampleNote = c.SampleNote,
                runIds = c.RunIds,
                latestUtc = c.LatestUtc,
            }),
            count = clusters.Count,
            corpusRuns = records.Count,
            expectedReferenceGapCases = expectedReferenceGaps.Count,
        }));
    }
}
