using System.Xml.Linq;
using D365FO.Core.Metadata;
using D365FO.Core.ObjectTypes;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Ground-truth check for the registry's folder names against a real installation.
/// Inert unless <c>D365FO_PACKAGES_PATH</c> points at a <c>PackagesLocalDirectory</c>
/// — CI has no AOS, and a test that silently passes on an empty directory would be
/// exactly the kind of confident lie this repo's triage rubric warns about, so the
/// assertions only run once real packages are found.
/// </summary>
/// <remarks>
/// This is the check that would have caught audit finding G1 years earlier:
/// <c>AxWorkflowType</c> was read by the extractor and matches no folder on any AOS.
/// </remarks>
public class ObjectTypeRegistryAotTests
{
    private static string? PackagesRoot()
    {
        var root = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
    }

    /// <summary>Distinct <c>Ax*</c> folder names present under &lt;root&gt;/&lt;package&gt;/&lt;model&gt;/.</summary>
    private static HashSet<string> AotFolderNames(string root)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var package in SafeDirs(root))
            foreach (var model in SafeDirs(package))
                foreach (var folder in SafeDirs(model))
                {
                    var name = Path.GetFileName(folder);
                    if (name.StartsWith("Ax", StringComparison.Ordinal)) names.Add(name);
                }
        return names;

        static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }
    }

    [Fact]
    public void Registry_folder_names_match_a_real_installation()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var actual = AotFolderNames(root);
        if (actual.Count == 0) return; // Path exists but holds no AOT — nothing proven either way.

        var wrong = ObjectTypeRegistry.All
            .Where(t => t.ExistsInStandardAot && !actual.Contains(t.AotSubfolder))
            .Select(t => $"{t.Kind} → {t.AotSubfolder}")
            .ToList();

        Assert.True(wrong.Count == 0,
            "Registry claims these folders exist, but no package/model under " + root + " has them: " +
            string.Join(", ", wrong));
    }

    /// <summary>
    /// Proves the contract catalog against shipped files: every element Microsoft writes must
    /// be a member the catalog knows for that type. This is what makes XML007 trustworthy —
    /// if the catalog were incomplete, the rule would flag correct files.
    /// </summary>
    /// <remarks>
    /// Note what is deliberately <em>not</em> asserted: that shipped files follow contract
    /// order. They do not, everywhere, and the provider reads them back with no loss anyway —
    /// which is why order is canonicalised on output but never linted.
    /// </remarks>
    [Fact]
    public void Contract_catalog_knows_every_member_shipped_files_use()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var samples = SampleFiles(root, perFolder: 12).ToList();
        if (samples.Count == 0) return;

        var unknown = new List<string>();
        foreach (var file in samples)
        {
            XDocument doc;
            try { doc = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }
            if (doc.Root is not null) CheckMembers(doc.Root, unknown);
        }

        Assert.True(unknown.Count == 0,
            $"The catalog is missing members that {samples.Count} shipped files use:\n  " +
            string.Join("\n  ", unknown.Distinct().Take(15)));
    }

    private static void CheckMembers(XElement element, List<string> unknown)
    {
        var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");
        var contract = MetadataContracts.ForElement(element.Name.LocalName, element.Attribute(xsi + "type")?.Value);
        if (contract is not null)
        {
            foreach (var child in element.Elements())
            {
                // Collection items are named after their own type, not after a member.
                if (MetadataContracts.Find(child.Name.LocalName) is not null) continue;
                if (!MetadataContracts.AcceptsMember(contract, child.Name.LocalName))
                    unknown.Add($"{contract.Name}.{child.Name.LocalName}");
            }
        }

        foreach (var child in element.Elements()) CheckMembers(child, unknown);
    }

    private static IEnumerable<string> SampleFiles(string root, int perFolder)
    {
        var interesting = new[]
        {
            "AxTable", "AxClass", "AxForm", "AxQuery", "AxView", "AxMap", "AxDataEntityView",
            "AxMenuItemDisplay", "AxReport", "AxSecurityPolicy", "AxSecurityPrivilege",
            "AxSecurityRole", "AxWorkflowTemplate", "AxWorkflowApproval", "AxService",
            "AxTableExtension", "AxFormExtension", "AxEdtExtension", "AxEdt", "AxEnum",
        };

        foreach (var package in SafeDirs(root))
        {
            if (!ShippedAot.IsMicrosoftPackage(package)) continue; // see ShippedAot
            foreach (var model in SafeDirs(package))
                foreach (var name in interesting)
                {
                    var dir = Path.Combine(model, name);
                    if (!Directory.Exists(dir)) continue;
                    foreach (var file in Directory.EnumerateFiles(dir, "*.xml").Take(perFolder))
                        yield return file;
                }
        }

        static IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
        }
    }

    [Fact]
    public void Folders_marked_absent_really_are_absent()
    {
        var root = PackagesRoot();
        if (root is null) return;

        var actual = AotFolderNames(root);
        if (actual.Count == 0) return;

        var present = ObjectTypeRegistry.All
            .Where(t => !t.ExistsInStandardAot && actual.Contains(t.AotSubfolder))
            .Select(t => $"{t.Kind} → {t.AotSubfolder}")
            .ToList();

        Assert.True(present.Count == 0,
            "These folders are marked as existing on no AOS, but " + root + " has them: " +
            string.Join(", ", present));
    }
}
