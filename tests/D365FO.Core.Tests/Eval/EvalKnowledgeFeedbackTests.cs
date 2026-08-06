using D365FO.Core.Eval;
using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// The MODEL_ERROR / KNOWLEDGE_GAP → skills/_source feedback path (audit finding K4).
/// </summary>
public class EvalKnowledgeFeedbackTests
{
    private static EvalCase Case(string id, string[] targets, string[]? args = null) => new(
        Id: id, Title: id, Tier: 1, Instruction: $"Do {id}.",
        CanonicalArgs: args, TargetArtifactTypes: targets, GoldenPath: id,
        Tags: [], Ignore: [], RequiresFixtureIndex: false, GoldenPending: false);

    private static EvalCorpusRecord Record(string caseId, string? classification, string? note = null, int minutesAgo = 0) => new(
        RunId: $"{caseId}-{Guid.NewGuid():N}", CaseId: caseId, Tier: 1,
        TimestampUtc: DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), Source: "agent",
        Score: new EvalScoreCard(true, 0, true, 0, false, XmlGoldenDiff.Empty),
        Classification: classification, Note: note);

    private static readonly KnowledgeTopic[] Topics =
    [
        new("table-scaffolding", "Tables", null, "Create an AxTable and set TableGroup."),
        new("ssrs-report-authoring", "Reports", null, "Use `generate report` to emit an AxReport."),
    ];

    [Fact]
    public void Only_the_two_knowledge_classes_produce_proposals()
    {
        var records = new[]
        {
            Record("L1-table-basic", EvalTriage.KnowledgeGap),
            Record("L1-table-basic", EvalTriage.ToolDefect),
            Record("L1-table-basic", EvalTriage.EnvFlake),
            Record("L1-table-basic", null),
        };

        var proposals = EvalKnowledgeFeedback.Build(records, [Case("L1-table-basic", ["AxTable"])], Topics);

        var p = Assert.Single(proposals);
        Assert.Equal(EvalTriage.KnowledgeGap, p.Classification);
    }

    [Fact]
    public void Proposals_carry_run_ids_as_provenance_and_rank_by_frequency()
    {
        var records = new[]
        {
            Record("L1-report-basic", EvalTriage.ModelError, "picked the wrong dataset element", 3),
            Record("L1-report-basic", EvalTriage.ModelError, "picked the wrong dataset element", 2),
            Record("L1-table-basic", EvalTriage.KnowledgeGap, "no guidance on TableGroup", 1),
        };

        var proposals = EvalKnowledgeFeedback.Build(
            records,
            [Case("L1-report-basic", ["AxReport"], ["generate", "report", "R"]), Case("L1-table-basic", ["AxTable"])],
            Topics);

        Assert.Equal(2, proposals.Count);
        Assert.Equal("L1-report-basic", proposals[0].CaseId);
        Assert.Equal(2, proposals[0].Runs);
        Assert.Equal(2, proposals[0].RunIds.Count);
        Assert.All(proposals, p => Assert.NotEmpty(p.RunIds));
    }

    [Fact]
    public void A_candidate_topic_says_which_literal_linked_it_to_the_case()
    {
        var proposals = EvalKnowledgeFeedback.Build(
            [Record("L1-report-basic", EvalTriage.KnowledgeGap)],
            [Case("L1-report-basic", ["AxReport"], ["generate", "report", "R"])],
            Topics);

        var candidate = Assert.Single(Assert.Single(proposals).Candidates);
        Assert.Equal("ssrs-report-authoring", candidate.TopicId);
        Assert.Equal("skills/_source/ssrs-report-authoring.md", candidate.SourcePath);
        Assert.Contains("generate report", candidate.Signal);
    }

    /// <summary>
    /// No candidate is a finding, not an empty result: the corpus is saying the
    /// corpus has nothing to say about this family.
    /// </summary>
    [Fact]
    public void A_case_no_topic_names_yields_a_proposal_with_no_candidates()
    {
        var proposals = EvalKnowledgeFeedback.Build(
            [Record("L1-map-basic", EvalTriage.KnowledgeGap)],
            [Case("L1-map-basic", ["AxMap"], ["generate", "map", "M"])],
            Topics);

        var p = Assert.Single(proposals);
        Assert.Empty(p.Candidates);
        Assert.Contains("**No topic names this case", EvalKnowledgeFeedback.RenderMarkdown(proposals));
    }

    [Fact]
    public void An_empty_corpus_renders_a_brief_that_says_so()
    {
        var md = EvalKnowledgeFeedback.RenderMarkdown(EvalKnowledgeFeedback.Build([], [], Topics));

        Assert.Contains("nothing to propose", md);
    }
}
