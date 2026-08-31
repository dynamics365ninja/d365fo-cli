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
    public void A_pattern_whose_mandatory_sub_pattern_is_unmodelled_is_refused()
    {
        // The AOS knows this pattern as "Task" and insists on the one sub-pattern its
        // registry entry allows there ("Pattern 'Task Single' requires a sub-pattern
        // specified on control …/OverviewTab"). That sub-pattern — ToolbarList — is not in
        // this repo's catalog, so the form cannot be built correctly and is not built at all.
        var task = Resolve("TaskSingle");
        Assert.Equal("Task", task.XmlName);
        Assert.False(FormPatternExpander.CanExpand(task, out var reason));
        Assert.Contains("sub-pattern", reason);
        Assert.Null(FormPatternExpander.Expand(task, new FormExpandOptions("ConDemoTaskForm")));
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
        var spec = new FormPatternSpec
        {
            Id = "ConDemoSynthetic",
            XmlName = "ConDemoSynthetic",
            DisplayName = "Synthetic",
            Versions = ["1.0"],
            Purpose = "test",
            DesignProperties = new Dictionary<string, string> { ["Style"] = "SimpleList" },
            Root =
            [
                new NodeSpec { Id = "MainGrid", ControlTypes = ["Grid"], NameHint = "MainGrid" },
            ],
        };

        var doc = FormPatternExpander.Expand(spec, new FormExpandOptions(
            "ConDemoGridForm",
            DsTable: "ConDemoTable",
            GridFields: ["VehicleId", "AcquiredDate"],
            ControlTypeResolver: f => f == "AcquiredDate" ? ("AxFormDateControl", "Date") : ("AxFormStringControl", "String")));

        var xml = doc!.ToString();
        Assert.Contains("<DataSource>ConDemoTable</DataSource>", xml);
        Assert.Contains("<Table>ConDemoTable</Table>", xml);
        // Columns come out typed through the resolver, the same way the templates do it.
        Assert.Contains("i:type=\"AxFormDateControl\"", xml);
        Assert.Contains("<DataField>VehicleId</DataField>", xml);
        Assert.Contains("<DataField>AcquiredDate</DataField>", xml);
    }
}
