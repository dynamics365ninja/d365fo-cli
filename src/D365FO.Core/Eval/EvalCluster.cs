namespace D365FO.Core.Eval;

/// <summary>
/// One ranked failure cluster. <see cref="RunIds"/> is the provenance the
/// eval-improver's PR must cite, and <see cref="Dimensions"/> says which axis
/// actually failed so a cluster can be read without re-opening the records.
/// </summary>
public sealed record EvalClusterResult(
    string Classification,
    string CaseId,
    int Count,
    string? SampleNote,
    IReadOnlyList<string> Dimensions,
    IReadOnlyList<string> RunIds,
    DateTimeOffset LatestUtc,
    bool Actionable);

/// <summary>
/// Groups failing runs by <c>(classification, case_id)</c> and ranks by
/// frequency — the "actionable clusters" list the eval-improver agent works
/// from (docs/AGENT_EVAL_LOOP.md §10).
/// </summary>
public static class EvalCluster
{
    /// <summary>Most recent run ids carried on a cluster, so a PR can cite evidence without dumping the corpus.</summary>
    private const int MaxRunIds = 5;

    /// <summary>
    /// Ranks failing runs. A run counts as failing when it missed the golden or
    /// a validator rejected it; <paramref name="expectedReferenceGapCases"/>
    /// (cases tagged <see cref="EvalTriage.KnownReferenceGapTag"/>) are exempt
    /// from the reference-error half, because there the unresolved reference is
    /// the documented consequence of scoring one artifact in isolation rather
    /// than a defect — counting it would bury real clusters under known noise.
    /// </summary>
    /// <param name="onlyActionable">
    /// Keep only <c>TOOL_DEFECT</c>/<c>VALIDATOR_GAP</c>/<c>KNOWLEDGE_GAP</c>.
    /// <c>MODEL_ERROR</c>, <c>ENV_FLAKE</c> and untriaged runs are not fixes.
    /// </param>
    public static IReadOnlyList<EvalClusterResult> Rank(
        IReadOnlyList<EvalCorpusRecord> records,
        IReadOnlyCollection<string>? expectedReferenceGapCases = null,
        bool onlyActionable = false)
    {
        var exempt = expectedReferenceGapCases is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(expectedReferenceGapCases, StringComparer.OrdinalIgnoreCase);

        var clusters = records
            .Select(r => (Record: r, Dimensions: FailedDimensions(r, exempt)))
            .Where(x => x.Dimensions.Count > 0)
            .GroupBy(x => (Classification: x.Record.Classification ?? EvalTriage.Unclassified, x.Record.CaseId))
            .Select(g =>
            {
                var newestFirst = g.OrderByDescending(x => x.Record.TimestampUtc).ToList();
                return new EvalClusterResult(
                    Classification: g.Key.Classification,
                    CaseId: g.Key.CaseId,
                    Count: g.Count(),
                    SampleNote: newestFirst.Select(x => x.Record.Note).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)),
                    Dimensions: newestFirst.SelectMany(x => x.Dimensions).Distinct(StringComparer.Ordinal).OrderBy(d => d, StringComparer.Ordinal).ToList(),
                    RunIds: newestFirst.Take(MaxRunIds).Select(x => x.Record.RunId).ToList(),
                    LatestUtc: newestFirst[0].Record.TimestampUtc,
                    Actionable: EvalTriage.Actionable.Contains(g.Key.Classification, StringComparer.Ordinal));
            })
            .Where(c => !onlyActionable || c.Actionable)
            .OrderByDescending(c => c.Actionable)
            .ThenByDescending(c => c.Count)
            .ThenBy(c => c.CaseId, StringComparer.Ordinal)
            .ToList();

        return clusters;
    }

    private static List<string> FailedDimensions(EvalCorpusRecord r, HashSet<string> exempt)
    {
        var dims = new List<string>();
        if (!r.Score.GoldenMatch) dims.Add("golden");
        if (r.Score.XppClean == false) dims.Add("xpp");
        if (r.Score.ReferencesClean == false && !exempt.Contains(r.CaseId)) dims.Add("references");
        // Same exemption for the compiler: it rejects an unprovisioned sibling or
        // standard object for exactly the reason `validate references` does.
        if (r.Score.BuildClean == false && !exempt.Contains(r.CaseId)) dims.Add("build");
        return dims;
    }
}
