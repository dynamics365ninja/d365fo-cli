using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Cover for the deterministic form auto-repair pipeline — the piece the upstream
/// port docs listed as missing next to <c>FormPatternValidator</c>. The contract
/// under test is: repair every violation with exactly one correct outcome, refuse
/// (visibly) the ones that need a human decision, and never delete a control.
/// </summary>
public class FormPatternRepairerTests
{
    private static readonly XNamespace Ax = "Microsoft.Dynamics.AX.Metadata.V6";

    private static string Scaffold(FormPattern pattern = FormPattern.SimpleList) =>
        XppScaffolder.Form("FmTestForm", "FmVehicle", pattern, "@Fleet:Vehicles",
            gridFields: new[] { "VIN", "Make" },
            sections: new[] { new FormSectionSpec("TabPageGeneral", "General") },
            linesTable: "FmVehicleLine");

    /// <summary>Drop the first control of a given normalized type from the Design root.</summary>
    private static string RemoveRootControl(string xml, string typeSuffix)
    {
        var doc = XDocument.Parse(xml);
        var controls = doc.Root!.Element(Ax + "Design")!.Element("Controls")!;
        var victim = controls.Elements()
            .First(e => ((string?)e.Attribute(XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type"))
                ?.EndsWith(typeSuffix, StringComparison.Ordinal) == true);
        victim.Remove();
        return doc.ToString();
    }

    [Fact]
    public void A_conforming_form_is_left_untouched()
    {
        var result = FormPatternRepairer.Repair(Scaffold());
        Assert.False(result.Changed);
        Assert.Empty(result.Changes);
        Assert.True(result.FullyRepaired);
    }

    [Fact]
    public void Missing_required_control_is_added_and_the_form_validates()
    {
        var broken = RemoveRootControl(Scaffold(), "AxFormGridControl");
        Assert.True(FormPatternValidator.ValidateXml(broken).HasErrors);

        var result = FormPatternRepairer.Repair(broken);

        Assert.Contains(result.Changes, c => c.Rule == "FP003" && c.Action == "added");
        Assert.True(result.FullyRepaired, DescribeFailure(result));
        Assert.Contains("AxFormGridControl", result.Xml);
    }

    [Fact]
    public void Missing_action_pane_is_added_and_the_form_validates()
    {
        var broken = RemoveRootControl(Scaffold(), "AxFormActionPaneControl");
        var result = FormPatternRepairer.Repair(broken);

        Assert.Contains(result.Changes, c => c.Rule == "FP003");
        Assert.True(result.FullyRepaired, DescribeFailure(result));
    }

    [Fact]
    public void Controls_out_of_order_are_reordered_not_recreated()
    {
        var doc = XDocument.Parse(Scaffold());
        var controls = doc.Root!.Element(Ax + "Design")!.Element("Controls")!;
        var children = controls.Elements().ToList();
        foreach (var c in children) c.Remove();
        foreach (var c in Enumerable.Reverse(children)) controls.Add(c); // Grid → filter → ActionPane
        var scrambled = doc.ToString();

        Assert.True(FormPatternValidator.ValidateXml(scrambled).HasErrors);

        var result = FormPatternRepairer.Repair(scrambled);

        Assert.Contains(result.Changes, c => c.Rule == "FP005" && c.Action == "reordered");
        Assert.DoesNotContain(result.Changes, c => c.Action == "added");
        Assert.True(result.FullyRepaired, DescribeFailure(result));
    }

    [Fact]
    public void An_unpatterned_form_can_be_adopted_into_a_pattern()
    {
        var xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="Microsoft.Dynamics.AX.Metadata.V6">
              <Name>FmBare</Name>
              <DataSources>
                <AxFormDataSource xmlns="">
                  <Name>FmVehicle</Name>
                  <Table>FmVehicle</Table>
                </AxFormDataSource>
              </DataSources>
              <Design>
                <Controls xmlns="" />
              </Design>
            </AxForm>
            """;

        Assert.Contains(FormPatternValidator.ValidateXml(xml).Violations, v => v.Rule == "FP010");

        var result = FormPatternRepairer.Repair(xml, "SimpleList");

        Assert.Contains(result.Changes, c => c.Action == "set-pattern");
        Assert.Contains(result.Changes, c => c.Action == "set-version");
        Assert.Contains(result.Changes, c => c.Rule == "FP003" && c.Action == "added");
        Assert.True(result.FullyRepaired, DescribeFailure(result));
    }

    [Fact]
    public void Without_an_explicit_pattern_an_unpatterned_form_is_skipped_not_guessed()
    {
        var xml = """
            <AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="Microsoft.Dynamics.AX.Metadata.V6">
              <Name>FmBare</Name>
              <Design><Controls xmlns="" /></Design>
            </AxForm>
            """;

        var result = FormPatternRepairer.Repair(xml);

        Assert.False(result.Changed);
        Assert.Contains(result.Skipped, s => s.Rule == "FP010");
    }

    [Fact]
    public void An_unknown_pattern_argument_is_reported_not_applied()
    {
        var result = FormPatternRepairer.Repair(Scaffold(), "NotARealPattern");
        Assert.Contains(result.Skipped, s => s.Rule == "FP001");
    }

    [Fact]
    public void A_stale_pattern_version_is_pinned_to_the_newest_catalog_version()
    {
        var doc = XDocument.Parse(Scaffold());
        doc.Root!.Element(Ax + "Design")!.Element("PatternVersion")!.SetValue("0.9");

        var result = FormPatternRepairer.Repair(doc.ToString());

        Assert.Contains(result.Changes, c => c.Rule == "FP002" && c.Action == "set-version");
        Assert.Equal("1.1", XDocument.Parse(result.Xml).Root!.Element(Ax + "Design")!.Element("PatternVersion")!.Value);
    }

    [Fact]
    public void A_design_property_that_drifted_is_reset_to_the_pattern_default()
    {
        var doc = XDocument.Parse(Scaffold());
        doc.Root!.Element(Ax + "Design")!.Element("Style")!.SetValue("Wrong");

        var result = FormPatternRepairer.Repair(doc.ToString());

        Assert.Contains(result.Changes, c => c.Rule == "FP009" && c.Detail.Contains("Style"));
        Assert.Equal("SimpleList", XDocument.Parse(result.Xml).Root!.Element(Ax + "Design")!.Element("Style")!.Value);
    }

    [Fact]
    public void A_disallowed_extra_control_is_reported_but_never_deleted()
    {
        var doc = XDocument.Parse(Scaffold());
        var controls = doc.Root!.Element(Ax + "Design")!.Element("Controls")!;
        controls.Add(FormControlFactory.Create("Group", "HandWrittenGroup"));

        var result = FormPatternRepairer.Repair(doc.ToString());

        Assert.Contains(result.Skipped, s => s.Rule == "FP004");
        Assert.Contains("HandWrittenGroup", result.Xml);
    }

    [Theory]
    [InlineData(FormPattern.SimpleList)]
    [InlineData(FormPattern.SimpleListDetails)]
    [InlineData(FormPattern.DetailsMaster)]
    [InlineData(FormPattern.DetailsTransaction)]
    [InlineData(FormPattern.Dialog)]
    [InlineData(FormPattern.TableOfContents)]
    [InlineData(FormPattern.Lookup)]
    [InlineData(FormPattern.ListPage)]
    [InlineData(FormPattern.Workspace)]
    public void Repair_is_idempotent_and_never_breaks_a_valid_form(FormPattern pattern)
    {
        var xml = Scaffold(pattern);
        var first = FormPatternRepairer.Repair(xml);
        Assert.False(first.Before.HasErrors, DescribeFailure(first));
        Assert.False(first.After.HasErrors, DescribeFailure(first));

        var second = FormPatternRepairer.Repair(first.Xml);
        Assert.False(second.Changed);
    }

    [Fact]
    public void Malformed_xml_is_reported_rather_than_thrown()
    {
        var result = FormPatternRepairer.Repair("<AxForm><unclosed>");
        Assert.False(result.Changed);
        Assert.Contains(result.Skipped, s => s.Rule == "FP000");
    }

    // ---- FormControlFactory ----

    [Fact]
    public void Factory_emits_the_mandatory_control_shape()
    {
        var el = FormControlFactory.Create("Grid", "Grid");
        var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");

        Assert.Equal("AxFormGridControl", (string?)el.Attribute(xsi + "type"));
        Assert.Equal("Grid", el.Element("Name")!.Value);
        Assert.Equal("Grid", el.Element("Type")!.Value);
        Assert.Equal("true", (string?)el.Element("FormControlExtension")!.Attribute(xsi + "nil"));
        Assert.NotNull(el.Element("Controls")); // containers must declare the collection
    }

    [Fact]
    public void Factory_omits_the_child_collection_for_leaf_controls()
    {
        var el = FormControlFactory.CreateBoundField("String", "Grid_VIN", "FmVehicle", "VIN");
        Assert.Null(el.Element("Controls"));
        Assert.Equal("VIN", el.Element("DataField")!.Value);
        Assert.Equal("FmVehicle", el.Element("DataSource")!.Value);
    }

    [Fact]
    public void Factory_puts_layout_properties_after_the_child_collection()
    {
        var el = FormControlFactory.Create("Group", "G", new Dictionary<string, string>
        {
            ["Style"] = "CustomFilter",   // trailing
            ["Caption"] = "Filters",      // leading
        });

        var names = el.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(names.IndexOf("Caption") < names.IndexOf("Controls"));
        Assert.True(names.IndexOf("Style") > names.IndexOf("Controls"));
    }

    [Fact]
    public void Factory_maps_types_without_a_metamodel_class_to_their_real_control()
    {
        // There is no AxFormMultilineTextControl — it is a String control.
        Assert.Equal("AxFormStringControl", FormControlFactory.AxTypeFor("MultilineText"));
        // Extension controls carry no i:type at all.
        Assert.Equal("", FormControlFactory.AxTypeFor("Control"));
    }

    private static string DescribeFailure(FormRepairResult r) =>
        "after: " + string.Join(" | ", r.After.Violations.Select(v => $"{v.Rule} {v.Path}: {v.Excerpt}")) +
        "  changes: " + string.Join(" | ", r.Changes.Select(c => c.Detail)) +
        "  skipped: " + string.Join(" | ", r.Skipped.Select(c => c.Detail));
}
