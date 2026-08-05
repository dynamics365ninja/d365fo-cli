namespace D365FO.Core.Eval;

public sealed record EvalTierStat(int Tier, int Total, int PassXpp, int PassReferences, int PassGolden);

public sealed record EvalReportResult(
    int TotalRuns,
    IReadOnlyList<EvalTierStat> ByTier,
    double GoldenPassRate,
    IReadOnlyDictionary<string, int> ClassificationCounts);

/// <summary>Aggregate scoreboard over the full corpus (all runs, not just the latest per case — a rolling history).</summary>
public static class EvalReport
{
    public static EvalReportResult Build(IReadOnlyList<EvalCorpusRecord> records)
    {
        var byTier = records
            .GroupBy(r => r.Tier)
            .OrderBy(g => g.Key)
            .Select(g => new EvalTierStat(
                Tier: g.Key,
                Total: g.Count(),
                PassXpp: g.Count(r => r.Score.XppClean == true),
                PassReferences: g.Count(r => r.Score.ReferencesClean == true),
                PassGolden: g.Count(r => r.Score.GoldenMatch)))
            .ToList();

        var goldenPassRate = records.Count == 0
            ? 0.0
            : records.Count(r => r.Score.GoldenMatch) / (double)records.Count;

        var classificationCounts = records
            .GroupBy(r => r.Classification ?? "UNCLASSIFIED")
            .ToDictionary(g => g.Key, g => g.Count());

        return new EvalReportResult(records.Count, byTier, goldenPassRate, classificationCounts);
    }
}
