using D365FO.Core.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// <see cref="SqlitePool.ReleaseFor"/> has to cover every connection-string spelling this
/// codebase opens, because a pool is keyed by the string and not by the file.
/// </summary>
/// <remarks>
/// Miss one and the temp database stays locked, so <c>Dispose</c> throws
/// <c>IOException: being used by another process</c> — which is exactly what happened when the
/// helper knew the two <c>MetadataRepository</c> spellings but not the bare
/// <c>Data Source={path}</c> one a test uses to seed with raw SQL. Reaching back for
/// <c>ClearAllPools</c> is not the fix: that call is process-wide and disposes connections other
/// tests are mid-query on, which is the flake this helper exists to remove.
/// </remarks>
public class SqlitePoolTests
{
    [Fact]
    public void Release_unlocks_the_file_for_every_spelling_the_codebase_opens()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-pool-{Guid.NewGuid():N}.sqlite");

        // 1. The repository's own two connection strings.
        var repo = new MetadataRepository(dbPath);
        repo.EnsureSchema();
        repo.UpsertModel("Fleet", null, null, true);
        _ = repo.GetDependencyGraph();

        // 2. The bare spelling a test uses when it seeds the database directly.
        using (var raw = new SqliteConnection($"Data Source={dbPath}"))
        {
            raw.Open();
            using var cmd = raw.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Models;";
            cmd.ExecuteScalar();
        }

        SqlitePool.ReleaseFor(dbPath);

        // The point of the helper: the file is deletable straight away, with no retry loop.
        var exception = Record.Exception(() =>
        {
            foreach (var ext in new[] { "", "-wal", "-shm" })
            {
                var p = dbPath + ext;
                if (File.Exists(p)) File.Delete(p);
            }
        });

        Assert.Null(exception);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void Release_is_harmless_for_a_path_that_was_never_opened()
    {
        var never = Path.Combine(Path.GetTempPath(), $"d365fo-pool-never-{Guid.NewGuid():N}.sqlite");
        Assert.Null(Record.Exception(() => SqlitePool.ReleaseFor(never)));
        Assert.Null(Record.Exception(() => SqlitePool.ReleaseFor("")));
    }
}
