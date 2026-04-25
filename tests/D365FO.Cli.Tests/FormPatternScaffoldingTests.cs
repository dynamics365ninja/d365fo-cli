using D365FO.Core.Scaffolding;
using System.Xml.Linq;

namespace D365FO.Cli.Tests;

/// <summary>
/// Verifies that every <see cref="FormPattern"/> renders to a valid AOT
/// <c>AxForm</c> XML document with the right <c>Pattern</c> /
/// <c>PatternVersion</c> shape — mirrors upstream MCP's
/// <c>formPatternTemplates.test.ts</c>.
/// </summary>
public class FormPatternScaffoldingTests
{
    private static readonly XNamespace Ax = "Microsoft.Dynamics.AX.Metadata.V6";

    public static IEnumerable<object[]> AllPatterns()
    {
        foreach (var p in Enum.GetValues<FormPattern>())
            yield return new object[] { p };
    }

    [Theory]
    [MemberData(nameof(AllPatterns))]
    public void Build_emits_well_formed_axform_with_correct_pattern(FormPattern pattern)
    {
        var xml = XppScaffolder.Form(
            formName:        "FmTestForm",
            dataSourceTable: "FmVehicle",
            pattern:         pattern,
            caption:         "@Fleet:Vehicles",
            gridFields:      new[] { "VIN", "Make", "Model" },
            sections:        new[] { new FormSectionSpec("TabPageGeneral", "General") },
            linesTable:      "FmVehicleLine");

        var doc = XDocument.Parse(xml); // throws if malformed
        var root = doc.Root!;
        Assert.Equal(Ax + "AxForm", root.Name);
        Assert.Equal("FmTestForm", root.Element(Ax + "Name")?.Value);

        var design = root.Element(Ax + "Design");
        Assert.NotNull(design);

        // <Pattern> + <PatternVersion> are emitted in the empty default namespace
        // (xmlns="" attribute on each element).
        var patternEl = design!.Element("Pattern");
        Assert.NotNull(patternEl);
        Assert.Equal(pattern.ToString(), patternEl!.Value);

        var versionEl = design.Element("PatternVersion");
        Assert.NotNull(versionEl);
        Assert.False(string.IsNullOrWhiteSpace(versionEl!.Value));
    }

    [Fact]
    public void SimpleList_includes_action_pane_and_quick_filter()
    {
        var xml = XppScaffolder.Form("FmList", "FmVehicle", FormPattern.SimpleList,
            gridFields: new[] { "VIN" });
        Assert.Contains("<Name>ActionPane</Name>", xml);
        Assert.Contains("<Name>ButtonGroup</Name>", xml);
        Assert.Contains("<Name>QuickFilterControl</Name>", xml);
        Assert.Contains("<Pattern>CustomAndQuickFilters</Pattern>", xml);
        Assert.Contains("<Name>Grid_VIN</Name>", xml);
    }

    [Fact]
    public void DetailsTransaction_links_lines_datasource_to_header()
    {
        var xml = XppScaffolder.Form("FmOrder", "FmOrderHeader", FormPattern.DetailsTransaction,
            linesTable: "FmOrderLine",
            gridFields: new[] { "OrderNum" });
        // --lines-table sets both the lines datasource name and table.
        Assert.Contains("<Name>FmOrderLine</Name>", xml);
        Assert.Contains("<Table>FmOrderLine</Table>", xml);
        Assert.Contains("<LinkType>Active</LinkType>", xml);
        Assert.Contains("<Name>LinesGrid</Name>", xml);
    }

    [Fact]
    public void DetailsTransaction_defaults_lines_datasource_to_HeaderLines_when_not_specified()
    {
        var xml = XppScaffolder.Form("FmOrder", "FmOrderHeader", FormPattern.DetailsTransaction);
        // No --lines-table → default to <DsName>Lines (i.e. FmOrderHeaderLines).
        Assert.Contains("<Name>FmOrderHeaderLines</Name>", xml);
    }

    [Fact]
    public void Dialog_with_no_datasource_emits_empty_DataSources_element()
    {
        var xml = XppScaffolder.Form("FmAskUser", dataSourceTable: null, FormPattern.Dialog,
            gridFields: new[] { "MyParam" });
        Assert.Contains("<DataSources />", xml);
        Assert.Contains("<Pattern xmlns=\"\">Dialog</Pattern>", xml);
        Assert.Contains("<Command>OK</Command>", xml);
        Assert.Contains("<Command>Cancel</Command>", xml);
    }

    [Fact]
    public void TableOfContents_emits_default_sections_when_none_supplied()
    {
        var xml = XppScaffolder.Form("FmParameters", dataSourceTable: null, FormPattern.TableOfContents);
        Assert.Contains("<Name>TabPageGeneral</Name>", xml);
        Assert.Contains("<Name>TabPageSetup</Name>", xml);
        Assert.Contains("<Style>TOCList</Style>", xml);
    }

    [Fact]
    public void Workspace_renders_summary_section_and_extra_panorama_lists()
    {
        var xml = XppScaffolder.Form("FmWorkspace", "FmVehicle", FormPattern.Workspace,
            sections: new[]
            {
                new FormSectionSpec("OpenOrders", "Open orders"),
                new FormSectionSpec("BackOrders", "Back orders"),
            });
        Assert.Contains("<Name>SummarySection</Name>", xml);
        Assert.Contains("<Name>OpenOrdersSection</Name>", xml);
        Assert.Contains("<Name>BackOrdersGrid</Name>", xml);
        Assert.Contains("<Style>Panorama</Style>", xml);
    }

    [Fact]
    public void ListPage_locks_datasource_to_read_only()
    {
        var xml = XppScaffolder.Form("FmListPage", "FmVehicle", FormPattern.ListPage);
        Assert.Contains("<AllowCreate>No</AllowCreate>", xml);
        Assert.Contains("<AllowEdit>No</AllowEdit>", xml);
        Assert.Contains("<AllowDelete>No</AllowDelete>", xml);
        Assert.Contains("<MultiSelect>Yes</MultiSelect>", xml);
    }

    [Theory]
    [InlineData("master",        FormPattern.DetailsMaster)]
    [InlineData("transaction",   FormPattern.DetailsTransaction)]
    [InlineData("DropDialog",    FormPattern.Dialog)]
    [InlineData("toc",           FormPattern.TableOfContents)]
    [InlineData("panorama",      FormPattern.Workspace)]
    [InlineData("operational",   FormPattern.Workspace)]
    [InlineData("Simple-List",   FormPattern.SimpleList)]
    [InlineData("",              FormPattern.SimpleList)]
    [InlineData(null,            FormPattern.SimpleList)]
    public void Normalizer_maps_aliases(string? raw, FormPattern expected)
    {
        Assert.Equal(expected, FormPatternNormalizer.Normalize(raw));
    }
}
