using System.Xml.Linq;
using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests.Eval;

public class XmlGoldenTests
{
    [Fact]
    public void Identical_documents_match()
    {
        var expected = XElement.Parse("<AxEdt><Name>Foo</Name><Label>Bar</Label></AxEdt>");
        var actual = XElement.Parse("<AxEdt><Name>Foo</Name><Label>Bar</Label></AxEdt>");

        var diff = XmlGolden.Diff(expected, actual);

        Assert.True(diff.IsMatch);
        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Extra);
        Assert.Empty(diff.Changed);
    }

    [Fact]
    public void Missing_element_in_actual_is_reported()
    {
        var expected = XElement.Parse("<AxEdt><Name>Foo</Name><Label>Bar</Label></AxEdt>");
        var actual = XElement.Parse("<AxEdt><Name>Foo</Name></AxEdt>");

        var diff = XmlGolden.Diff(expected, actual);

        Assert.False(diff.IsMatch);
        Assert.Contains("AxEdt/Label", diff.Missing);
        Assert.Empty(diff.Extra);
    }

    [Fact]
    public void Extra_element_in_actual_is_reported()
    {
        var expected = XElement.Parse("<AxEdt><Name>Foo</Name></AxEdt>");
        var actual = XElement.Parse("<AxEdt><Name>Foo</Name><Label>Bar</Label></AxEdt>");

        var diff = XmlGolden.Diff(expected, actual);

        Assert.False(diff.IsMatch);
        Assert.Contains("AxEdt/Label", diff.Extra);
        Assert.Empty(diff.Missing);
    }

    [Fact]
    public void Changed_leaf_value_is_reported_with_expected_and_actual()
    {
        var expected = XElement.Parse("<AxEdt><Name>Foo</Name></AxEdt>");
        var actual = XElement.Parse("<AxEdt><Name>Bar</Name></AxEdt>");

        var diff = XmlGolden.Diff(expected, actual);

        Assert.False(diff.IsMatch);
        var change = Assert.Single(diff.Changed);
        Assert.Equal("AxEdt/Name", change.Path);
        Assert.Equal("Foo", change.Expected);
        Assert.Equal("Bar", change.Actual);
    }

    [Fact]
    public void Changed_attribute_value_is_reported()
    {
        var expected = XElement.Parse("<AxTableField i:type=\"AxTableFieldString\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>F</Name></AxTableField>");
        var actual = XElement.Parse("<AxTableField i:type=\"AxTableFieldInt\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>F</Name></AxTableField>");

        var diff = XmlGolden.Diff(expected, actual);

        Assert.False(diff.IsMatch);
        var change = Assert.Single(diff.Changed);
        Assert.Equal("AxTableField/@type", change.Path);
        Assert.Equal("AxTableFieldString", change.Expected);
        Assert.Equal("AxTableFieldInt", change.Actual);
    }

    [Fact]
    public void Reordering_a_keyed_collection_does_not_register_as_a_diff()
    {
        var expected = XElement.Parse("""
            <AxTable>
              <Fields>
                <AxTableField><Name>A</Name><Mandatory>Yes</Mandatory></AxTableField>
                <AxTableField><Name>B</Name></AxTableField>
              </Fields>
            </AxTable>
            """);
        var actualReordered = XElement.Parse("""
            <AxTable>
              <Fields>
                <AxTableField><Name>B</Name></AxTableField>
                <AxTableField><Name>A</Name><Mandatory>Yes</Mandatory></AxTableField>
              </Fields>
            </AxTable>
            """);

        var diff = XmlGolden.Diff(expected, actualReordered);

        Assert.True(diff.IsMatch);
    }

    [Fact]
    public void Repeated_elements_key_by_DataField_child_when_no_Name_is_present()
    {
        var expected = XElement.Parse("""
            <AxTableIndex>
              <Fields>
                <AxTableIndexField><DataField>A</DataField></AxTableIndexField>
                <AxTableIndexField><DataField>B</DataField></AxTableIndexField>
              </Fields>
            </AxTableIndex>
            """);
        var actualDifferentValue = XElement.Parse("""
            <AxTableIndex>
              <Fields>
                <AxTableIndexField><DataField>A</DataField></AxTableIndexField>
                <AxTableIndexField><DataField>C</DataField></AxTableIndexField>
              </Fields>
            </AxTableIndex>
            """);

        var diff = XmlGolden.Diff(expected, actualDifferentValue);

        Assert.False(diff.IsMatch);
        // Keyed by the sibling's own DataField value, so this is a missing+extra pair, not a "changed".
        Assert.Contains(diff.Missing, p => p.Contains("[B]"));
        Assert.Contains(diff.Extra, p => p.Contains("[C]"));
    }

    [Fact]
    public void Ignore_globs_suppress_matching_paths_on_both_sides()
    {
        var expected = XElement.Parse("<AxTable><Name>Foo</Name><SomeGuid>111</SomeGuid></AxTable>");
        var actual = XElement.Parse("<AxTable><Name>Foo</Name><SomeGuid>222</SomeGuid></AxTable>");

        var withoutIgnore = XmlGolden.Diff(expected, actual);
        Assert.False(withoutIgnore.IsMatch);

        var withIgnore = XmlGolden.Diff(expected, actual, ignore: new[] { "AxTable/SomeGuid" });
        Assert.True(withIgnore.IsMatch);
    }

    [Fact]
    public void Empty_collections_on_both_sides_produce_no_diff()
    {
        var expected = XElement.Parse("<AxTable><Fields /></AxTable>");
        var actual = XElement.Parse("<AxTable><Fields></Fields></AxTable>");

        var diff = XmlGolden.Diff(expected, actual);

        Assert.True(diff.IsMatch);
    }
}
