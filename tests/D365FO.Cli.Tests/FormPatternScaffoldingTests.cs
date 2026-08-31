using D365FO.Core.FormPatterns;
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

        // The serialized pattern is the AOT registry's name, which is not always the
        // enum's: there is no pattern called "Lookup" (it is LookupGridOnly) and
        // "Workspace" exists only as an inactive 2.0 (the live one is
        // WorkspaceOperational). FormPatternCatalog.XmlName is the mapping.
        var expected = FormPatternCatalog.Patterns.Single(p => p.Id == pattern.ToString()).XmlName;
        Assert.Equal(expected, patternEl!.Value);

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
        // The lines grid is the registry's LineViewLinesGrid part, inside the Lines
        // panel's own FastTab — the flat "LinesGrid" on the tab page was the shape the
        // AOS rejected.
        Assert.Contains("<Name>LineViewLinesGrid</Name>", xml);
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
        // The Design carries the table-of-contents look; the Tab control must NOT, because a
        // Tab's Style is the TabStyle enum and "TOCList" is not one of its members — a form
        // carrying it fails to deserialize entirely.
        Assert.Contains("<Style xmlns=\"\">TableOfContents</Style>", xml);
        Assert.DoesNotContain("TOCList", xml);
    }

    /// <summary>
    /// The workspace is no longer a Panorama of inline list sections. The AOT registry
    /// has no active "Workspace" pattern — WorkspaceOperational 1.1 is the live one,
    /// and its sections are FastTab pages whose lists live in separate
    /// FormPartSectionList forms.
    /// </summary>
    [Fact]
    public void Workspace_is_an_operational_workspace_not_a_panorama()
    {
        var xml = XppScaffolder.Form("FmWorkspace", "FmVehicle", FormPattern.Workspace);

        Assert.Contains("<Pattern xmlns=\"\">WorkspaceOperational</Pattern>", xml);
        Assert.DoesNotContain("<Style>Panorama</Style>", xml);
        Assert.DoesNotContain("<Name>PanoramaBody</Name>", xml);
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
        // Workspace is the one pattern that cannot place fields at all: its lists live
        // in separate FormPartSectionList forms. It does not discard them silently
        // either — `generate form` refuses --field for it outright, which is the same
        // contract stated the other way round.
        if (pattern == FormPattern.Workspace) return;

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

    /// <summary>
    /// An operational workspace is a shell of sections. Its lists are not inline: a
    /// tabbed-list page must hold a FormPartControl pointing at a separate form whose
    /// own design pattern is FormPartSectionList, so the workspace scaffold emits the
    /// three required sections and nothing else. The fields the caller asked for are
    /// refused by `generate form` rather than dropped here — see
    /// GenerateCommands' Workspace guard.
    /// </summary>
    [Fact]
    public void Workspace_renders_the_three_sections_the_pattern_requires()
    {
        var xml = XppScaffolder.Form("FmWorkspace", "FmVehicle", FormPattern.Workspace);

        Assert.Contains("<Name>WorkspaceSections</Name>", xml);
        Assert.Contains("<Name>SectionSummaryTiles</Name>", xml);
        Assert.Contains("<Name>SectionTabbedList</Name>", xml);
        Assert.Contains("<Name>SectionRelatedLinks</Name>", xml);

        // The tabbed list's own tab is present and empty — its pages are Count="*",
        // and each would need a FormPart this command does not generate.
        Assert.Contains("<Name>TabbedList</Name>", xml);
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
    [InlineData("toc",           FormPattern.TableOfContents)]
    [InlineData("panorama",      FormPattern.Workspace)]
    [InlineData("Simple-List",   FormPattern.SimpleList)]
    [InlineData("",              FormPattern.SimpleList)]
    [InlineData(null,            FormPattern.SimpleList)]
    public void Normalizer_maps_aliases(string? raw, FormPattern expected)
    {
        Assert.True(FormPatternNormalizer.TryNormalize(raw, out var actual, out var error));
        Assert.Null(error);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Audit finding G5: catalog-known-but-not-generatable patterns used to fall through
    /// to SimpleList, so `--pattern Wizard` produced a plain list form and reported success.
    /// </summary>
    [Theory]
    [InlineData("Wizard")]
    [InlineData("DropDialog")]
    [InlineData("FormPartFactboxGrid")]
    [InlineData("TaskSingle")]
    public void Normalizer_rejects_catalog_only_patterns(string raw)
    {
        Assert.False(FormPatternNormalizer.TryNormalize(raw, out _, out var error));
        Assert.Contains("cannot scaffold it", error);
        Assert.Contains("get form-pattern", error);
    }

    [Fact]
    public void Normalizer_routes_a_second_spelling_of_the_same_AOS_pattern_to_its_template()
    {
        // "operational" resolves to the catalog's WorkspaceOperational entry, which writes
        // the same <Pattern>WorkspaceOperational</Pattern> as the templated Workspace entry.
        // Sending it to the generic expander instead produced a form the metadata reader
        // rejected, so a name that names an AOS pattern a template covers gets the template.
        Assert.True(FormPatternNormalizer.TryNormalize("operational", out var pattern, out var error));
        Assert.Null(error);
        Assert.Equal(D365FO.Core.Scaffolding.FormPattern.Workspace, pattern);
    }

    [Fact]
    public void Normalizer_points_a_variant_at_its_generatable_parent()
    {
        Assert.False(FormPatternNormalizer.TryNormalize("DropDialog", out _, out var error));
        Assert.Contains("--pattern Dialog", error);
    }

    [Fact]
    public void Normalizer_rejects_unknown_patterns_and_lists_what_is_generatable()
    {
        Assert.False(FormPatternNormalizer.TryNormalize("nonsense", out _, out var error));
        Assert.Contains("Unknown form pattern 'nonsense'", error);
        foreach (var p in Enum.GetNames<FormPattern>())
            Assert.Contains(p, error);
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
