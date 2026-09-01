using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// A recipe that names an object or a base class the platform does not have is worse than no
/// recipe: it reads as authoritative and costs a build cycle to disprove.
/// </summary>
/// <remarks>
/// The base-class names here were counted in a real ApplicationSuite/Foundation rather than
/// recalled. These tests pin the shape of the catalog; <c>ReportRecipesAotTests</c> checks the
/// names against an actual installation when one is present.
/// </remarks>
public class ReportRecipesTests
{
    [Fact]
    public void There_are_seven_recipes_with_unique_ids()
    {
        var recipes = ReportRecipes.List();
        Assert.Equal(7, recipes.Count);
        Assert.Equal(recipes.Count, recipes.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("simple-list")]
    [InlineData("grouped-with-totals")]
    [InlineData("header-detail")]
    [InlineData("pre-process")]
    [InlineData("query-based")]
    [InlineData("print-mgmt-form-letter")]
    [InlineData("ui-builder-dialog")]
    public void Every_recipe_is_findable_and_complete(string id)
    {
        var recipe = ReportRecipes.Find(id);

        Assert.NotNull(recipe);
        Assert.NotEmpty(recipe!.WhenToUse);
        Assert.NotEmpty(recipe.Roster);
        Assert.NotEmpty(recipe.ScaffoldCall);
        Assert.NotEmpty(recipe.Checks);
    }

    [Fact]
    public void Find_is_case_insensitive_and_tolerates_spacing()
    {
        Assert.NotNull(ReportRecipes.Find("PRE-PROCESS"));
        Assert.NotNull(ReportRecipes.Find("  pre-process  "));
        Assert.Null(ReportRecipes.Find("preprocess"));
    }

    [Fact]
    public void The_pre_process_recipe_names_the_pre_process_base_and_the_others_do_not()
    {
        // Building a pre-processed requirement on SRSReportDataProviderBase behaves wrong in
        // batch with nothing failing anywhere, so this is the distinction that matters most.
        var pre = ReportRecipes.Find("pre-process")!;
        Assert.Contains(pre.Roster, o => o.Extends == "SrsReportDataProviderPreProcessTempDB");

        var simple = ReportRecipes.Find("simple-list")!;
        Assert.Contains(simple.Roster, o => o.Extends == "SRSReportDataProviderBase");
        Assert.DoesNotContain(simple.Roster, o => o.Extends == "SrsReportDataProviderPreProcessTempDB");
    }

    [Fact]
    public void Only_the_print_management_recipe_uses_the_print_management_controller()
    {
        foreach (var recipe in ReportRecipes.List())
        {
            var usesPrintMgmt = recipe.Roster.Any(o => o.Extends == "SrsPrintMgmtFormLetterController");
            Assert.Equal(recipe.Id == "print-mgmt-form-letter", usesPrintMgmt);
        }
    }

    [Fact]
    public void The_query_based_recipe_has_no_temp_table_contract_or_dp()
    {
        // Adding them because the other six have them is the common way a query-based report
        // turns into three objects that do nothing.
        var recipe = ReportRecipes.Find("query-based")!;

        Assert.DoesNotContain(recipe.Roster, o => o.Role == "TmpTable");
        Assert.DoesNotContain(recipe.Roster, o => o.Role == "Contract");
        Assert.DoesNotContain(recipe.Roster, o => o.Role == "DP");
        Assert.Contains(recipe.Roster, o => o.Kind == "AxQuery");
    }

    [Fact]
    public void Every_scaffold_call_is_a_real_generate_invocation()
    {
        foreach (var recipe in ReportRecipes.List())
        {
            Assert.StartsWith("d365fo generate ", recipe.ScaffoldCall);
            Assert.Contains("--install-to", recipe.ScaffoldCall);
        }
    }

    [Fact]
    public void Base_classes_are_spelled_the_way_the_AOT_spells_them()
    {
        // The DP base is SRS in capitals while every other class in the stack is Srs. X++
        // resolves either, so this is consistency rather than compilation — but a scaffold that
        // emits the shipped spelling produces files that diff cleanly against their neighbours.
        var known = new[]
        {
            "SRSReportDataProviderBase",
            "SrsReportDataProviderPreProcessTempDB",
            "SrsReportRunController",
            "SrsPrintMgmtFormLetterController",
            "SrsReportDataContractUIBuilder",
        };

        var used = ReportRecipes.List()
            .SelectMany(r => r.Roster)
            .Select(o => o.Extends)
            .Where(e => e is not null)
            .Distinct()
            .ToList();

        Assert.All(used, e => Assert.Contains(e, known));
    }
}
