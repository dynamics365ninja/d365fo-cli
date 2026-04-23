using D365FO.Core;
using Xunit;

namespace D365FO.Core.Tests;

public class ModelMatcherTests
{
    [Fact]
    public void Empty_list_matches_nothing()
    {
        var m = new ModelMatcher(Array.Empty<string>());
        Assert.True(m.IsEmpty);
        Assert.False(m.IsMatch("AnyModel"));
    }

    [Fact]
    public void Exact_name_is_case_insensitive()
    {
        var m = new ModelMatcher(new[] { "AslCore" });
        Assert.True(m.IsMatch("AslCore"));
        Assert.True(m.IsMatch("aslcore"));
        Assert.False(m.IsMatch("AslCoreExt"));
    }

    [Fact]
    public void Star_wildcard_matches_prefix()
    {
        var m = new ModelMatcher(new[] { "Asl*" });
        Assert.True(m.IsMatch("AslCore"));
        Assert.True(m.IsMatch("AslFinance"));
        Assert.True(m.IsMatch("Asl"));
        Assert.False(m.IsMatch("ISVFoo"));
    }

    [Fact]
    public void Question_mark_matches_single_char()
    {
        var m = new ModelMatcher(new[] { "Mod?" });
        Assert.True(m.IsMatch("ModA"));
        Assert.True(m.IsMatch("Mod1"));
        Assert.False(m.IsMatch("ModAB"));
        Assert.False(m.IsMatch("Mod"));
    }

    [Fact]
    public void Negation_excludes_prior_match()
    {
        var m = new ModelMatcher(new[] { "Asl*", "!AslTest" });
        Assert.True(m.IsMatch("AslCore"));
        Assert.False(m.IsMatch("AslTest"));
    }

    [Fact]
    public void Blank_and_whitespace_patterns_are_ignored()
    {
        var m = new ModelMatcher(new[] { "", "  ", "ISV*" });
        Assert.True(m.IsMatch("ISV_Foo"));
        Assert.False(m.IsMatch("Other"));
    }
}
