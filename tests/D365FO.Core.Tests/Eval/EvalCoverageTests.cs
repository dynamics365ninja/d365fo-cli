using D365FO.Core.Eval;
using D365FO.Core.Knowledge;
using D365FO.Core.ObjectTypes;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// The K ∧ E ∧ T taxonomy (plan item 4.5). The point of these tests is that the
/// report cannot be talked into claiming coverage: each leg has to be earned by
/// something that actually exists.
/// </summary>
public class EvalCoverageTests
{
    private static EvalCase Case(
        string id, string[] targets, string[]? args = null, bool pending = false, string[]? tags = null) => new(
        Id: id, Title: id, Tier: 1, Instruction: "…",
        CanonicalArgs: args, TargetArtifactTypes: targets, GoldenPath: id,
        Tags: tags ?? [], Ignore: [], RequiresFixtureIndex: false, GoldenPending: pending);

    private static KnowledgeTopic Topic(string id, string body) => new(id, "desc", null, body);

    [Fact]
    public void A_family_is_complete_only_when_all_three_legs_hold()
    {
        var report = EvalCoverage.Build(
            [Case("L1-table-basic", ["AxTable"], ["generate", "table", "FmThing"])],
            [Topic("table-scaffolding", "Create an AxTable with the right TableGroup.")]);

        var table = Assert.Single(report.Families, l => l.Id == "AxTable");
        Assert.Equal("KET", table.Status);
        Assert.True(table.Complete);
    }

    [Fact]
    public void A_pending_golden_does_not_count_as_proof()
    {
        var report = EvalCoverage.Build(
            [Case("L1-table-basic", ["AxTable"], ["generate", "table", "FmThing"], pending: true)],
            [Topic("table-scaffolding", "AxTable")]);

        var table = Assert.Single(report.Families, l => l.Id == "AxTable");
        Assert.False(table.Eval);
        Assert.Equal("K-T", table.Status);
    }

    /// <summary>
    /// Substring matching would let one table topic mark every extension family as
    /// taught, which turns the whole report into a rubber stamp.
    /// </summary>
    [Fact]
    public void A_topic_naming_AxTable_does_not_also_cover_AxTableExtension()
    {
        var report = EvalCoverage.Build([], [Topic("table-scaffolding", "All about AxTable.")]);

        Assert.True(Assert.Single(report.Families, l => l.Id == "AxTable").Knowledge);
        Assert.False(Assert.Single(report.Families, l => l.Id == "AxTableExtension").Knowledge);
    }

    [Fact]
    public void A_capability_is_proven_by_a_case_that_invokes_that_subcommand()
    {
        var report = EvalCoverage.Build(
            [Case("L1-view-basic", ["AxView"], ["generate", "view", "FmView"])],
            []);

        Assert.True(Assert.Single(report.Capabilities, l => l.Id == "view").Eval);
        Assert.False(Assert.Single(report.Capabilities, l => l.Id == "map").Eval);
    }

    [Fact]
    public void An_agent_only_case_proves_a_capability_through_its_tags()
    {
        var report = EvalCoverage.Build(
            [Case("L4-report-slice", ["AxReport"], args: null, tags: ["report"])],
            []);

        Assert.True(Assert.Single(report.Capabilities, l => l.Id == "report").Eval);
    }

    [Fact]
    public void Families_that_exist_on_no_AOS_are_not_leaves()
    {
        var report = EvalCoverage.Build([], []);

        Assert.DoesNotContain(report.Families, l => l.Id == "AxWorkspace");
        Assert.All(report.Families, l => Assert.True(ObjectTypeRegistry.Find(l.Id)!.ExistsInStandardAot));
    }

    [Fact]
    public void Every_generate_capability_names_at_least_one_root_the_registry_knows()
    {
        foreach (var cap in GenerateSurface.All)
        {
            Assert.NotEmpty(cap.Roots);
            foreach (var root in cap.Roots)
            {
                var type = ObjectTypeRegistry.Find(root);
                Assert.True(type is not null, $"`generate {cap.Name}` claims to emit <{root}>, which the registry does not know.");
                Assert.True(type!.ExistsInStandardAot, $"`generate {cap.Name}` claims to emit <{root}>, which exists on no AOS.");
            }
        }
    }

    /// <summary>
    /// The registry and the surface table describe the same thing from two sides;
    /// if a kind names <c>generate x</c> but capability <c>x</c> does not list that
    /// kind's root, one of the two is wrong and the report would understate T.
    /// </summary>
    [Fact]
    public void Every_registry_kind_with_a_generate_command_is_listed_under_that_capability()
    {
        foreach (var type in ObjectTypeRegistry.All.Where(t => t.GenerateCommand is not null))
        {
            var cap = GenerateSurface.Find(type.GenerateCommand);
            Assert.True(cap is not null, $"Registry kind '{type.Kind}' names `generate {type.GenerateCommand}`, which GenerateSurface does not list.");
            Assert.Contains(type.RootElement, cap!.Roots);
        }
    }

    [Fact]
    public void The_markdown_renders_the_gap_list_from_the_leaves()
    {
        var report = EvalCoverage.Build([], []);
        var md = EvalCoverage.RenderMarkdown(report);

        Assert.Contains("K ∧ E ∧ T", md);
        Assert.Contains("## Open gaps", md);
        // With no cases and no topics, every buildable leaf is a gap.
        Assert.Contains("**AxTable** (family)", md);
    }
}
