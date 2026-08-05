namespace D365FO.Core.Eval;

public sealed record EvalClusterResult(string Classification, string CaseId, int Count, string? SampleNote);

/// <summary>
/// Groups failing runs by <c>(classification, case_id)</c> and ranks by
/// frequency — the "actionable clusters" list the eval-improver agent works
/// from (docs/AGENT_EVAL_LOOP.md).
/// </summary>
public static class EvalCluster
{
    public static IReadOnlyList<EvalClusterResult> Rank(IReadOnlyList<EvalCorpusRecord> records)
        => records
            .Where(r => !r.Score.GoldenMatch || r.Score.XppClean == false || r.Score.ReferencesClean == false)
            .GroupBy(r => (Classification: r.Classification ?? "UNCLASSIFIED", r.CaseId))
            .Select(g => new EvalClusterResult(
                g.Key.Classification,
                g.Key.CaseId,
                g.Count(),
                g.OrderByDescending(r => r.TimestampUtc).First().Note))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.CaseId, StringComparer.Ordinal)
            .ToList();
}
