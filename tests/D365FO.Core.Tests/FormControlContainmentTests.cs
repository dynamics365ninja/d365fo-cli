using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using D365FO.Core.Metadata;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>Which control may sit under which parent (issue #290).</summary>
public class FormControlContainmentTests
{
    [Theory]
    [InlineData("AxFormGridControl", "AxFormRadioButtonControl", false)]
    [InlineData("AxFormGridControl", "AxFormStringControl", true)]
    [InlineData("AxFormGridControl", "AxFormGroupControl", true)]
    [InlineData("AxFormGridControl", "AxFormControl", true)]
    [InlineData("AxFormGroupControl", "AxFormRadioButtonControl", true)]
    [InlineData("AxFormGroupControl", "AxFormTabPageControl", false)]
    [InlineData("AxFormTabControl", "AxFormTabPageControl", true)]
    [InlineData("AxFormTabControl", "AxFormStringControl", false)]
    [InlineData("AxFormDesign", "AxFormGridControl", true)]
    [InlineData("AxFormDesign", "AxFormTabPageControl", false)]
    [InlineData("AxFormStringControl", "AxFormStringControl", false)]
    // A type the metadata model does not declare is the write's to refuse, by name.
    [InlineData("AxFormGridControl", "AxFormNoSuchControl", true)]
    public void A_pairing_is_judged_as_the_compiler_judges_it(string parent, string child, bool allowed)
        => Assert.Equal(allowed, FormControlContainment.Allows(parent, child));

    [Fact]
    public void Every_type_the_rule_tables_name_is_one_the_contract_catalog_declares()
    {
        var tables = MetadataContracts.AllowedChildControls
            .Concat(MetadataContracts.DisallowedChildControls).ToList();

        Assert.NotEmpty(tables);
        foreach (var (parent, children) in tables)
        {
            Assert.NotNull(MetadataContracts.Find(parent));
            Assert.All(children, child => Assert.NotNull(MetadataContracts.Find(child)));
        }
    }

    /// <summary>
    /// The refusal must stay quiet on forms known to compile. Inert unless
    /// <c>D365FO_PACKAGES_PATH</c> points at a real <c>PackagesLocalDirectory</c>.
    /// </summary>
    [Fact]
    public void No_shipped_form_nests_a_control_the_rule_refuses()
    {
        var root = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;

        XName type = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type";
        var findings = new List<string>();
        var files = Directory.EnumerateDirectories(root)
            .Where(ShippedAot.IsMicrosoftPackage)
            .SelectMany(package => Directory.EnumerateDirectories(package))
            .Select(model => Path.Combine(model, "AxForm"))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.xml").Take(40));

        foreach (var file in files)
        {
            var design = XDocument.Load(file).Root!.Elements().FirstOrDefault(e => e.Name.LocalName == "Design");
            if (design is null) continue;
            foreach (var control in design.Descendants().Where(e => e.Name.LocalName == "AxFormControl"))
            {
                var parent = control.Parent!.Parent!;
                var parentType = parent == design
                    ? FormControlContainment.DesignType
                    : (string?)parent.Attribute(type) ?? FormControlContainment.UntypedControl;
                var childType = (string?)control.Attribute(type) ?? FormControlContainment.UntypedControl;
                if (!FormControlContainment.Allows(parentType, childType))
                    findings.Add($"{Path.GetFileName(file)}: {childType} under {parentType}");
            }
            if (findings.Count > 20) break;
        }

        Assert.True(findings.Count == 0,
            "The containment rule refuses nesting Microsoft ships:\n  " + string.Join("\n  ", findings.Take(10)));
    }
}
