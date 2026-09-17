using D365FO.Core.Metadata;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// XML006/XML007 — the offline half of "would the AOT actually keep this?". Both rules exist
/// because <c>DataContractSerializer</c> does not reject bad members, it ignores them: the file
/// parses, the other validators pass, and the property is gone.
/// </summary>
public class ContractShapeRulesTests
{
    private static IReadOnlyList<XppViolation> Check(string xml)
        => XppValidator.Validate(xml, XppValidator.CodeTypeXmlAny);

    [Fact]
    public void Catalog_is_loaded_and_covers_the_families_we_generate()
    {
        Assert.NotEmpty(MetadataContracts.All);
        foreach (var type in new[]
                 {
                     "AxTable", "AxClass", "AxForm", "AxQuery", "AxView", "AxMap",
                     "AxDataEntityView", "AxMenuItemDisplay", "AxReport", "AxSecurityPolicy",
                     "AxSecurityPrivilege", "AxSecurityRole", "AxSecurityDuty",
                     "AxWorkflowTemplate", "AxWorkflowApproval", "AxWorkflowTask",
                 })
            Assert.True(MetadataContracts.Find(type) is not null, $"catalog is missing {type}");
    }

    [Fact]
    public void Catalog_records_the_namespace_and_abstractness_the_assembly_declares()
    {
        Assert.Equal(string.Empty, MetadataContracts.Find("AxTable")!.Namespace);
        Assert.Equal("Microsoft.Dynamics.AX.Metadata.V6", MetadataContracts.Find("AxForm")!.Namespace);
        Assert.Equal("Microsoft.Dynamics.AX.Metadata.V2", MetadataContracts.Find("AxWorkflowTemplate")!.Namespace);
        Assert.Equal("Microsoft.Dynamics.AX.Metadata.V1", MetadataContracts.Find("AxMenuItemDisplay")!.Namespace);

        Assert.True(MetadataContracts.Find("AxQuery")!.IsAbstract);
        Assert.False(MetadataContracts.Find("AxQuerySimple")!.IsAbstract);
        // The type the write guard used to demand does not exist at all.
        Assert.Null(MetadataContracts.Find("AxEdtStringExtension"));
    }

    [Fact]
    public void Element_contract_follows_the_xsi_type_when_one_is_pinned()
    {
        Assert.Equal("AxQuerySimple", MetadataContracts.ForElement("AxQuery", "AxQuerySimple")!.Name);
        Assert.Equal("AxQuery", MetadataContracts.ForElement("AxQuery", null)!.Name);
    }

    [Fact]
    public void Member_the_type_does_not_declare_is_reported()
    {
        var xml = """
            <AxTable>
              <Name>ConTest</Name>
              <Kolekce>oops</Kolekce>
            </AxTable>
            """;

        var v = Assert.Single(Check(xml), x => x.Rule == ContractShapeRules.RuleUnknownMember);
        Assert.Equal("error", v.Severity);
        Assert.Contains("Kolekce", v.Excerpt);
        Assert.Contains("dropped on read", v.Fix);
    }

    /// <summary>
    /// Member order is not linted, deliberately: shipped Microsoft files deviate from contract
    /// order in places and the provider still reads them with no loss, so flagging deviation
    /// would cry wolf. Order is enforced where it is provably useful — on output.
    /// </summary>
    [Fact]
    public void Order_deviation_alone_is_not_reported()
    {
        var xml = """
            <AxTable>
              <Name>ConTest</Name>
              <Fields />
              <FieldGroups />
            </AxTable>
            """;

        Assert.DoesNotContain(Check(xml), v => v.Rule == ContractShapeRules.RuleUnknownMember);
    }

    [Fact]
    public void Rules_apply_to_every_family_not_just_tables()
    {
        var xml = """
            <AxWorkflowTemplate>
              <Name>ConWf</Name>
              <DocumentTableName>FmVehicle</DocumentTableName>
            </AxWorkflowTemplate>
            """;

        // DocumentTableName is exactly the kind of plausible invention this catches — the
        // workflow scaffolder emitted it for years and the AOT never had such a property.
        var v = Assert.Single(Check(xml), x => x.Rule == ContractShapeRules.RuleUnknownMember);
        Assert.Contains("DocumentTableName", v.Excerpt);
        Assert.Contains("Document", v.Fix);
    }

    [Fact]
    public void Unknown_root_types_are_left_alone()
    {
        // Hand-rolled fragments and non-AOT XML must not be second-guessed.
        var xml = "<SomethingElse><Whatever>1</Whatever></SomethingElse>";

        Assert.DoesNotContain(Check(xml), v => v.Rule == ContractShapeRules.RuleUnknownMember);
    }

    [Fact]
    public void Nested_contract_objects_are_checked_too()
    {
        var xml = """
            <AxTable>
              <Name>ConTest</Name>
              <Fields>
                <AxTableField i:type="AxTableFieldString" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                  <Name>VIN</Name>
                  <NotAProperty>x</NotAProperty>
                </AxTableField>
              </Fields>
            </AxTable>
            """;

        var v = Assert.Single(Check(xml), x => x.Rule == ContractShapeRules.RuleUnknownMember);
        Assert.Contains("NotAProperty", v.Excerpt);
        Assert.Contains("AxTableFieldString", v.Fix);
    }
}
