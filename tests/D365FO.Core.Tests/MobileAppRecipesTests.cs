using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>Shape of the warehouse-app recipe catalog, independent of any installation.</summary>
public class MobileAppRecipesTests
{
    [Fact]
    public void There_are_seven_recipes_with_unique_ids()
    {
        var recipes = MobileAppRecipes.List();
        Assert.Equal(7, recipes.Count);
        Assert.Equal(recipes.Count, recipes.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("processguide-flow")]
    [InlineData("processguide-page-control")]
    [InlineData("processguide-page-replace")]
    [InlineData("processguide-step-insert")]
    [InlineData("app-step-identity")]
    [InlineData("legacy-workexecutedisplay")]
    [InlineData("gs1-scan-input")]
    public void Every_recipe_is_findable_and_says_when_it_applies(string id)
    {
        var recipe = MobileAppRecipes.Find(id);

        Assert.NotNull(recipe);
        Assert.NotEmpty(recipe!.WhenToUse);
        Assert.NotEmpty(recipe.Guidance);
        Assert.NotEmpty(recipe.Checks);
    }

    [Fact]
    public void Both_frameworks_are_represented_and_the_decision_is_stated()
    {
        // The list exists to make the framework choice first, so a catalog covering only one of
        // them would quietly answer "use ProcessGuide" to a question about a legacy flow.
        var frameworks = MobileAppRecipes.List().Select(r => r.Framework).Distinct().ToList();

        Assert.Contains(MobileFramework.ProcessGuide, frameworks);
        Assert.Contains(MobileFramework.WorkExecuteDisplay, frameworks);
        Assert.Contains(MobileFramework.Configuration, frameworks);

        Assert.Contains("rewrite", MobileAppRecipes.FrameworkDecision, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_configuration_recipes_ask_for_no_classes_at_all()
    {
        // Writing a class to change a caption, or a parser to split a barcode, is the mistake
        // these two exist to prevent — so neither may carry a required class in its roster.
        foreach (var recipe in MobileAppRecipes.List().Where(r => r.Framework == MobileFramework.Configuration))
        {
            Assert.DoesNotContain(recipe.Roster, o => o.Required && o.Extends is not null);
        }
    }

    [Fact]
    public void Only_the_legacy_recipe_belongs_to_the_legacy_framework()
    {
        foreach (var recipe in MobileAppRecipes.List())
        {
            var legacy = recipe.Framework == MobileFramework.WorkExecuteDisplay;
            Assert.Equal(recipe.Id == "legacy-workexecutedisplay", legacy);
        }
    }

    [Fact]
    public void Find_is_case_insensitive_and_trims()
    {
        Assert.NotNull(MobileAppRecipes.Find("PROCESSGUIDE-FLOW"));
        Assert.NotNull(MobileAppRecipes.Find("  gs1-scan-input "));
        Assert.Null(MobileAppRecipes.Find("processguide flow"));
    }
}
