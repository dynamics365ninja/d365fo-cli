using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests.Eval;

public class EvalCorpusStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"eval-corpus-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private static EvalScoreCard Score(bool goldenMatch, bool? xppClean = true, bool? refsClean = true) => new(
        XppClean: xppClean, XppErrors: xppClean == false ? 1 : 0,
        ReferencesClean: refsClean, ReferenceErrors: refsClean == false ? 1 : 0,
        GoldenMatch: goldenMatch,
        GoldenDiff: new XmlGoldenDiff(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<XmlGoldenChange>()));

    private static EvalCorpusRecord Record(string caseId, int tier, bool goldenMatch, string? classification = null, DateTimeOffset? ts = null)
        => new(
            RunId: $"{caseId}-{Guid.NewGuid():N}", CaseId: caseId, Tier: tier,
            TimestampUtc: ts ?? DateTimeOffset.UtcNow, Source: "replay",
            Score: Score(goldenMatch), Classification: classification, Note: null);

    [Fact]
    public void Append_then_ReadAll_round_trips_every_field()
    {
        var record = Record("L0-edt-basic", 0, goldenMatch: true, classification: "TOOL_DEFECT");

        EvalCorpusStore.Append(_dir, record);
        var read = Assert.Single(EvalCorpusStore.ReadAll(_dir));

        Assert.Equal(record.RunId, read.RunId);
        Assert.Equal(record.CaseId, read.CaseId);
        Assert.Equal(record.Tier, read.Tier);
        Assert.Equal(record.Source, read.Source);
        Assert.Equal(record.Classification, read.Classification);
        Assert.Equal(record.Score.GoldenMatch, read.Score.GoldenMatch);
    }

    [Fact]
    public void ReadAll_on_a_missing_directory_returns_empty_without_throwing()
    {
        Assert.Empty(EvalCorpusStore.ReadAll(Path.Combine(_dir, "does-not-exist")));
    }

    [Fact]
    public void ReadAll_skips_a_malformed_record_and_keeps_the_rest()
    {
        EvalCorpusStore.Append(_dir, Record("L0-good", 0, goldenMatch: true));
        File.WriteAllText(Path.Combine(_dir, "zzz-corrupt.json"), "{ not json");

        var records = EvalCorpusStore.ReadAll(_dir);

        var good = Assert.Single(records);
        Assert.Equal("L0-good", good.CaseId);
    }

    [Fact]
    public void EvalReport_aggregates_pass_rates_per_tier_and_classification_counts()
    {
        var records = new[]
        {
            Record("L0-edt-basic", 0, goldenMatch: true),
            Record("L0-enum-basic", 0, goldenMatch: false, classification: "TOOL_DEFECT"),
            Record("L1-table-basic", 1, goldenMatch: true),
            Record("L2-coc-extension", 2, goldenMatch: false, classification: "MODEL_ERROR"),
        };

        var report = EvalReport.Build(records);

        Assert.Equal(4, report.TotalRuns);
        Assert.Equal(0.5, report.GoldenPassRate);

        var tier0 = Assert.Single(report.ByTier, t => t.Tier == 0);
        Assert.Equal(2, tier0.Total);
        Assert.Equal(1, tier0.PassGolden);

        Assert.Equal(1, report.ClassificationCounts["TOOL_DEFECT"]);
        Assert.Equal(1, report.ClassificationCounts["MODEL_ERROR"]);
    }

    [Fact]
    public void EvalReport_on_empty_corpus_has_zero_pass_rate_not_NaN()
    {
        var report = EvalReport.Build(Array.Empty<EvalCorpusRecord>());

        Assert.Equal(0, report.TotalRuns);
        Assert.Equal(0.0, report.GoldenPassRate);
        Assert.Empty(report.ByTier);
    }

    [Fact]
    public void EvalCluster_only_surfaces_failing_runs_ranked_by_frequency()
    {
        var records = new List<EvalCorpusRecord>
        {
            Record("L0-edt-basic", 0, goldenMatch: true), // passing — must not appear in any cluster
            Record("L0-enum-basic", 0, goldenMatch: false, classification: "TOOL_DEFECT"),
            Record("L0-enum-basic", 0, goldenMatch: false, classification: "TOOL_DEFECT"),
            Record("L2-coc-extension", 2, goldenMatch: false, classification: "MODEL_ERROR"),
        };

        var clusters = EvalCluster.Rank(records);

        Assert.Equal(2, clusters.Count);
        Assert.Equal("L0-enum-basic", clusters[0].CaseId); // higher frequency (2) ranks first
        Assert.Equal(2, clusters[0].Count);
        Assert.Equal("TOOL_DEFECT", clusters[0].Classification);
        Assert.DoesNotContain(clusters, c => c.CaseId == "L0-edt-basic");
    }

    [Fact]
    public void EvalCluster_groups_untriaged_failures_under_UNCLASSIFIED()
    {
        var records = new[] { Record("L1-table-basic", 1, goldenMatch: false, classification: null) };

        var cluster = Assert.Single(EvalCluster.Rank(records));

        Assert.Equal("UNCLASSIFIED", cluster.Classification);
    }
}
