using System.Xml.Linq;
using D365FO.Core.Metadata;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Calibrates the enum half of the contract catalog — and the rule built on it, XML008 —
/// against AOT that is known good: the files Microsoft ships.
/// </summary>
/// <remarks>
/// <para>
/// A checker is only worth its findings once it has been shown to stay quiet on correct input.
/// This repo has already produced two checks that looked right and were not: a round-trip diff
/// that counted every leaf of an <c>i:type</c>'d document as dropped, and a strict member-order
/// rule that flagged Microsoft's own files. Both would have been caught here in seconds.
/// </para>
/// <para>
/// XML007's own calibration lives in <see cref="ObjectTypeRegistryAotTests"/>
/// (<c>Contract_catalog_knows_every_member_shipped_files_use</c>); this covers what that one
/// cannot see, since an enum value is text inside a member the catalog already accepts.
/// </para>
/// <para>
/// Inert unless <c>D365FO_PACKAGES_PATH</c> points at a real <c>PackagesLocalDirectory</c>.
/// A test that passes on an empty directory proves nothing.
/// </para>
/// </remarks>
public class MetadataContractsAotTests
{
    private static string? PackagesRoot()
    {
        var root = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
    }

    /// <summary>
    /// Families whose values exercise the widest enum surface — form controls alone account for
    /// most of it, and security/menu items bring the rest.
    /// </summary>
    private static readonly string[] Interesting =
    [
        "AxForm", "AxTable", "AxView", "AxQuery", "AxDataEntityView", "AxMenuItemDisplay",
        "AxMenuItemAction", "AxSecurityPrivilege", "AxSecurityRole", "AxSecurityPolicy",
        "AxEdt", "AxEnum", "AxTableExtension", "AxFormExtension", "AxReport",
    ];

    private static IEnumerable<string> SampleFiles(string root, int perFolder)
    {
        foreach (var package in SafeDirs(root))
        {
            // "What Microsoft ships" — a developer VM's package root also holds what its
            // developers and other tools wrote, and their defects are not this census's.
            if (!ShippedAot.IsMicrosoftPackage(package)) continue;
            foreach (var model in SafeDirs(package))
                foreach (var name in Interesting)
                {
                    var dir = Path.Combine(model, name);
                    if (!Directory.Exists(dir)) continue;

                    var taken = 0;
                    foreach (var file in Directory.EnumerateFiles(dir, "*.xml"))
                    {
                        // Skip the giant SSRS designs: the bulk is an embedded RDL document,
                        // which says nothing about the AOT contract and costs seconds to parse.
                        if (new FileInfo(file).Length > 512 * 1024) continue;
                        yield return file;
                        if (++taken >= perFolder) break;
                    }
                }
        }

        static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return []; }
            catch (IOException) { return []; }
        }
    }

    /// <summary>
    /// Runs the real rules over shipped files. Broader than the catalog-completeness check in
    /// <see cref="ObjectTypeRegistryAotTests"/>, which resolves each element by its own name:
    /// this walks the way the rules do, descending into member-typed sub-objects
    /// (<c>&lt;Grant&gt;</c>, <c>&lt;Design&gt;</c>) that name no type at all.
    /// </summary>
    [Fact]
    public void No_shipped_file_trips_the_contract_shape_rules()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var samples = SampleFiles(root, perFolder: 12).ToList();
        if (samples.Count == 0) return;

        var findings = new List<string>();
        foreach (var file in samples)
        {
            string xml;
            try { xml = File.ReadAllText(file); }
            catch (IOException) { continue; }

            var violations = new List<XppViolation>();
            ContractShapeRules.Check(xml, violations);

            foreach (var v in violations.Take(2))
                findings.Add($"{Path.GetFileName(file)}: {v.Rule} {v.Excerpt} — {v.Fix}");

            if (findings.Count > 20) break;
        }

        Assert.True(findings.Count == 0,
            $"The contract-shape rules flagged files Microsoft ships ({samples.Count} sampled), so the "
            + "rules — not the files — are wrong:\n  " + string.Join("\n  ", findings.Take(10)));
    }

    /// <summary>
    /// <c>&lt;AxFormDataSource&gt;</c> in a shipped form is not the abstract CLR type of that
    /// name — it is <c>AxFormDataSourceRoot</c>, which <em>contracts</em> to that name. Keyed by
    /// CLR name the catalog answered with the abstract base's five members, leaving
    /// <c>AllowDelete</c> and <c>DataSourceLinks</c> unrankable and therefore dropped on read.
    /// </summary>
    [Fact]
    public void An_element_resolves_to_the_type_that_contracts_to_its_name()
    {
        var resolved = MetadataContracts.Find("AxFormDataSource");

        Assert.NotNull(resolved);
        Assert.False(resolved!.IsAbstract);
        foreach (var m in new[] { "Name", "Table", "Fields", "AllowDelete", "DataSourceLinks" })
            Assert.True(resolved.IndexOf(m) >= 0, $"{m} unranked on {resolved.Name}");
    }

    /// <summary>
    /// The same slip in the other direction: a collection item may be named after neither its
    /// CLR type nor its member. <c>&lt;Method&gt;</c> is an <c>AxMethodPropertyCollection</c>.
    /// </summary>
    [Fact]
    public void A_contract_name_that_looks_nothing_like_its_clr_type_still_resolves()
    {
        var method = MetadataContracts.Find("Method");

        Assert.NotNull(method);
        Assert.True(method!.IndexOf("Source") >= 0);
        Assert.True(method.IndexOf("Visibility") >= 0);
    }

    /// <summary>An explicit discriminator names the type; the element name is then irrelevant.</summary>
    [Fact]
    public void An_explicit_xsi_type_decides_the_contract()
    {
        var resolved = MetadataContracts.ForElement("AxReportDesign", xsiType: "AxReportPrecisionDesign");

        Assert.Equal("AxReportPrecisionDesign", resolved?.Name);
        Assert.True(resolved!.IndexOf("Text") >= 0, "the RDL body lives in Text");
    }

    [Fact]
    public void Catalog_carries_the_enum_values_generation_is_judged_against()
    {
        // A guard that the emitter's enum pass actually ran: without data, XML008 degrades to a
        // silent no-op, which is the failure mode of every check that depends on generated input.
        Assert.NotEmpty(MetadataContracts.Enums);

        var groupStyle = MetadataContracts.EnumValues("GroupStyle");
        Assert.Contains("TOCTopicList", groupStyle);

        // The two values this repo actually invented, both of which made the whole file
        // unreadable rather than merely lossy.
        Assert.DoesNotContain("TileSection", groupStyle);
        Assert.DoesNotContain("TOCList", MetadataContracts.EnumValues("TabStyle"));

        var group = MetadataContracts.Find("AxFormGroupControl");
        Assert.NotNull(group);
        Assert.Equal("GroupStyle", MetadataContracts.EnumForMember(group!, "Style"));
    }
}
