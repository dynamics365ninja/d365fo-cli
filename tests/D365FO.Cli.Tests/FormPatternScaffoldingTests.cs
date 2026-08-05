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

    /// <summary>
    /// The generate-form command reports <c>fieldCount = --field count</c> for
    /// every pattern. TableOfContents and Workspace used to render their
    /// templates without ever consulting <c>GridFields</c>, so the caller got a
    /// success payload claiming two fields over XML that bound none — the
    /// "confident lie" failure mode eval/README.md ranks worst. No pattern may
    /// silently discard the fields it was handed.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllPatterns))]
    public void No_pattern_silently_discards_requested_fields(FormPattern pattern)
    {
        var xml = XppScaffolder.Form(
            formName:        "FmFieldSink",
            dataSourceTable: "FmVehicle",
            pattern:         pattern,
            gridFields:      new[] { "VIN", "Make" });

        var bound = XDocument.Parse(xml).Descendants("DataField").Select(e => e.Value).ToList();
        Assert.Contains("VIN", bound);
        Assert.Contains("Make", bound);
    }

    [Fact]
    public void TableOfContents_binds_requested_fields_on_the_first_tab_page()
    {
        var xml = XppScaffolder.Form("FmParameters", "FmVehicle", FormPattern.TableOfContents,
            gridFields: new[] { "VIN", "Make" });

        // Fields belong to the first page only — binding them on every page would
        // bind the same field twice.
        var pages = XDocument.Parse(xml).Descendants("AxFormControl")
            .Where(c => c.Element("Type")?.Value == "TabPage")
            .ToList();
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { "VIN", "Make" }, pages[0].Descendants("DataField").Select(e => e.Value));
        Assert.Empty(pages[1].Descendants("DataField"));
    }

    [Fact]
    public void Workspace_gives_fields_an_implicit_list_section_when_none_requested()
    {
        var xml = XppScaffolder.Form("FmWorkspace", "FmVehicle", FormPattern.Workspace,
            gridFields: new[] { "VIN", "Make" });

        // No --section, but --field was supplied: one implicit list section named
        // after the datasource carries them, rather than the fields vanishing.
        Assert.Contains("<Name>FmVehicleSection</Name>", xml);
        Assert.Contains("<Name>FmVehicleGrid_VIN</Name>", xml);
        Assert.Contains("<Name>FmVehicleGrid_Make</Name>", xml);
    }

    [Fact]
    public void Workspace_binds_fields_into_every_requested_section_grid()
    {
        var xml = XppScaffolder.Form("FmWorkspace", "FmVehicle", FormPattern.Workspace,
            gridFields: new[] { "VIN" },
            sections: new[]
            {
                new FormSectionSpec("OpenOrders", "Open orders"),
                new FormSectionSpec("BackOrders", "Back orders"),
            });
        Assert.Contains("<Name>OpenOrdersGrid_VIN</Name>", xml);
        Assert.Contains("<Name>BackOrdersGrid_VIN</Name>", xml);
    }

    [Fact]
    public void Workspace_without_fields_still_renders_only_the_summary_section()
    {
        var xml = XppScaffolder.Form("FmWorkspace", "FmVehicle", FormPattern.Workspace);
        Assert.Contains("<Name>SummarySection</Name>", xml);
        Assert.DoesNotContain("<Name>FmVehicleSection</Name>", xml);
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

    [Theory]
    [InlineData(FormPattern.SimpleList, new[] { "Overview" })]
    [InlineData(FormPattern.DetailsTransaction, new[] { "Overview" })]
    [InlineData(FormPattern.SimpleListDetails, new[] { "Overview", "General" })]
    [InlineData(FormPattern.DetailsMaster, new[] { "Overview", "General" })]
    [InlineData(FormPattern.Dialog, new string[0])]
    public void RequiredFieldGroups_lists_datagroups_referenced_by_templates(FormPattern pattern, string[] expected)
    {
        Assert.Equal(expected, D365FO.Cli.Commands.Generate.GenerateFormImpl.RequiredFieldGroups(pattern));
    }

    [Fact]
    public void TableDefinesFieldGroup_reads_table_xml()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".xml");
        File.WriteAllText(path, """
            <AxTable>
              <Name>FmVehicle</Name>
              <FieldGroups>
                <AxTableFieldGroup><Name>Overview</Name></AxTableFieldGroup>
              </FieldGroups>
            </AxTable>
            """);
        try
        {
            Assert.True(D365FO.Cli.Commands.Generate.GenerateFormImpl.TableDefinesFieldGroup(path, "Overview"));
            Assert.False(D365FO.Cli.Commands.Generate.GenerateFormImpl.TableDefinesFieldGroup(path, "General"));
        }
        finally { File.Delete(path); }
    }
}
