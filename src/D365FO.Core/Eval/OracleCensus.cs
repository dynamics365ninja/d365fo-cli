using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using D365FO.Core.Metadata;

namespace D365FO.Core.Eval;

/// <summary>
/// Counts what the installation actually writes, so a rule can be built on evidence instead of
/// on what the documentation implies.
/// </summary>
/// <remarks>
/// <para>
/// This is the measurement that has to come before enforcement. A rule invented from a contract
/// and released without a census is a guess with an error severity attached: this repository
/// shipped one (a member-order rule) that looked right, passed a 150-file sample, and flagged
/// files Microsoft ships — an <c>AxEnum</c> with <c>ConfigurationKey</c> after
/// <c>UseEnumValue</c>, an <c>AccessGrant</c> with <c>Create</c> after <c>Update</c>. The census
/// is what answers "does shipped code really do this?" before anyone writes the rule.
/// </para>
/// <para>
/// It also answers the reverse, which is where catalog drift shows: a member the installation
/// writes and the contract does not declare means the contract is behind, not that the file is
/// wrong.
/// </para>
/// </remarks>
public static class OracleCensus
{
    /// <param name="Member">Child element name.</param>
    /// <param name="Files">How many documents carry it at least once.</param>
    /// <param name="Occurrences">How many times it appears in total.</param>
    /// <param name="Declared">Whether the metadata contract declares it.</param>
    /// <param name="SampleValues">Distinct leaf values seen, capped — empty for container members.</param>
    public sealed record MemberTally(
        string Member, int Files, int Occurrences, bool? Declared, IReadOnlyList<string> SampleValues);

    /// <param name="Root">The installation the census was taken over.</param>
    /// <param name="Element">The element the census was taken over, e.g. AxTable.</param>
    /// <param name="Contract">The metadata contract governing it, when one is known.</param>
    /// <param name="OrderIsStable">
    /// Whether every document writes the shared members in one consistent relative order. False
    /// means the order carries no rule — the fact that decided a rule not to write.
    /// </param>
    /// <param name="FilesScanned">Documents read.</param>
    /// <param name="ElapsedMs">Wall-clock time of the census.</param>
    /// <param name="OrderCounterExamples">Member pairs seen in both orders — the evidence behind an unstable order.</param>
    /// <param name="Members">One tally per child element, most-carried first.</param>
    /// <param name="DeclaredNeverSeen">Members the contract declares that no file uses.</param>
    /// <param name="SeenNotDeclared">Members files carry that the contract does not declare — catalog drift.</param>
    public sealed record Report(
        string Root,
        string Element,
        string? Contract,
        int FilesScanned,
        long ElapsedMs,
        bool OrderIsStable,
        IReadOnlyList<string> OrderCounterExamples,
        IReadOnlyList<MemberTally> Members,
        IReadOnlyList<string> DeclaredNeverSeen,
        IReadOnlyList<string> SeenNotDeclared);

