using D365FO.Core.Index;
using Microsoft.Data.Sqlite;
using Dapper;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Two writers on one index — the Claude Desktop MCP server and a shell, or two shells. Before
/// this, write connections had no busy timeout and the extract pipeline's
/// <c>wal_autocheckpoint = 0</c> was set on every connection (and survived in the pool), and
/// nothing serialised a refresh against an <c>index_sync</c> of the same file.
/// </summary>
/// <remarks>
/// In the environment collection: <see cref="IndexWriteLock.WaitTimeout"/> is process-wide and
/// one test shortens it.
/// </remarks>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class IndexConcurrencyTests : IDisposable
{
    private static readonly string MiniAot =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-concurrency-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            try { if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext); } catch { }
        }
    }

    private MetadataRepository Repo()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        return repo;
    }

    [Fact]
    public void Every_connection_waits_on_a_lock_instead_of_failing_at_once()
    {
        var repo = Repo();

        using var write = repo.Open();
        Assert.Equal(MetadataRepository.BusyTimeoutMs, write.ExecuteScalar<long>("PRAGMA busy_timeout"));

        using var bulk = repo.Open(bulk: true);
        Assert.Equal(MetadataRepository.BusyTimeoutMs, bulk.ExecuteScalar<long>("PRAGMA busy_timeout"));
    }

    [Fact]
    public void Automatic_checkpoint_is_off_only_on_the_bulk_extract_connection()
    {
        var repo = Repo();

        using (var bulk = repo.Open(bulk: true))
            Assert.Equal(0, bulk.ExecuteScalar<long>("PRAGMA wal_autocheckpoint"));

        // Pooled: the next ordinary connection is very likely the same handle the bulk one used,
        // so this also proves the setting does not leak through the pool.
        using var ordinary = repo.Open();
        Assert.True(ordinary.ExecuteScalar<long>("PRAGMA wal_autocheckpoint") > 0);
    }

    [Fact]
    public void A_write_waits_out_another_connection_s_write_transaction_instead_of_database_is_locked()
    {
        var repo = Repo();

        // A second process's writer, holding the write lock for longer than an unguarded
        // writer would wait.
        using var other = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath, Pooling = false,
        }.ToString());
        other.Open();
        using var tx = other.BeginTransaction();
        other.Execute("INSERT INTO ExtractionRuns(StartedUtc, Model, ElapsedMs, Tables, Classes, Edts, Enums, Labels, IsCustom) "
            + "VALUES('2026-01-01T00:00:00Z', 'Holder', 0, 0, 0, 0, 0, 0, 0)", transaction: tx);

        var release = new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(750));
            tx.Commit();
        });
        release.Start();

        var ex = Record.Exception(() =>
            repo.RecordExtractionRun("Waiter", DateTime.UtcNow, 1, 0, 0, 0, 0, 0, isCustom: true));
        release.Join();

        Assert.Null(ex);
    }

    [Fact]
    public void Two_index_syncs_on_one_database_in_parallel_do_not_fail_with_database_is_locked()
    {
        Repo();
        var results = new ToolResult<object>[4];

        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
            results[i] = IndexSync.Sync("TestModel", path: null, packagesOverride: MiniAot, databasePath: _dbPath));

        foreach (var r in results)
            Assert.True(r.Ok, r.Error?.Message);
        Assert.Equal(results.Length, Repo().GetExtractionRuns(limit: 50, model: "TestModel").Count);
    }

    [Fact]
    public void Index_sync_behind_a_held_write_lock_is_refused_with_INDEX_BUSY_and_writes_nothing()
    {
        var repo = Repo();
        var previous = IndexWriteLock.WaitTimeout;
        IndexWriteLock.WaitTimeout = TimeSpan.FromMilliseconds(200);

        using var held = new ManualResetEventSlim();
        using var done = new ManualResetEventSlim();
        // A mutex belongs to the thread that took it, so the "other writer" needs its own thread.
        var holder = new Thread(() =>
        {
            using var writeLock = IndexWriteLock.TryAcquire(_dbPath, TimeSpan.FromSeconds(5));
            held.Set();
            done.Wait();
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(TimeSpan.FromSeconds(5)));

            var result = IndexSync.Sync("TestModel", path: null, packagesOverride: MiniAot, databasePath: _dbPath);

            Assert.False(result.Ok);
            Assert.Equal(D365FoErrorCodes.IndexBusy, result.Error!.Code);
            Assert.Contains("Retry", result.Error.Hint, StringComparison.Ordinal);
            Assert.Empty(repo.GetExtractionRuns(limit: 50, model: "TestModel"));
        }
        finally
        {
            done.Set();
            holder.Join();
            IndexWriteLock.WaitTimeout = previous;
        }
    }

    [Fact]
    public void Write_lock_is_user_scoped_and_not_shell_session_scoped_on_unix()
    {
        // On Unix a "session" is the shell's, so a session-local lock would not be shared by an
        // editor-launched MCP server and a terminal refresh.
        Assert.True(IndexWriteLock.Scope.CurrentUserOnly);
        Assert.Equal(OperatingSystem.IsWindows(), IndexWriteLock.Scope.CurrentSessionOnly);
        Assert.DoesNotContain('\\', IndexWriteLock.MutexNameFor(_dbPath));
    }

    [Fact]
    public void Write_lock_is_per_database_file()
    {
        var other = Path.Combine(Path.GetTempPath(), $"d365fo-concurrency-other-{Guid.NewGuid():N}.sqlite");

        Assert.Equal(IndexWriteLock.MutexNameFor(_dbPath),
            IndexWriteLock.MutexNameFor(Path.Combine(Path.GetDirectoryName(_dbPath)!, ".", Path.GetFileName(_dbPath))));
        Assert.NotEqual(IndexWriteLock.MutexNameFor(_dbPath), IndexWriteLock.MutexNameFor(other));

        // Holding one file's lock does not block another file's writer.
        using var mine = IndexWriteLock.TryAcquire(_dbPath, TimeSpan.Zero);
        Assert.NotNull(mine);
        var acquiredElsewhere = false;
        var otherThread = new Thread(() =>
        {
            using var theirs = IndexWriteLock.TryAcquire(other, TimeSpan.Zero);
            acquiredElsewhere = theirs is not null;
        });
        otherThread.Start();
        otherThread.Join();
        Assert.True(acquiredElsewhere);
    }
}
