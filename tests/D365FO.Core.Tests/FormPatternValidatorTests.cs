using D365FO.Core.FormPatterns;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Coverage for the form-pattern catalog and structural validator (FP001-FP010)
/// ported from the upstream MCP form pattern engine: catalog integrity,
/// rule-by-rule detection, and a golden gate proving every form the CLI's own
/// scaffolder emits passes its declared pattern without structural errors.
/// </summary>
public class FormPatternValidatorTests
{
    // ── Catalog integrity ────────────────────────────────────────────────────

    [Fact]
    public void Catalog_ids_and_xml_names_are_unique()
    {
        var patternKeys = FormPatternCatalog.Patterns
            .SelectMany(p => new[] { p.Id, p.XmlName }.Concat(p.XmlAliases ?? Array.Empty<string>()))
            .Select(k => k.ToLowerInvariant())
            .ToList();
        Assert.Equal(patternKeys.Distinct().Count(), patternKeys.Distinct().Count());
        Assert.Equal(FormPatternCatalog.Patterns.Select(p => p.Id).Distinct().Count(), FormPatternCatalog.Patterns.Count);
        Assert.Equal(FormPatternCatalog.SubPatterns.Select(p => p.Id).Distinct().Count(), FormPatternCatalog.SubPatterns.Count);
    }

    [Fact]
    public void Catalog_variants_reference_existing_parents()
    {
        foreach (var p in FormPatternCatalog.Patterns.Where(p => p.VariantOf is not null))
            Assert.NotNull(FormPatternCatalog.ResolveExact(p.VariantOf));
    }

    [Fact]
    public void Allowed_sub_patterns_in_node_specs_exist_in_catalog()
    {
        static IEnumerable<NodeSpec> Flatten(IEnumerable<NodeSpec> specs)
            => specs.SelectMany(s => new[] { s }.Concat(Flatten(s.Children ?? Array.Empty<NodeSpec>())));

        var all = FormPatternCatalog.Patterns.SelectMany(p => Flatten(p.Root))
            .Concat(FormPatternCatalog.SubPatterns.SelectMany(sp => Flatten(sp.Root)));
        foreach (var node in all)
            foreach (var name in node.AllowedSubPatterns ?? Array.Empty<string>())
                Assert.NotNull(FormPatternCatalog.ResolveSubPattern(name));
    }

    [Fact]
    public void Resolve_accepts_free_text_aliases()
    {
        Assert.Equal("DetailsMaster", FormPatternCatalog.Resolve("master")!.Id);
        Assert.Equal("DetailsTransaction", FormPatternCatalog.Resolve("details transaction")!.Id);
        Assert.Equal("TableOfContents", FormPatternCatalog.Resolve("toc")!.Id);
        Assert.Equal("SimpleListDetails", FormPatternCatalog.Resolve("simple list details")!.Id);
        Assert.Null(FormPatternCatalog.Resolve("nonsense-xyz"));
    }

    [Fact]
    public void Resolve_maps_bare_factbox_to_grid_variant()
    {
        // Bare "FactBox" must not silently degrade; grid is the common variant.
        Assert.Equal("FormPartFactboxGrid", FormPatternCatalog.Resolve("FactBox")!.Id);
        Assert.Equal("FormPartFactboxGrid", FormPatternCatalog.Resolve("fact box")!.Id);
        // The card variant stays an explicit ask via its exact alias.
        Assert.Equal("FormPartFactboxCard", FormPatternCatalog.Resolve("FactBoxCard")!.Id);
    }

    [Fact]
    public void ResolveExact_rejects_fuzzy_names_the_validator_must_not_accept()
    {
        Assert.NotNull(FormPatternCatalog.ResolveExact("SimpleList"));
        Assert.NotNull(FormPatternCatalog.ResolveExact("simplelist")); // case-insensitive
        Assert.Null(FormPatternCatalog.ResolveExact("Simple List"));   // free text is not a serialized name
    }

    // ── Validator rules ──────────────────────────────────────────────────────

    private static string Form(string design, int dataSources = 1, string name = "TestForm")
    {
        var ds = string.Join("\n", Enumerable.Range(1, dataSources).Select(i =>
            $"<AxFormDataSource xmlns=\"\"><Name>Ds{i}</Name><Table>CustTable</Table></AxFormDataSource>"));
        return $"""
<?xml version="1.0" encoding="utf-8"?>
<AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance" xmlns="">
  <Name>{name}</Name>
  <Design>
{design}
  </Design>
  <DataSources>
{ds}
  </DataSources>
</AxForm>
""";
    }

