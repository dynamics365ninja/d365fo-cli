using System.Collections.Concurrent;

namespace D365FO.Mcp;

/// <summary>
/// Duplicate-call dedup cache (agentic-loop mitigation) — port of the upstream
/// MCP server's <c>callDedup</c> module.
///
/// A model stuck in a loop re-issues the same read call with identical
/// arguments. Read tools are served from a short-TTL cache on repeat — the
/// model gets the identical answer instantly (with a note) instead of
/// re-running DB queries. Write/stateful tools are excluded: repeated identical
/// calls are legitimate there (write retries after fixes, status polling).
/// </summary>
public static class CallDedup
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public const int MaxEntries = 200;

    /// <summary>Tools whose repeated identical calls are legitimate — never dedup, never loop-hint.</summary>
    public static readonly HashSet<string> ExcludedTools = new(StringComparer.Ordinal)
    {
        // Write tools are already excluded via ToolCatalog.WriteTools (and flush
        // the cache, see InvalidateAfter); these are stateful reads whose answer can
        // change between identical calls (index freshness, workspace config).
        "index_status", "get_workspace_info", "index_history",
        // `prepare` issues a fresh provenance token on every call — never dedup.
        "prepare",
    };

    private sealed record Entry(string Body, bool IsError, DateTimeOffset At);

    private static readonly ConcurrentDictionary<string, Entry> Cache = new();

    public static string Key(string toolName, string argsJson) => toolName + "|" + argsJson;

    /// <summary>Returns the cached body (with a duplicate-call note) on a repeat read call.</summary>
    public static (string Body, bool IsError)? TryGet(string key)
    {
        if (!Cache.TryGetValue(key, out var entry)) return null;
        if (DateTimeOffset.UtcNow - entry.At > Ttl)
        {
            Cache.TryRemove(key, out _);
            return null;
        }
        return (entry.Body, entry.IsError);
    }

    private static long s_generation;

    /// <summary>
    /// Bumped by every <see cref="InvalidateAfter"/> flush. A read captures it before it runs
    /// and passes it to <see cref="Store"/>, so an answer computed before a concurrent write
    /// (another daemon or HTTP connection) is not cached after that write's flush.
    /// </summary>
    public static long Generation => Interlocked.Read(ref s_generation);

    /// <param name="observedGeneration">
    /// <see cref="Generation"/> captured before the read ran. When a write has flushed the
    /// cache since, the result is not cached.
    /// </param>
    public static void Store(string key, string body, bool isError, long? observedGeneration = null)
    {
        if (observedGeneration is { } seen && seen != Generation) return;
        if (Cache.Count >= MaxEntries)
        {
            // Drop the oldest ~25% — cheap pressure valve, exactness not needed.
            foreach (var stale in Cache.OrderBy(kv => kv.Value.At).Take(MaxEntries / 4).ToList())
                Cache.TryRemove(stale.Key, out _);
        }
        var entry = new Entry(body, isError, DateTimeOffset.UtcNow);
        Cache[key] = entry;
        // A flush may have landed between the check above and the write: the generation is
        // bumped before the clear, so re-checking here catches it and withdraws this entry.
        if (observedGeneration is { } again && again != Generation)
            Cache.TryRemove(new KeyValuePair<string, Entry>(key, entry));
    }

    /// <summary>Note appended to a deduped response so the agent notices the loop.</summary>
    public const string LoopHint =
        "\n\n[dedup] Identical call within 60 s — served from cache. " +
        "You already have this answer; use it instead of re-asking. " +
        "If you need different data, change the arguments.";

    /// <summary>
    /// Drop every cached answer after a call to a tool in <see cref="ToolCatalog.WriteTools"/>.
    /// The cache is keyed on the call, not on what the call read, so there is no narrower
    /// invalidation that is still correct: <c>modify_object</c> on a table changes what
    /// <c>get_object_info</c>, <c>search</c> and <c>find_references</c> would answer. It runs
    /// whether or not the write reported success, because a refused batch or a build that
    /// failed half way can still have changed disk or the index. One process-wide cache, so a
    /// write on one daemon or HTTP connection flushes the reads of every other one too — they
    /// share the index the write just changed.
    /// </summary>
    /// <returns>True when <paramref name="toolName"/> is a write tool and the cache was cleared.</returns>
    public static bool InvalidateAfter(string toolName)
    {
        if (!ToolCatalog.WriteTools.Contains(toolName)) return false;
        Interlocked.Increment(ref s_generation);
        Cache.Clear();
        return true;
    }

    /// <summary>Test hook.</summary>
    public static void Clear() => Cache.Clear();
}
