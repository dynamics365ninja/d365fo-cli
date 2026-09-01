using Microsoft.Data.Sqlite;

namespace D365FO.Core.Tests;

/// <summary>
/// Releases the pooled SQLite handles for ONE database file, so a test can delete its temp
/// database without disturbing any other test's connections.
/// </summary>
/// <remarks>
/// <para>
/// Twenty test classes used to call <c>SqliteConnection.ClearAllPools()</c> in <c>Dispose</c>
/// to let the temp file be deleted. That call is process-wide: it disposes every pooled
/// connection in the process, including one another test is in the middle of a query on. xUnit
/// runs collections in parallel, so the loser came back as
/// <c>ObjectDisposedException: Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'</c>
/// from somewhere entirely unrelated to the test that was tearing down — observed on
/// <c>MethodSourceFtsTests</c>, roughly one full-suite run in ten, and never reproducible when
/// that assembly ran on its own because the timing windows were too narrow.
/// </para>
/// <para>
/// <c>ClearPool</c> is scoped to one connection string, which is what was wanted all along.
/// <c>MetadataRepository</c> opens two per database — read-write and read-only — so both are
/// cleared, and the path is cleared as given and as a full path, since a pool is keyed by the
/// connection string text rather than by the file it resolves to.
/// </para>
/// </remarks>
internal static class SqlitePool
{
    public static void ReleaseFor(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) return;

        foreach (var connectionString in PoolKeysFor(databasePath))
        {
            try
            {
                using var connection = new SqliteConnection(connectionString);
                SqliteConnection.ClearPool(connection);
            }
            catch
            {
                // Releasing a pool that was never created is not a failure worth reporting.
            }
        }
    }

    /// <summary>
    /// Every connection string this codebase opens against one database file. A pool is keyed by
    /// the connection string TEXT, not by the file it resolves to, so each distinct spelling is
    /// its own pool and has to be cleared on its own.
    /// </summary>
    /// <remarks>
    /// The bare <c>Data Source={path}</c> form is not hypothetical: tests that seed a database
    /// with raw SQL open it that way, and missing it left the file locked and the delete in
    /// <c>Dispose</c> throwing on every single run. If a fourth spelling ever appears, the
    /// symptom is that same IOException — add it here rather than reaching back for
    /// <c>ClearAllPools</c>.
    /// </remarks>
    private static IEnumerable<string> PoolKeysFor(string databasePath)
    {
        foreach (var source in DistinctSources(databasePath))
        {
            // What tests use when they open the file directly to seed it.
            yield return $"Data Source={source}";

            // What MetadataRepository opens: read-write and read-only, private cache.
            foreach (var mode in new[] { SqliteOpenMode.ReadWriteCreate, SqliteOpenMode.ReadOnly })
            {
                yield return new SqliteConnectionStringBuilder
                {
                    DataSource = source,
                    Mode = mode,
                    Cache = SqliteCacheMode.Private,
                    Pooling = true,
                }.ToString();
            }
        }
    }

    private static IEnumerable<string> DistinctSources(string databasePath)
    {
        yield return databasePath;

        string? full = null;
        try { full = Path.GetFullPath(databasePath); }
        catch { /* an unusable path has no pool to clear */ }

        if (full is not null && !string.Equals(full, databasePath, StringComparison.Ordinal))
            yield return full;
    }
}
