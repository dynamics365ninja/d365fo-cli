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
