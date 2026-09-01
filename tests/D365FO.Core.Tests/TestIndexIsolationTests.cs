using D365FO.Core;
using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The suite must journal into its own directory, never the one the developer's index uses.
/// </summary>
/// <remarks>
/// Without this, a full run appended into <c>&lt;dirname(D365FO_INDEX_DB)&gt;/journal</c> — the
/// real undo journal — and the 500-entry cap then pruned the developer's genuine history away.
/// It cost nothing to notice and there was nothing watching, so it is watched here.
/// </remarks>
public class TestIndexIsolationTests
{
    [Fact]
    public void Journal_resolves_under_the_isolated_test_root_not_the_machine_index()
    {
        var journalDir = ModificationJournal.ForIndex().JournalDirectory;
        var testRoot = Path.Combine(Path.GetTempPath(), "d365fo-testrun");

        Assert.StartsWith(testRoot, Path.GetFullPath(journalDir), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Index_path_resolves_under_the_isolated_test_root()
    {
        var db = Path.GetFullPath(D365FoSettings.FromEnvironment().DatabasePath);
        var testRoot = Path.Combine(Path.GetTempPath(), "d365fo-testrun");

        Assert.StartsWith(testRoot, db, StringComparison.OrdinalIgnoreCase);
    }
}
