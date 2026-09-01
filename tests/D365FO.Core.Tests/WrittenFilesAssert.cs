using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Assertions about which files a write path actually put on disk.
/// </summary>
/// <remarks>
/// Asserting this off a <see cref="Directory.GetFiles(string, string)"/> listing is flaky on
/// Windows under load. <c>ScaffoldFileWriter</c> publishes by renaming a staged file onto the
/// target and then takes <c>FileInfo.Length</c> on that target, so a write that returned
/// normally has PROVEN the file was there. A directory enumeration taken in the same instant
/// can still be missing the entry the rename just created — this cost roughly one run in ten
/// of the full suite, always as "Expected 3, Actual 2" with no exception anywhere and no way
/// to tell a real lost write from an enumeration artefact.
///
/// Existence is therefore queried one path at a time, which does not have that window. The
/// listing is still taken — to catch files nobody asked for, and to put the directory's real
/// contents into the failure message when something genuinely did go missing.
/// </remarks>
internal static class WrittenFilesAssert
{
    /// <summary>Exactly <paramref name="expected"/> `.xml` files exist in <paramref name="dir"/>.</summary>
    public static void ExactlyTheseXml(string dir, params string[] expected)
    {
        var listed = Directory.GetFiles(dir, "*.xml")
            .Select(Path.GetFileName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Order()
            .ToArray();

        var missing = expected.Where(n => !File.Exists(Path.Combine(dir, n))).Order().ToArray();
        Assert.True(missing.Length == 0,
            $"These were never written: [{string.Join(", ", missing)}]. " +
            $"The directory lists [{string.Join(", ", listed)}].");

        var unexpected = listed.Except(expected, StringComparer.Ordinal).Order().ToArray();
        Assert.True(unexpected.Length == 0,
            $"Files nobody asked for: [{string.Join(", ", unexpected)}]. " +
            $"Expected exactly [{string.Join(", ", expected.Order())}].");
    }
}