    /// <param name="root">Installation (or any packages-shaped root) to read.</param>
    /// <param name="element">Root element to census, e.g. <c>AxTable</c>.</param>
    /// <param name="limit">Stop after this many documents. 0 reads every one.</param>
    /// <param name="sampleValues">Distinct leaf values to keep per member.</param>
    /// <param name="parallelism">Documents parsed at once. Defaults to half the cores.</param>
    public static Report Run(string root, string element, int limit = 0, int sampleValues = 5, int? parallelism = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(element);
        var sw = Stopwatch.StartNew();

        var files = EnumerateDocuments(root, element).ToList();
        if (limit > 0 && files.Count > limit) files = files.Take(limit).ToList();

        var occurrences = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var fileCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var values = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        // member -> the members it has been seen AFTER. A pair seen in both directions is proof
        // that the order is not fixed.
        var seenAfter = new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);
        var scanned = 0;

        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism ?? Math.Max(1, Environment.ProcessorCount / 2) },
            path =>
            {
                XDocument doc;
                try { doc = XDocument.Load(path); }
                catch { return; }
                if (doc.Root is null || !string.Equals(doc.Root.Name.LocalName, element, StringComparison.OrdinalIgnoreCase))
                    return;

                Interlocked.Increment(ref scanned);

                var here = new List<string>();
                foreach (var child in doc.Root.Elements())
                {
                    var name = child.Name.LocalName;
                    occurrences.AddOrUpdate(name, 1, (_, n) => n + 1);
                    if (!here.Contains(name, StringComparer.Ordinal)) here.Add(name);

                    if (!child.HasElements)
                    {
                        var text = child.Value.Trim();
                        if (text.Length is > 0 and <= 60)
                            values.GetOrAdd(name, _ => new(StringComparer.Ordinal)).TryAdd(text, 0);
                    }
                }

                foreach (var name in here.Distinct(StringComparer.Ordinal))
                    fileCounts.AddOrUpdate(name, 1, (_, n) => n + 1);

                // Record the pairwise order this document used.
                for (var i = 0; i < here.Count; i++)
                for (var j = i + 1; j < here.Count; j++)
                    seenAfter.GetOrAdd(here[j], _ => new(StringComparer.Ordinal)).TryAdd(here[i], 0);
            });

        var contract = MetadataContracts.Find(element)
                       ?? MetadataContracts.ForElement(element, null);
        var declared = contract?.Members ?? Array.Empty<string>();

        var members = occurrences.Keys
            .OrderByDescending(m => fileCounts.TryGetValue(m, out var f) ? f : 0)
            .ThenBy(m => m, StringComparer.Ordinal)
            .Select(m => new MemberTally(
                m,
                fileCounts.TryGetValue(m, out var f) ? f : 0,
                occurrences[m],
                contract is null ? null : declared.Contains(m, StringComparer.Ordinal),
                values.TryGetValue(m, out var vs)
                    ? vs.Keys.OrderBy(v => v, StringComparer.Ordinal).Take(sampleValues).ToList()
                    : Array.Empty<string>()))
            .ToList();

        // A pair seen in both directions across the corpus is a counter-example to any order rule.
        var counterExamples = new List<string>();
        foreach (var (later, earlier) in seenAfter.SelectMany(kv => kv.Value.Keys.Select(e => (kv.Key, e))))
        {
            if (seenAfter.TryGetValue(earlier, out var reverse) && reverse.ContainsKey(later)
                && string.CompareOrdinal(later, earlier) < 0)
                counterExamples.Add($"{later} ↔ {earlier}");
        }
        counterExamples = counterExamples.Distinct(StringComparer.Ordinal).OrderBy(c => c, StringComparer.Ordinal).ToList();

        sw.Stop();
        return new Report(
            root,
            element,
            contract?.Name,
            scanned,
            sw.ElapsedMilliseconds,
            OrderIsStable: counterExamples.Count == 0,
            counterExamples.Take(20).ToList(),
            members,
            contract is null
                ? Array.Empty<string>()
                : declared.Where(d => !occurrences.ContainsKey(d)).ToList(),
            contract is null
                ? Array.Empty<string>()
                : occurrences.Keys
                    .Where(m => !declared.Contains(m, StringComparer.Ordinal)
                                && MetadataContracts.Find(m) is null)   // a collection item is not a member
                    .OrderBy(m => m, StringComparer.Ordinal)
                    .ToList());
    }

    /// <summary>Documents whose root is <paramref name="element"/>, found by folder convention.</summary>
    /// <remarks>
    /// The AOT folder is named after the element it holds (<c>AxTable</c> → <c>AxTable/</c>), so
    /// the census reads only the folders that can contain the element rather than every file in
    /// the installation.
    /// </remarks>
    private static IEnumerable<string> EnumerateDocuments(string root, string element)
    {
        if (!Directory.Exists(root)) yield break;

        foreach (var package in Safe(root))
        foreach (var model in Safe(package))
        foreach (var axDir in Safe(model))
        {
            if (!string.Equals(Path.GetFileName(axDir), element, StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(axDir, "*.xml", SearchOption.TopDirectoryOnly); }
            catch { continue; }
            foreach (var f in files) yield return f;
        }
    }

    private static IEnumerable<string> Safe(string dir)
    {
        try { return Directory.EnumerateDirectories(dir); }
        catch { return Array.Empty<string>(); }
    }
}
