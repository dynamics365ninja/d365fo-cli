// <copyright file="LabelDiskCheck.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.RegularExpressions;

namespace D365FO.Core.Index;

/// <summary>
/// Does an indexed label actually exist in its <c>.label.txt</c> on disk? Port of the upstream
/// MCP server's <c>labelDiskCheck.ts</c>.
///
/// The symbol index is a snapshot. When it is ahead of the file system — a label row from a
/// run that was rolled back, a model rebuilt outside this tool, an index written before a git
/// checkout — label search answers with a reusable-looking hit for a label that is in no file.
/// Downstream, the caller reuses that "existing" label in XML and the failure only surfaces at
/// build time as <c>BPErrorUnknownLabel</c> (upstream observed a benchmark run take all three
/// labels it needed from phantom rows; xppc does not check labels, so the build passed and the
/// BP check found them two steps later, costing a second build and a second BP run).
///
/// The check is deliberately one-way. A label the file HAS is never questioned, and any doubt
/// — unreadable path, no indexed path, oversized file, budget exhausted — reports <c>null</c>
/// ("could not verify") rather than a verdict. Only "the file reads fine and this id is not in
/// it" is worth telling the caller about, because that one is always a real defect in what
/// they were about to build on.
/// </summary>
public static class LabelDiskCheck
{
    /// <summary>Above this, don't pay the read: shipped Microsoft label files are the only ones near it.</summary>
    public const long MaxLabelFileBytes = 16 * 1024 * 1024;

    /// <summary>
    /// Total bytes one check may read before giving up with "no verdict". A label file id has
    /// ~74 language variants and the platform's own are ~10 MB each — upstream measured an
    /// unbounded sweep at 17 s, and 218 ms with the budget. The budget rather than a
    /// "skip Microsoft models" rule, because an unrecognised custom model reads as Microsoft's
    /// and the check would switch itself off precisely where a stale row is most likely.
    /// </summary>
    public const long MaxTotalReadBytes = 32 * 1024 * 1024;

    /// <summary>
    /// A label file line is <c>LabelId=Text</c>, with <c>;</c>-prefixed comment lines. Only
    /// the id half matters here. The BOM is in the class because every shipped
    /// <c>.label.txt</c> starts with one — without it the FIRST label of every file would be
    /// unfindable, i.e. reported as missing.
    /// </summary>
    public static bool FileDeclaresLabel(string content, string labelId)
        => Regex.IsMatch(content,
            $@"^[\uFEFF \t]*{Regex.Escape(labelId)}[ \t]*=",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);

    /// <summary>
    /// Verdicts for several ids at once, reading each file ONCE instead of once per id (search
    /// checks every candidate row it is about to show, and those rows share a label file).
    /// Every requested id gets an entry: <c>true</c> missing, <c>false</c> present,
    /// <c>null</c> no verdict. Present in ANY language file is present — the check is about
    /// existence, not translation completeness.
    /// </summary>
    /// <param name="bytesRead">
    /// Shared read budget across a whole search/resolve call — pass the running total in and
    /// carry it between per-file groups so ten files cannot each spend the full budget.
    /// </param>
    public static IReadOnlyDictionary<string, bool?> LabelsMissingOnDisk(
        IReadOnlyCollection<string> labelIds,
        IReadOnlyList<string> filePaths,
        ref long bytesRead)
    {
        var verdicts = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        var pending = new HashSet<string>(labelIds, StringComparer.OrdinalIgnoreCase);
        if (pending.Count == 0) return verdicts;

        bool readAny = false;
        bool overBudget = false;

        foreach (var filePath in filePaths)
        {
            if (pending.Count == 0) break;
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length > MaxLabelFileBytes) continue;
                // Over budget before every id has an answer: stop and say nothing about
                // the rest. Reporting "missing" off a partial sweep would be a verdict the
                // files never gave.
                if (bytesRead + info.Length > MaxTotalReadBytes)
                {
                    overBudget = true;
                    break;
                }
                var content = File.ReadAllText(filePath);
                bytesRead += info.Length;
                readAny = true;

                foreach (var labelId in pending.ToList())
                {
                    if (FileDeclaresLabel(content, labelId))
                    {
                        verdicts[labelId] = false;
                        pending.Remove(labelId);
                    }
                }
            }
            catch
            {
                // Missing or unreadable file: no verdict from this path.
            }
        }

        foreach (var labelId in pending)
        {
            verdicts[labelId] = overBudget || !readAny ? null : true;
        }
        return verdicts;
    }

    /// <summary>
    /// Verify a set of search/resolve hits against disk: per (File, Key), <c>true</c> = the
    /// index has it and no <c>.label.txt</c> of that file does (stale row — do not reuse),
    /// <c>false</c> = confirmed on disk, <c>null</c> = could not verify. One shared read
    /// budget across the whole call.
    /// </summary>
    /// <summary>
    /// Convenience over <see cref="VerifyMatches"/> for the search/resolve surfaces: the
    /// phantom tokens (<c>@File:Key</c> rows the index has and no file declares) plus the
    /// caller-facing warning, or an empty set and null when everything checks out or nothing
    /// could be verified.
    /// </summary>
    public static (HashSet<string> Phantoms, List<string>? Warnings) Annotate(
        MetadataRepository repo, IReadOnlyList<LabelMatch> matches)
    {
        var verdicts = VerifyMatches(matches, repo.GetLabelFilePaths);
        var phantoms = matches
            .Where(m => verdicts.TryGetValue((m.File, m.Key), out var v) && v == true)
            .Select(m => $"@{m.File}:{m.Key}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = phantoms.Count == 0 ? null : new List<string>
        {
            $"{phantoms.Count} hit(s) exist in the index but in NO .label.txt on disk (stale rows — a rolled-back " +
            "session or an out-of-band change): " + string.Join(", ", phantoms.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) +
            ". Do NOT reuse them — xppc does not check labels, so the build passes and BP fails later with " +
            "BPErrorUnknownLabel. Run `d365fo index refresh`.",
        };
        return (phantoms, warnings);
    }

    public static IReadOnlyDictionary<(string File, string Key), bool?> VerifyMatches(
        IEnumerable<LabelMatch> matches,
        Func<string, IReadOnlyList<string>> pathsForFile)
    {
        var result = new Dictionary<(string, string), bool?>();
        long bytesRead = 0;
        foreach (var group in matches.GroupBy(m => m.File, StringComparer.OrdinalIgnoreCase))
        {
            IReadOnlyList<string> paths;
            try { paths = pathsForFile(group.Key); }
            catch { paths = Array.Empty<string>(); }

            var keys = group.Select(m => m.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var verdicts = LabelsMissingOnDisk(keys, paths, ref bytesRead);
            foreach (var key in keys)
            {
                result[(group.Key, key)] = verdicts.TryGetValue(key, out var v) ? v : null;
            }
        }
        return result;
    }
}
