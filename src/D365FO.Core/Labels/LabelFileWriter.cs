using System.Text;

namespace D365FO.Core.Labels;

/// <summary>
/// Atomic read/write for D365FO <c>*.label.txt</c> resource files. The on-disk
/// format is a flat key=value list with optional <c>;</c>-prefixed comments,
/// BOM-aware UTF-8. The writer preserves comment lines and blank separators so
/// hand-maintained translators' notes survive a create/rename round-trip.
/// </summary>
/// <remarks>
/// Thread-safety: callers must serialise writes to the same path. The writer
/// itself uses tmp+move so partial files never end up on disk, but it does
/// not take an exclusive OS-level lock.
/// </remarks>
public static class LabelFileWriter
{
    /// <summary>
    /// Create or update a single <c>KEY=VALUE</c> entry. When the file does
    /// not exist it is created. When the key already exists and
    /// <paramref name="overwrite"/> is <c>false</c>, the method returns a
    /// <see cref="WriteOutcome.KeyExists"/> outcome without touching the file.
    /// </summary>
    public static WriteResult CreateOrUpdate(string path, string key, string value, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('=')) throw new ArgumentException("Label keys must not contain '='.", nameof(key));

        var lines = File.Exists(path)
            ? File.ReadAllLines(path).ToList()
            : new List<string>();

        var existing = FindKey(lines, key);
        if (existing.lineIdx >= 0)
        {
            if (!overwrite)
                return new WriteResult(path, WriteOutcome.KeyExists, key, existing.existingValue, null);

            var prev = existing.existingValue;
            lines[existing.lineIdx] = key + "=" + Sanitise(value);
            AtomicWrite(path, lines);
            return new WriteResult(path, WriteOutcome.Updated, key, prev, value);
        }

        lines.Add(key + "=" + Sanitise(value));
        AtomicWrite(path, lines);
        return new WriteResult(path, WriteOutcome.Created, key, null, value);
    }

    /// <summary>
    /// Rename a key in place, preserving its value. Returns
    /// <see cref="WriteOutcome.KeyMissing"/> when the source key is absent,
    /// and <see cref="WriteOutcome.KeyExists"/> when the target key already
    /// exists (unless <paramref name="overwrite"/> is true).
    /// </summary>
    public static WriteResult Rename(string path, string oldKey, string newKey, bool overwrite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(oldKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newKey);
        if (newKey.Contains('=')) throw new ArgumentException("Label keys must not contain '='.", nameof(newKey));
        if (!File.Exists(path))
            return new WriteResult(path, WriteOutcome.FileMissing, oldKey, null, null);

        var lines = File.ReadAllLines(path).ToList();
        var source = FindKey(lines, oldKey);
        if (source.lineIdx < 0)
            return new WriteResult(path, WriteOutcome.KeyMissing, oldKey, null, null);

        if (string.Equals(oldKey, newKey, StringComparison.Ordinal))
            return new WriteResult(path, WriteOutcome.NoChange, oldKey, source.existingValue, source.existingValue);

        var target = FindKey(lines, newKey);
        if (target.lineIdx >= 0 && !overwrite)
            return new WriteResult(path, WriteOutcome.KeyExists, newKey, target.existingValue, null);

        if (target.lineIdx >= 0)
        {
            // Overwriting: drop the old occurrence first.
            lines.RemoveAt(target.lineIdx);
            if (target.lineIdx < source.lineIdx) source.lineIdx--;
        }

        lines[source.lineIdx] = newKey + "=" + Sanitise(source.existingValue ?? "");
        AtomicWrite(path, lines);
        return new WriteResult(path, WriteOutcome.Renamed, newKey, source.existingValue, source.existingValue);
    }

    /// <summary>Delete a key. Returns <see cref="WriteOutcome.KeyMissing"/> when absent.</summary>
    public static WriteResult Delete(string path, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!File.Exists(path))
            return new WriteResult(path, WriteOutcome.FileMissing, key, null, null);
        var lines = File.ReadAllLines(path).ToList();
        var existing = FindKey(lines, key);
        if (existing.lineIdx < 0)
            return new WriteResult(path, WriteOutcome.KeyMissing, key, null, null);
        lines.RemoveAt(existing.lineIdx);
        AtomicWrite(path, lines);
        return new WriteResult(path, WriteOutcome.Deleted, key, existing.existingValue, null);
    }

    // ---- internals ----------------------------------------------------

    private static (int lineIdx, string? existingValue) FindKey(List<string> lines, string key)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(';')) continue;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;
            var k = trimmed.Substring(0, eq).TrimEnd();
            if (string.Equals(k, key, StringComparison.Ordinal))
                return (i, trimmed.Substring(eq + 1));
        }
        return (-1, null);
    }

    private static string Sanitise(string value)
    {
        // Label files are line-oriented. D365FO does not support multi-line
        // values; CR/LF are escaped as explicit XPP sequences.
        return value.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
    }

    private static void AtomicWrite(string path, IReadOnlyList<string> lines)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        // UTF-8 with BOM matches what Visual Studio writes for .label.txt files.
        using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            foreach (var line in lines)
                writer.WriteLine(line);
        }
        if (File.Exists(path))
        {
            var bak = path + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(path, bak);
        }
        File.Move(tmp, path);
    }
}

public enum WriteOutcome
{
    Created,
    Updated,
    Renamed,
    Deleted,
    NoChange,
    KeyExists,
    KeyMissing,
    FileMissing,
}

public sealed record WriteResult(
    string Path,
    WriteOutcome Outcome,
    string Key,
    string? OldValue,
    string? NewValue);
