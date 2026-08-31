using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The catalog-driven form expander (port of upstream formControlExpander): patterns with no
/// hand-written template are emitted from the SAME spec the validator enforces, so the
/// skeleton is pattern-correct by construction — and a pattern the expander cannot faithfully
/// build is refused with the reason, never approximated.
/// </summary>
public class FormPatternExpanderTests
{
    private static FormPatternSpec Resolve(string name) => FormPatternCatalog.Resolve(name)!;

    /// <summary>
    /// First element with this local name, whatever namespace it sits in. The document mixes
    /// two on purpose — the root's direct children are in the form's own namespace and
    /// everything below them resets to the empty one, exactly as shipped AxForm files do.
    /// </summary>
    private static string? Value(XDocument doc, string localName) =>
        doc.Descendants().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

    private static IEnumerable<string> Values(XDocument doc, string localName) =>
        doc.Descendants().Where(e => e.Name.LocalName == localName).Select(e => e.Value);

    [Theory]
    [InlineData("Wizard")]
    [InlineData("DropDialog")]
    [InlineData("FormPartSectionList")]
    [InlineData("FormPartFactboxCard")]
    [InlineData("FormPartFactboxGrid")]
    public void Catalog_only_patterns_expand_and_pass_the_validators_own_gate(string pattern)
    {
        var spec = Resolve(pattern);
        Assert.True(FormPatternExpander.CanExpand(spec, out var reason), reason);

        var doc = FormPatternExpander.Expand(spec, new FormExpandOptions("ConDemoExpandedForm"));
        Assert.NotNull(doc);

        // Generation and validation share one source of truth — zero structural errors is
        // the expander's whole promise (FP006 sub-pattern warnings are allowed).
        var report = FormPatternValidator.ValidateXml(doc!.ToString());
        Assert.Equal(spec.XmlName, report.Pattern);
        Assert.False(report.HasErrors,
            string.Join("; ", report.Violations.Where(v => v.Severity == "error").Select(v => $"{v.Rule} {v.Path}")));

        // The declared version is the newest ACTIVE version the AOT registry has for this
        // pattern, falling back to the catalog's only where the registry does not know it.
        // The catalog's own newest is not enough: xppc rejects the form outright with
        // "Unable to validate pattern 'DropDialog 1.1'. Message: Pattern … not found", and
        // for Wizard the AOS's 1.2 is what Microsoft's own shipped Wizard form declares.
        var registryVersions = FormPatternRegistry.VersionsOf(spec.XmlName);
        var expected = registryVersions.Count > 0 ? registryVersions[0] : spec.Versions[0];
        Assert.Equal(expected, Value(doc, "PatternVersion"));
    }

    [Fact]
    public void Expanded_form_carries_the_property_values_the_AOS_requires()
    {
        // Compiled on a real installation: without these the AOS fails the form with
        // "Property 'VisibleRows' on control … must have value '5' per pattern
        // 'Form Part Factbox Grid'" — seven such errors on this one pattern.
        var doc = FormPatternExpander.Expand(
            Resolve("FormPartFactboxGrid"), new FormExpandOptions("ConDemoFactboxForm", DsTable: "ConDemoTable"))!;

        Assert.Contains("FormPart", Values(doc, "Style"));
        Assert.Contains("SimpleReadOnly", Values(doc, "Style"));
        Assert.Equal("5", Value(doc, "VisibleRows"));
        Assert.Equal("Fixed", Value(doc, "VisibleRowsMode"));
        Assert.Equal("No", Value(doc, "AllowEdit"));
    }

    [Fact]
    public void Root_children_stay_in_the_forms_own_namespace()
    {
        // <Name xmlns=""> is unreadable to the metadata provider: it deserializes an AxForm
        // with no name at all and xppc fails the file with "The element must be named
        // 'X' instead of '' to be consistent with its file name". Only the elements BELOW
        // the root's direct children reset to the empty namespace.
        var doc = FormPatternExpander
            .Expand(Resolve("Wizard"), new FormExpandOptions("ConDemoNamespacedForm"))!;

        var name = doc.Root!.Elements().First(e => e.Name.LocalName == "Name");
        Assert.Equal("ConDemoNamespacedForm", name.Value);
        Assert.Equal(doc.Root.Name.Namespace, name.Name.Namespace);
        Assert.DoesNotContain("<Name xmlns=\"\">ConDemoNamespacedForm</Name>", doc.ToString());
    }

