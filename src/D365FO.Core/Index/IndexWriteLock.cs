using System.Security.Cryptography;
using System.Text;

namespace D365FO.Core.Index;

/// <summary>
/// A cross-process lock, one per index database file, held by the writers that replace a
/// model's rows: <c>index extract</c>, <c>index refresh</c>, <c>index sync</c> and the MCP
/// <c>index_sync</c> tool.
/// </summary>
/// <remarks>
/// <para>
/// SQLite already serialises writers, but only for as long as its busy timeout. A refresh
/// writes a large model in one transaction that can outlast that, so a second writer on the
/// same file (Claude Desktop's MCP server re-indexing a model while a shell runs
/// <c>index refresh</c>) failed half way with "database is locked". Waiting on this lock instead
/// turns that into either a clean wait or an <see cref="D365FoErrorCodes.IndexBusy"/> refusal
/// before anything was written.
/// </para>
/// <para>
/// A named <see cref="Mutex"/> rather than a lock file: the OS releases it when the owning
/// process dies, so a crashed refresh cannot leave the index locked for everyone after it.
/// A mutex is owned by a thread — acquire and dispose on the same one, which every caller
/// does because index writes are synchronous. It is also re-entrant on that thread.
/// </para>
/// </remarks>
public sealed class IndexWriteLock : IDisposable
{
    /// <summary>
    /// How long a writer waits for another to finish before refusing. Long enough to ride out
    /// an <c>index sync</c> of a custom model, short enough that a call waiting behind a
    /// full refresh comes back with a reason instead of hanging. Settable for tests.
    /// </summary>
    internal static TimeSpan WaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private readonly Mutex _mutex;
    private bool _released;

    private IndexWriteLock(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// The OS name of the lock for <paramref name="databasePath"/>. Derived from the full path
    /// (case-folded on Windows, where the file system is), so two spellings of one file share a
    /// lock and two files never do. Hashed because a path is not a legal mutex name.
    /// The scope comes from <see cref="Scope"/>, not a <c>Local\</c> prefix.
    /// </summary>
    public static string MutexNameFor(string databasePath)
    {
        var full = Path.GetFullPath(databasePath);
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..32];
        return "d365fo-index-" + hash;
    }

    /// <summary>
    /// Always limited to the current user. On Windows also to the logon session, where every
    /// process sharing one user's index runs. Not on Unix: there a "session" is the shell's
    /// (setsid) session, so a session-local mutex would not be shared between an MCP server
    /// launched by an editor and a refresh run from a terminal — exactly the pair this lock is for.
    /// </summary>
    internal static NamedWaitHandleOptions Scope => new()
    {
        CurrentUserOnly = true,
        CurrentSessionOnly = OperatingSystem.IsWindows(),
    };

    /// <summary>
    /// Take the write lock for <paramref name="databasePath"/>, waiting up to
    /// <see cref="WaitTimeout"/>. Returns null when another writer still holds it.
    /// </summary>
    public static IndexWriteLock? TryAcquire(string databasePath) => TryAcquire(databasePath, WaitTimeout);

    /// <inheritdoc cref="TryAcquire(string)"/>
    public static IndexWriteLock? TryAcquire(string databasePath, TimeSpan timeout)
    {
        var mutex = new Mutex(initiallyOwned: false, MutexNameFor(databasePath), Scope);
        bool acquired;
        try
        {
            acquired = mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // The previous holder died mid-write. SQLite rolled its transaction back, so the
            // index is consistent and the lock is now ours.
            acquired = true;
        }

        if (acquired) return new IndexWriteLock(mutex);
        mutex.Dispose();
        return null;
    }

    /// <summary>The refusal a writer returns when <see cref="TryAcquire(string)"/> gave up.</summary>
    public static ToolResult<object> Busy(string databasePath) =>
        ToolResult<object>.Fail(D365FoErrorCodes.IndexBusy,
            $"Another process is writing the index at {Path.GetFullPath(databasePath)} "
            + $"(waited {WaitTimeout.TotalSeconds:0} s). Nothing was written.",
            "An `index refresh` / `index extract` / `index sync` or an MCP `index_sync` is running against "
            + "the same database. Retry once it finishes.");

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
