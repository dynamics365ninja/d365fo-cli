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

    [Theory]
    [InlineData("Wizard")]
    [InlineData("DropDialog")]
    [InlineData("TaskSingle")]
    [InlineData("FormPartSectionList")]
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

        // The declared version is the spec's own newest — not the registry's, which can be
        // ahead of the catalog (that skew produced an FP002 on the first cut of this).
        Assert.Contains($"<PatternVersion>{spec.Versions[0]}</PatternVersion>", doc.ToString());
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
