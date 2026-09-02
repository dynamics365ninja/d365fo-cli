using System.Text;
using D365FO.Core.Guardrails;

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

        InsertSorted(lines, key + "=" + Sanitise(value), key);
        AtomicWrite(path, lines);
        return new WriteResult(path, WriteOutcome.Created, key, null, value);
    }

    /// <summary>
    /// Replace the text of an entry that must ALREADY exist.
    /// </summary>
    /// <remarks>
    /// Not <see cref="CreateOrUpdate"/> with <c>overwrite</c>: that writes a new entry when the
    /// key is absent, which turns a mistyped key in a correction into a second label rather than
    /// a fix. Correcting a translation and creating one are different intents, and only one of
    /// them should be able to invent a key.
    /// </remarks>
    public static WriteResult Update(string path, string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Contains('=')) throw new ArgumentException("Label keys must not contain '='.", nameof(key));

        if (!File.Exists(path))
            return new WriteResult(path, WriteOutcome.KeyMissing, key, null, null);

        var lines = File.ReadAllLines(path).ToList();
        var existing = FindKey(lines, key);
        if (existing.lineIdx < 0)
            return new WriteResult(path, WriteOutcome.KeyMissing, key, null, null);

        var prev = existing.existingValue;
        lines[existing.lineIdx] = key + "=" + Sanitise(value);
        AtomicWrite(path, lines);
        return new WriteResult(path, WriteOutcome.Updated, key, prev, value);
    }

    /// <summary>
    /// Insert a new entry at its case-insensitive ORDINAL position among the
    /// existing key lines (upstream parity: culture-aware sorts put e.g.
    /// "Item_2" before "Item10" differently per OS locale, producing noisy
    /// diffs). Comment and blank lines keep their position relative to the
    /// entry that follows them.
    /// </summary>
    private static void InsertSorted(List<string> lines, string newLine, string newKey)
    {
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith(';')) continue;
            var key = line[..eq];
            if (StringComparer.OrdinalIgnoreCase.Compare(newKey, key) < 0)
            {
                // Skip back over the comment block attached to this entry.
                int insertAt = i;
                while (insertAt > 0 &&
                       (lines[insertAt - 1].StartsWith(';') || lines[insertAt - 1].Trim().Length == 0))
                    insertAt--;
                lines.Insert(insertAt, newLine);
                return;
            }
        }
        lines.Add(newLine);
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

    /// <summary>
    /// True when a label file ID (or a <c>*.label.txt</c> path) refers to a
    /// label file EXTENSION rather than an original (base) label file owned by
    /// the model. D365FO names label file extensions with an <c>_Extension</c>
    /// marker, optionally followed by a model prefix — e.g.
    /// <c>Base_Extension</c> or <c>Base_ExtensionContoso</c>. New labels must
    /// always go into the model's own ORIGINAL label file: an extension only
    /// extends a base file owned by another model, and writing there is what
    /// leads clients to wrongly prefix label IDs.
    /// </summary>
    public static bool IsExtensionLabelFile(string labelFileIdOrPath)
    {
        if (string.IsNullOrWhiteSpace(labelFileIdOrPath)) return false;
        var name = Path.GetFileName(labelFileIdOrPath);
        // Strip the `.<lang>.label.txt` tail when a physical path was given.
        var dot = name.IndexOf('.');
        var fileId = dot > 0 ? name[..dot] : name;
        return fileId.Contains("_Extension", StringComparison.OrdinalIgnoreCase);
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
        var fullPath = Path.GetFullPath(path);

        // Prevent directory traversal: output must stay within packages or workspace.
        var cfg = D365FoSettings.FromEnvironment();
        PathGuard.EnsureWithinBoundary(fullPath, new[] { cfg.PackagesPath, cfg.WorkspacePath }.Concat(cfg.CustomPackagesPaths).ToArray());

        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = fullPath + ".tmp";
        // UTF-8 with BOM matches what Visual Studio writes for .label.txt files.
        using (var writer = new StreamWriter(tmp, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)))
        {
            foreach (var line in lines)
                writer.WriteLine(line);
        }
        if (File.Exists(fullPath))
        {
            var bak = fullPath + ".bak";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(fullPath, bak);
        }
        File.Move(tmp, fullPath);
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
