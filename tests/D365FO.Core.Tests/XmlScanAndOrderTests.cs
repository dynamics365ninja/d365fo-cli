using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Reading a document's shape by tokens rather than by text, and the members the reader drops.
/// </summary>
/// <remarks>
/// The two remaining defect classes from the upstream 1.16.0 table: a rule that decided what a
/// document was by searching its TEXT, and an out-of-order member that the metadata
/// deserializer discards without a word.
/// </remarks>
public class XmlScanAndOrderTests
{
    // ── the root is the root, not a mention of one ─────────────────────────

    [Theory]
    [InlineData("<AxClass><Name>X</Name></AxClass>", "AxClass")]
    [InlineData("<?xml version=\"1.0\"?>\n<AxTable/>", "AxTable")]
    // The name in a comment above the root is not the root.
    [InlineData("<!-- was an <AxTable> once -->\n<AxClass/>", "AxClass")]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- <AxTable> -->\n<!DOCTYPE x>\n<AxForm/>", "AxForm")]
    [InlineData("   \n\n<AxEnum />", "AxEnum")]
    [InlineData("", null)]
    [InlineData("<!-- only a comment -->", null)]
    [InlineData("no markup at all", null)]
    public void The_root_element_is_read_past_the_prolog_and_the_comments(string xml, string? expected)
        => Assert.Equal(expected, XmlScan.RootElementName(xml));

    [Fact]
    public void A_name_inside_a_comment_is_not_the_root()
    {
        const string xml = "<!-- This was an <AxTable> before it was rewritten -->\n<AxClass><Name>C</Name></AxClass>";

        Assert.True(XmlScan.RootIs(xml, "AxClass"));
        Assert.False(XmlScan.RootIs(xml, "AxTable"));
    }

    [Fact]
    public void An_element_named_only_in_a_comment_or_cdata_is_not_present()
    {
        const string xml = """
            <AxClass>
              <!-- <AxTableIndex> would go here -->
              <SourceCode><Declaration><![CDATA[// see <AxTableIndex> on the base table
            class C {}]]></Declaration></SourceCode>
            </AxClass>
            """;

        Assert.False(XmlScan.ContainsElement(xml, "AxTableIndex"));
        Assert.True(XmlScan.ContainsElement(xml, "SourceCode"));
        Assert.True(XmlScan.ContainsElement(xml, "Declaration"));
    }

    // ── a member the reader would drop ─────────────────────────────────────
    //
    // There is no order rule here on purpose. One was written, and the census over the
    // installation (MetadataContractsAotTests) showed it flagging files Microsoft ships — an
    // AxEnum with ConfigurationKey after UseEnumValue, an AccessGrant with Create after Update.
    // The contract's member list is declaration order; the deserializer is more tolerant than
    // that. Ordering what we write stays; calling a document broken for its order does not.

    private static List<XppViolation> Check(string xml)
    {
        var v = new List<XppViolation>();
        ContractShapeRules.Check(xml, v);
        return v;
    }

    /// <summary>
    /// The unknown-member rule still owns what it always owned, and did not change when the
    /// order rule was withdrawn.
    /// </summary>
    [Fact]
    public void An_unknown_member_is_still_reported()
    {
        const string xml = """
            <AxTable>
              <Name>ConT</Name>
              <NoSuchMember>x</NoSuchMember>
            </AxTable>
            """;

        Assert.Contains(Check(xml), x => x.Rule == ContractShapeRules.RuleUnknownMember);
    }
}