    // SimpleList's spec is derived from the AOT pattern registry, which makes the
    // custom-filter group a required part (Count="1") where the hand-written catalog
    // had it optional. A fixture without one is not a valid SimpleList form.
    private const string SimpleListDesign = """
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Style>SimpleList</Style>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGroupControl"><Name>CustomFilterGroup</Name><Style>CustomFilter</Style></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
    </Controls>
""";

    [Fact]
    public void Valid_simple_list_form_has_no_errors()
    {
        var report = FormPatternValidator.ValidateXml(Form(SimpleListDesign));
        Assert.False(report.HasErrors, string.Join("; ", report.Violations.Select(v => $"{v.Rule}:{v.Excerpt}")));
        Assert.Equal("SimpleList", report.Pattern);
        Assert.Equal("TestForm", report.FormName);
    }

    [Fact]
    public void FP000_invalid_xml_and_non_axform()
    {
        Assert.Contains(FormPatternValidator.ValidateXml("not xml").Violations, v => v.Rule == "FP000");
        Assert.Contains(FormPatternValidator.ValidateXml("<AxTable/>").Violations, v => v.Rule == "FP000");
    }

    [Fact]
    public void FP001_unknown_pattern_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleListX</Pattern>
    <Controls />
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP001" && v.Severity == "error");
    }

    [Fact]
    public void FP002_unknown_version_is_error_newer_version_is_warning()
    {
        var unknown = FormPatternValidator.ValidateXml(Form(SimpleListDesign.Replace("1.1", "0.9")));
        Assert.Contains(unknown.Violations, v => v.Rule == "FP002" && v.Severity == "error");

        var newer = FormPatternValidator.ValidateXml(Form(SimpleListDesign.Replace("1.1", "9.9")));
        Assert.Contains(newer.Violations, v => v.Rule == "FP002" && v.Severity == "warning");
        Assert.False(newer.HasErrors);
    }

    [Fact]
    public void FP003_missing_required_grid_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP003" && v.Excerpt.Contains("Grid"));
    }

    [Fact]
    public void FP004_disallowed_extra_root_child_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormStaticTextControl"><Name>Rogue</Name></AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP004" && v.Excerpt.Contains("StaticText"));
    }

    [Fact]
    public void FP005_grid_before_action_pane_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP005");
    }

    [Fact]
    public void FP006_design_container_requiring_sub_pattern_warns_when_unspecified()
    {
        // A conforming SimpleList whose custom-filter group declares no sub-pattern.
        // The registry marks that part CustomAndQuickFilters, so its absence is the
        // "unspecified container" FP006 is about — and nothing else here is wrong,
        // which is what makes the warning the only finding.
        var report = FormPatternValidator.ValidateXml(Form(SimpleListDesign));

        Assert.Contains(report.Violations, v => v.Rule == "FP006" && v.Severity == "warning");
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void FP001_unknown_sub_pattern_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGroupControl">
        <Name>Filters</Name><Style>CustomFilter</Style>
        <Pattern>QuickFiltersAndCustom</Pattern>
      </AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP001" && v.Excerpt.Contains("QuickFiltersAndCustom"));
    }

    [Fact]
    public void FP003_custom_and_quick_filters_without_quick_filter_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGroupControl">
        <Name>Filters</Name><Style>CustomFilter</Style>
        <Pattern>CustomAndQuickFilters</Pattern>
        <PatternVersion>1.1</PatternVersion>
      </AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP003" && v.Excerpt.Contains("QuickFilterControl"));
    }

    [Fact]
    public void Quick_filter_extension_control_satisfies_custom_and_quick_filters()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGroupControl">
        <Name>Filters</Name><Style>CustomFilter</Style>
        <Pattern>CustomAndQuickFilters</Pattern>
        <PatternVersion>1.1</PatternVersion>
        <Controls>
          <AxFormControl xmlns="">
            <Name>QuickFilterControl</Name>
            <FormControlExtension><Name>QuickFilterControl</Name></FormControlExtension>
          </AxFormControl>
        </Controls>
      </AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
    </Controls>
