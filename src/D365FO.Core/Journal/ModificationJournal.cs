using System.Text.Json;
using System.Text.Json.Serialization;

namespace D365FO.Core.Journal;

/// <summary>
/// File-backed FIFO journal of reversible writes, stored one JSON file per entry under
/// <c>&lt;index-dir&gt;/journal/</c> (next to the SQLite index — see <see cref="ForIndex"/>).
/// Filenames embed the UTC timestamp (<c>Ticks</c>, zero-padded) so lexical directory order is
/// chronological order: the last file is the top of the undo stack. Size-capped — <see cref="Append"/>
/// prunes the oldest entries (by filename order) once the directory exceeds <see cref="MaxBytes"/>
/// or <see cref="MaxEntries"/>, mirroring <c>ProvenanceStore</c>'s file-per-record convention.
/// </summary>
public sealed class ModificationJournal
{
    /// <summary>Default cap: keep the journal directory under ~50 MB.</summary>
    public const long DefaultMaxBytes = 50_000_000;

    /// <summary>Default cap: keep at most this many entries regardless of size.</summary>
    public const int DefaultMaxEntries = 500;

    private static readonly JsonSerializerOptions StorageOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    public string JournalDirectory { get; }
    public long MaxBytes { get; }
    public int MaxEntries { get; }

    public ModificationJournal(string journalDirectory, long maxBytes = DefaultMaxBytes, int maxEntries = DefaultMaxEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalDirectory);
        JournalDirectory = journalDirectory;
        MaxBytes = maxBytes;
        MaxEntries = maxEntries;
    }

    /// <summary>
    /// Resolve the journal directory next to the active SQLite index
    /// (<c>&lt;dirname(DatabasePath)&gt;/journal</c>), honouring the same
    /// <c>--db</c>/<c>D365FO_INDEX_DB</c> override chain as <c>RepoFactory</c>-style
    /// callers. Size/entry caps are overridable via <c>D365FO_JOURNAL_MAX_BYTES</c> /
    /// <c>D365FO_JOURNAL_MAX_ENTRIES</c> for tests and unusually write-heavy sessions.
    /// </summary>
    public static ModificationJournal ForIndex(string? databasePathOverride = null)
    {
        var settings = D365FoSettings.FromEnvironment(databasePathOverride);
        var dbDir = Path.GetDirectoryName(Path.GetFullPath(settings.DatabasePath));
        var journalDir = Path.Combine(string.IsNullOrEmpty(dbDir) ? "." : dbDir, "journal");

        var maxBytes = DefaultMaxBytes;
        var maxBytesRaw = D365FoSettings.Resolve("D365FO_JOURNAL_MAX_BYTES");
        if (!string.IsNullOrWhiteSpace(maxBytesRaw) && long.TryParse(maxBytesRaw, out var parsedBytes) && parsedBytes > 0)
            maxBytes = parsedBytes;

        var maxEntries = DefaultMaxEntries;
        var maxEntriesRaw = D365FoSettings.Resolve("D365FO_JOURNAL_MAX_ENTRIES");
        if (!string.IsNullOrWhiteSpace(maxEntriesRaw) && int.TryParse(maxEntriesRaw, out var parsedEntries) && parsedEntries > 0)
            maxEntries = parsedEntries;

        return new ModificationJournal(journalDir, maxBytes, maxEntries);
    }

    /// <summary>
    /// Append a new entry (assigning <see cref="JournalEntry.Id"/>/<see cref="JournalEntry.TimestampUtc"/>
    /// if not already set) and prune the oldest entries until the directory is back under both caps.
    /// Best-effort by design — callers wrap this in try/catch so a journal failure never blocks the
    /// underlying write it is trying to record.
    /// </summary>
    public JournalEntry Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Directory.CreateDirectory(JournalDirectory);

        var stamped = entry with
        {
            Id = string.IsNullOrWhiteSpace(entry.Id) ? Guid.NewGuid().ToString("N") : entry.Id,
            TimestampUtc = entry.TimestampUtc == default ? DateTimeOffset.UtcNow : entry.TimestampUtc,
        };

        var path = PathFor(stamped);
        var json = JsonSerializer.Serialize(stamped, StorageOptions);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);

        Prune();
        return stamped;
    }

    /// <summary>
    /// List entries, most-recent-first (top of the undo stack first). Corrupt/unreadable entry
    /// files are silently skipped — a single bad file must not make the whole journal unusable.
    /// </summary>
    public IReadOnlyList<JournalEntry> List(int? limit = null)
    {
        if (!Directory.Exists(JournalDirectory)) return Array.Empty<JournalEntry>();

        var files = Directory.EnumerateFiles(JournalDirectory, "*.json")
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .ToList();

        var results = new List<JournalEntry>(limit ?? files.Count);
        foreach (var file in files)
        {
            if (limit.HasValue && results.Count >= limit.Value) break;
            var entry = TryRead(file);
            if (entry is not null) results.Add(entry);
        }
        return results;
    }

    /// <summary>The most recent entry, or null when the journal is empty.</summary>
    public JournalEntry? Peek() => List(1).FirstOrDefault();

    /// <summary>Remove an entry by id (called after a successful undo). No-op if already gone.</summary>
    public bool Remove(string id)
    {
        if (!Directory.Exists(JournalDirectory)) return false;
        foreach (var file in Directory.EnumerateFiles(JournalDirectory, "*.json"))
        {
            var entry = TryRead(file);
            if (entry is not null && string.Equals(entry.Id, id, StringComparison.Ordinal))
            {
                try { File.Delete(file); return true; }
                catch { return false; }
            }
        }
        return false;
    }

    /// <summary>Total entry count currently on disk.</summary>
    public int Count() => Directory.Exists(JournalDirectory)
        ? Directory.EnumerateFiles(JournalDirectory, "*.json").Count()
        : 0;

    private string PathFor(JournalEntry entry)
        => Path.Combine(JournalDirectory, $"{entry.TimestampUtc.UtcTicks:D19}_{entry.Id}.json");

    private static JournalEntry? TryRead(string file)
    {
        try
        {
            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<JournalEntry>(json, StorageOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>FIFO prune: delete the oldest files (ascending filename order) until under both caps.</summary>
    private void Prune()
    {
        try
        {
            var files = Directory.EnumerateFiles(JournalDirectory, "*.json")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            long total = files.Sum(f => f.Length);
            int count = files.Count;
            int i = 0;
            while (i < files.Count && (total > MaxBytes || count > MaxEntries))
            {
                var f = files[i];
                try
                {
                    total -= f.Length;
                    count--;
                    f.Delete();
                }
                catch { /* best-effort */ }
                i++;
            }
        }
        catch { /* best-effort: pruning must never break Append */ }
    }
}
