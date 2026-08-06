using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// The replay runner now records a classification hypothesis instead of leaving
/// every corpus record untriaged (plan item 4.4). These tests pin the part that
/// matters: what it refuses to claim.
/// </summary>
public class EvalTriageTests
{
    private static EvalCase Case(string id, params string[] tags) => new(
        Id: id, Title: id, Tier: 1, Instruction: "…",
        CanonicalArgs: ["generate", "table", "FmThing"],
        TargetArtifactTypes: ["AxTable"], GoldenPath: id, Tags: tags,
        Ignore: [], RequiresFixtureIndex: false, GoldenPending: false);

    private static EvalScoreCard Score(bool golden, bool? xpp = true, bool? refs = true) => new(
        XppClean: xpp, XppErrors: xpp == false ? 2 : 0,
        ReferencesClean: refs, ReferenceErrors: refs == false ? 3 : 0,
        GoldenMatch: golden,
        GoldenDiff: golden ? XmlGoldenDiff.Empty : new XmlGoldenDiff(["a"], [], []));

    [Fact]
    public void A_clean_replay_is_not_classified_at_all()
    {
        var (classification, note) = EvalTriage.Hypothesize(Case("L1-t"), Score(golden: true), "replay");

        Assert.Null(classification);
        Assert.Null(note);
    }

    [Fact]
    public void A_replay_that_missed_the_golden_is_a_tool_defect()
    {
        var (classification, note) = EvalTriage.Hypothesize(Case("L1-t"), Score(golden: false), "replay");

        Assert.Equal(EvalTriage.ToolDefect, classification);
        Assert.Contains("golden", note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_validator_complaining_about_golden_matching_output_is_a_validator_gap()
    {
        var (classification, _) = EvalTriage.Hypothesize(Case("L1-t"), Score(golden: true, xpp: false), "replay");

        Assert.Equal(EvalTriage.ValidatorGap, classification);
    }

    [Fact]
    public void A_reference_gap_the_case_documents_is_not_a_defect()
    {
        var (classification, note) = EvalTriage.Hypothesize(
            Case("L1-t", EvalTriage.KnownReferenceGapTag), Score(golden: true, refs: false), "replay");

        Assert.Null(classification);
        Assert.Contains("expected", note!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_undocumented_reference_gap_is_still_reported()
    {
        var (classification, _) = EvalTriage.Hypothesize(Case("L1-t"), Score(golden: true, refs: false), "replay");

        Assert.Equal(EvalTriage.ValidatorGap, classification);
    }

    /// <summary>
    /// The load-bearing refusal: with an agent in the loop the same scorecard is
    /// consistent with both a tool defect and the agent's own mistake, and guessing
    /// would point the improver at innocent code.
    /// </summary>
    [Fact]
    public void An_agent_run_is_left_untriaged_rather_than_guessed()
    {
        var (classification, note) = EvalTriage.Hypothesize(Case("L1-t"), Score(golden: false), "agent");

        Assert.Null(classification);
        Assert.Null(note);
    }

    [Theory]
    [InlineData("tool_defect", "TOOL_DEFECT")]
    [InlineData("model-error", "MODEL_ERROR")]
    [InlineData(" knowledge_gap ", "KNOWLEDGE_GAP")]
    public void Normalize_accepts_the_five_classes_in_any_casing(string input, string expected)
        => Assert.Equal(expected, EvalTriage.Normalize(input));

    [Theory]
    [InlineData("PROBABLY_FINE")]
    [InlineData("")]
    [InlineData(null)]
    public void Normalize_rejects_anything_else(string? input)
        => Assert.Null(EvalTriage.Normalize(input));
}