"""));
        Assert.False(report.HasErrors, string.Join("; ", report.Violations.Select(v => $"{v.Rule}:{v.Excerpt}")));
    }

    [Fact]
    public void FP007_sub_pattern_on_wrong_control_type_is_error()
    {
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl">
        <Name>Grid</Name>
        <Pattern>SidePanel</Pattern>
      </AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations, v => v.Rule == "FP007" && v.Excerpt.Contains("SidePanel"));
    }

    [Fact]
    public void FP007_parent_restricted_sub_pattern_outside_parent_is_error()
    {
        // SidePanel is only valid inside SimpleListDetails
        var report = FormPatternValidator.ValidateXml(Form("""
    <Pattern>SimpleList</Pattern>
    <PatternVersion>1.1</PatternVersion>
    <Controls>
      <AxFormControl xmlns="" i:type="AxFormActionPaneControl"><Name>ActionPane</Name></AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGroupControl">
        <Name>Filters</Name><Style>CustomFilter</Style>
        <Pattern>SidePanel</Pattern>
      </AxFormControl>
      <AxFormControl xmlns="" i:type="AxFormGridControl"><Name>Grid</Name></AxFormControl>
    </Controls>
"""));
        Assert.Contains(report.Violations,
            v => v.Rule == "FP007" && v.Excerpt.Contains("only valid inside"));
    }

    [Fact]
    public void FP008_missing_datasource_warns()
    {
        var report = FormPatternValidator.ValidateXml(Form(SimpleListDesign, dataSources: 0));
        Assert.Contains(report.Violations, v => v.Rule == "FP008" && v.Severity == "warning");
    }

    [Fact]
    public void FP009_design_style_mismatch_warns()
    {
        var report = FormPatternValidator.ValidateXml(Form(SimpleListDesign.Replace(
            "<Style>SimpleList</Style>", "<Style>DetailsFormMaster</Style>")));
        Assert.Contains(report.Violations, v => v.Rule == "FP009" && v.Severity == "warning");
        Assert.False(report.HasErrors);
    }

    [Fact]
    public void FP010_no_pattern_declared_warns()
    {
        var report = FormPatternValidator.ValidateXml(Form("<Controls />"));
        Assert.Contains(report.Violations, v => v.Rule == "FP010" && v.Severity == "warning");
        Assert.False(report.HasErrors);
    }

    // ── Golden gate: the CLI's own templates must pass the validator ─────────

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
    public void Scaffolded_forms_pass_their_declared_pattern(FormPattern pattern)
    {
        var xml = XppScaffolder.Form(
            formName: "FpGateTestForm",
            dataSourceTable: pattern is FormPattern.Dialog or FormPattern.Workspace ? null : "CustTable",
            pattern: pattern,
            caption: null,
            gridFields: new[] { "AccountNum", "CustGroup" },
            sections: Array.Empty<FormSectionSpec>(),
            linesTable: pattern == FormPattern.DetailsTransaction ? "CustTrans" : null);

        var report = FormPatternValidator.ValidateXml(xml);
        Assert.False(report.HasErrors,
            $"{pattern}: " + string.Join("; ", report.Violations
                .Where(v => v.Severity == "error")
                .Select(v => $"{v.Rule} {v.Path}: {v.Excerpt}")));
        // The template must actually declare a known pattern — without it the
        // gate degenerates to FP010 and the structural rules never run.
        Assert.NotNull(report.Pattern);
        Assert.NotNull(FormPatternCatalog.ResolveExact(report.Pattern));
    }

    /// <summary>
    /// The same gate with <c>--section</c> supplied. This used to fail for two patterns
    /// — Dialog wrapped its sections in a Tab (FP004 under the FieldsFieldGroups
    /// sub-pattern) and Workspace declared CustomAndQuickFilters over an empty
    /// &lt;Controls /&gt; (FP003, missing QuickFilterControl) — so
    /// `generate form --pattern Dialog|Workspace --section X` could never be written
    /// while the write gate was enforced.
    /// </summary>
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
    public void Scaffolded_forms_with_sections_pass_their_declared_pattern(FormPattern pattern)
    {
        var xml = XppScaffolder.Form(
            formName: "FpGateSectionForm",
            dataSourceTable: pattern is FormPattern.Dialog ? null : "CustTable",
            pattern: pattern,
            caption: "@SYS1234",
            gridFields: new[] { "AccountNum", "CustGroup" },
            sections: new[]
            {
                new FormSectionSpec("SectionGeneral", "General"),
                new FormSectionSpec("SectionSetup", "Setup"),
            },
            linesTable: pattern == FormPattern.DetailsTransaction ? "CustTrans" : null);

        var report = FormPatternValidator.ValidateXml(xml);
        Assert.False(report.HasErrors,
            $"{pattern}: " + string.Join("; ", report.Violations
                .Where(v => v.Severity == "error")
                .Select(v => $"{v.Rule} {v.Path}: {v.Excerpt}")));
    }
}