    [Fact]
    public void A_container_the_AOS_wants_a_sub_pattern_on_gets_one_and_its_skeleton()
    {
        // The AOS knows this pattern as "Task" and insists on a sub-pattern on its tab page
        // ("Pattern 'Task Single' requires a sub-pattern specified on control …/OverviewTab").
        // Declaring one is not enough either — the tab page then has to hold what that
        // sub-pattern requires, which for ToolbarList is a grid.
        var task = Resolve("TaskSingle");
        Assert.Equal("Task", task.XmlName);

        var doc = FormPatternExpander.Expand(task, new FormExpandOptions(
            "ConDemoTaskForm", DsTable: "ConDemoTable", GridFields: ["VehicleId"]))!;

        Assert.Contains("ToolbarList", Values(doc, "Pattern"));
        Assert.Contains("Grid", Values(doc, "Type"));
        Assert.False(FormPatternValidator.ValidateXml(doc.ToString()).HasErrors);
    }

    [Fact]
    public void A_pattern_the_AOS_does_not_have_is_refused()
    {
        // Whatever the catalog says, a <Pattern> the AOT registry has no active version of
        // fails the build with "Pattern 'X 1.1' not found" — the catalog's own
        // "SimpleDetails" was exactly that until it was mapped onto the variant the platform
        // ships. So a name the registry does not carry is never expanded.
        var invented = new FormPatternSpec
        {
            Id = "ConDemoInvented",
            XmlName = "ConDemoInvented",
            DisplayName = "Invented",
            Versions = ["1.0"],
            Purpose = "test",
            Root = [new NodeSpec { Id = "MainGrid", ControlTypes = ["Grid"], NameHint = "MainGrid" }],
        };

        Assert.False(FormPatternExpander.CanExpand(invented, out var reason));
        Assert.Contains("no active pattern", reason);
        Assert.Null(FormPatternExpander.Expand(invented, new FormExpandOptions("ConDemoX")));
    }

    [Fact]
    public void Ambiguous_or_wildcard_patterns_are_refused_with_the_reason()
    {
        // TaskDouble's sibling slots share a control type — dropping optionals would shift
        // the validator's type-based matcher onto the wrong slot.
        var taskDouble = Resolve("TaskDouble");
        Assert.False(FormPatternExpander.CanExpand(taskDouble, out var reason));
        Assert.Contains("sharing a control type", reason);
        Assert.Null(FormPatternExpander.Expand(taskDouble, new FormExpandOptions("ConDemoX")));
    }

    [Fact]
    public void Templated_patterns_stay_on_their_templates()
    {
        // The nine hand-written templates are VM-proven; the expander exists only for the
        // patterns they do not cover. The command routes them first, so this is a guard on
        // the routing premise: every templated pattern still resolves through the enum.
        foreach (var name in Enum.GetNames<D365FO.Core.Scaffolding.FormPattern>())
        {
            Assert.True(D365FO.Core.Scaffolding.FormPatternNormalizer.TryNormalize(name, out _, out _), name);
        }
    }

    [Fact]
    public void Grid_slots_bind_the_datasource_and_render_columns()
    {
        var doc = FormPatternExpander.Expand(Resolve("FormPartSectionList"), new FormExpandOptions(
            "ConDemoGridForm",
            DsTable: "ConDemoTable",
            GridFields: ["VehicleId", "AcquiredDate"],
            ControlTypeResolver: f => f == "AcquiredDate" ? ("AxFormDateControl", "Date") : ("AxFormStringControl", "String")))!;

        var xml = doc.ToString();
        Assert.Contains("ConDemoTable", Values(doc, "DataSource"));
        Assert.Equal("ConDemoTable", Value(doc, "Table"));
        // Columns come out typed through the resolver, the same way the templates do it.
        Assert.Contains("i:type=\"AxFormDateControl\"", xml);
        Assert.Contains("VehicleId", Values(doc, "DataField"));
        Assert.Contains("AcquiredDate", Values(doc, "DataField"));
    }

    [Fact]
    public void Control_names_are_unique_across_the_whole_form()
    {
        // Both the catalog skeleton and the registry parts call a filter control
        // "QuickFilter", and DetailsMasterTabs has two of them in different branches. The
        // metadata provider refuses the file over it: "Element named: 'QuickFilter' of type
        // 'AxFormControl' already exists".
        var doc = FormPatternExpander.Expand(
            Resolve("DetailsMasterTabs"), new FormExpandOptions("ConDemoTabsForm", DsTable: "ConDemoTable"))!;

        var names = doc.Descendants()
            .Where(e => e.Name.LocalName == "AxFormControl")
            .Select(e => e.Element(e.Name.Namespace + "Name")?.Value)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList();

        Assert.NotEmpty(names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
