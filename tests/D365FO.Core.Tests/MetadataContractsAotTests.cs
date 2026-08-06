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

        static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return []; }
            catch (IOException) { return []; }
        }
    }

    [Fact]
    public void No_shipped_file_trips_the_invalid_enum_rule()
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

            foreach (var v in violations.Where(v => v.Rule == ContractShapeRules.RuleInvalidEnumValue).Take(2))
                findings.Add($"{Path.GetFileName(file)}: {v.Excerpt} — {v.Fix}");

            if (findings.Count > 20) break;
        }

        Assert.True(findings.Count == 0,
            $"XML008 flagged enum values in files Microsoft ships ({samples.Count} sampled), so the "
            + "catalog's enum data — not the files — is wrong:\n  " + string.Join("\n  ", findings.Take(10)));
    }

    /// <summary>
    /// The reason ordering needs an <em>effective</em> type: shipped forms write
    /// <c>&lt;AxFormDataSource&gt;</c>, an abstract five-member contract, and fill it with an
    /// <c>AxFormDataSourceRoot</c>. Ranking those members against the base finds no position for
    /// them, so they stay where they were and the serializer drops them on read.
    /// </summary>
    [Fact]
    public void Element_named_after_an_abstract_base_resolves_to_the_subtype_it_carries()
    {
        var members = new[] { "Name", "Table", "Fields", "ReferencedDataSources", "AllowDelete", "DataSourceLinks" };

        var resolved = MetadataContracts.EffectiveContract("AxFormDataSource", xsiType: null, members);

        Assert.NotNull(resolved);
        Assert.Equal("AxFormDataSourceRoot", resolved!.Name);
        Assert.All(members, m => Assert.True(resolved.IndexOf(m) >= 0, $"{m} unranked on {resolved.Name}"));
    }

    /// <summary>An explicit discriminator is authoritative and must not be second-guessed.</summary>
    [Fact]
    public void An_explicit_xsi_type_wins_over_member_based_resolution()
    {
        var resolved = MetadataContracts.EffectiveContract(
            "AxReportDesign", xsiType: "AxReportPrecisionDesign", ["Name", "StyleTemplate", "Text"]);

        Assert.Equal("AxReportPrecisionDesign", resolved?.Name);
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
