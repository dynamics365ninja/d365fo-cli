using D365FO.Core.Knowledge;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class TableDataMethodsTests
{
    [Theory]
    [InlineData("modifiedFieldValue", "public void modifiedFieldValue(FieldName _fieldName, int _value)")]
    [InlineData("postLoad", "public void postLoad()")]
    [InlineData("defaultRow", "public void defaultRow()")]
    [InlineData("defaultField", "public void defaultField(FieldId _fieldId)")]
    [InlineData("toolTipField", "public str toolTipField(FieldId _fieldId)")]
    [InlineData("helpField", "public str helpField(FieldId _fieldId)")]
    [InlineData("caption", "public str caption()")]
    [InlineData("aosValidateInsert", "public boolean aosValidateInsert()")]
    [InlineData("aosValidateUpdate", "public boolean aosValidateUpdate()")]
    [InlineData("aosValidateDelete", "public boolean aosValidateDelete()")]
    [InlineData("aosValidateRead", "public boolean aosValidateRead()")]
    public void An_inherited_data_method_has_its_signature(string name, string signature)
        => Assert.Equal(signature, TableDataMethods.Lookup(name)?.Signature);

    /// <summary>A wrapper on these compiles with only a warning, and never runs.</summary>
    [Fact]
    public void The_aos_validation_methods_are_not_offered_for_chain_of_command()
    {
        foreach (var name in new[] { "aosValidateInsert", "aosValidateUpdate", "aosValidateDelete", "aosValidateRead" })
        {
            Assert.False(TableDataMethods.Lookup(name)!.CocEligible);
            Assert.DoesNotContain(TableDataMethods.All, m => m.Name == name);
        }
        Assert.True(TableDataMethods.Lookup("validateWrite")!.CocEligible);
        Assert.Contains(TableDataMethods.All, m => m.Name == "postLoad");
    }
}
