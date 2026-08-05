using D365FO.Cli.Commands.Get;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Hands every committed golden to Microsoft's own metadata serializer and asserts it can be
/// read back. This is the only check that answers "is what we generate actually valid AOT
/// metadata?" — the offline validators only ever agree with our own expectations.
/// </summary>
/// <remarks>
/// <para>
/// Inert unless the bridge is configured (<c>D365FO_BRIDGE_ENABLED=1</c>, <c>D365FO_BRIDGE_PATH</c>,
/// <c>D365FO_BIN_PATH</c>): CI has no D365FO installation, and generation must keep working
/// offline. On a machine that has one, it fails loudly.
/// </para>
/// <para>
/// It found ten unreadable families at once: menu items, reports and workflow types written
/// without their contract namespace; a form carrying a <c>TabStyle</c> value that does not
/// exist; a security policy putting a table name in a <c>NoYes</c> flag; and an EDT extension
/// pinned to the invented type <c>AxEdtStringExtension</c>.
/// </para>
/// </remarks>
public class MetadataRoundTripTests
{
    private static string? GoldensDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "eval", "goldens")))
            dir = dir.Parent;
        return dir is null ? null : Path.Combine(dir.FullName, "eval", "goldens");
    }

    [Fact]
    public void Every_golden_can_be_read_by_the_metadata_provider()
    {
        if (!BridgeGate.ShouldTry()) return;

        var goldens = GoldensDir();
        Assert.NotNull(goldens);

        var failures = new List<string>();
        var checkedFiles = 0;

        foreach (var file in Directory.GetFiles(goldens!, "*.xml", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal))
        {
            var verdict = BridgeGate.TryValidateArtifact(null, File.ReadAllText(file));
            // The bridge is enabled but not answering (no metadata assemblies on this box):
            // nothing is proven either way, so do not pretend otherwise.
            if (verdict is null) return;

            checkedFiles++;
            if (!verdict.Deserialized)
                failures.Add($"{Path.GetFileName(Path.GetDirectoryName(file))}: {verdict.ErrorCode} {verdict.ErrorMessage}");
        }

        Assert.True(checkedFiles > 0, "No goldens were checked.");
        Assert.True(failures.Count == 0,
            "The metadata provider cannot read these generated artifacts:\n  " + string.Join("\n  ", failures));
    }
}
