using D365FO.Core;
using Xunit;

namespace D365FO.Cli.Tests;

public class ObjectNamingRulesTests
{
    [Fact]
    public void Valid_pascal_case_name_passes_with_no_errors()
    {
        var v = ObjectNamingRules.Validate("Table", "CustTable");
        Assert.DoesNotContain(v, x => x.Severity == "error");
    }

    [Fact]
    public void Empty_name_produces_error()
    {
        var v = ObjectNamingRules.Validate("Table", "");
        Assert.Contains(v, x => x.Code == "EMPTY_NAME" && x.Severity == "error");
    }

    [Fact]
    public void Name_with_space_produces_invalid_chars()
    {
        var v = ObjectNamingRules.Validate("Class", "Bad Name");
        Assert.Contains(v, x => x.Code == "INVALID_CHARS");
    }

    [Fact]
    public void Name_starting_with_digit_is_rejected()
    {
        var v = ObjectNamingRules.Validate("Class", "1Foo");
        Assert.Contains(v, x => x.Code == "LEADS_WITH_DIGIT" && x.Severity == "error");
    }

    [Fact]
    public void Missing_publisher_prefix_emits_warning()
    {
        var v = ObjectNamingRules.Validate("Table", "CustTable", prefix: "Contoso_");
        Assert.Contains(v, x => x.Code == "MISSING_PUBLISHER_PREFIX" && x.Severity == "warn");
    }

    [Fact]
    public void Coc_class_without_extension_suffix_is_flagged()
    {
        var v = ObjectNamingRules.Validate("Coc", "CustTableHelper");
        Assert.Contains(v, x => x.Code == "COC_SUFFIX");
    }

    [Fact]
    public void Coc_class_with_proper_extension_suffix_passes()
    {
        var v = ObjectNamingRules.Validate("Coc", "CustTable_Extension");
        Assert.DoesNotContain(v, x => x.Code == "COC_SUFFIX");
    }
}
