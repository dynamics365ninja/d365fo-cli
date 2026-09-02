using System.Text.RegularExpressions;
using D365FO.Core.Extract;

namespace D365FO.Core.Index;

/// <summary>One matching line inside a method body.</summary>
public sealed record SourceRefMatchLine(int Line, string Text);

/// <summary>A method that references the searched symbol.</summary>
public sealed record SourceRefHit(
    string Kind,
    string Name,
    string Model,
    string Method,
    string? Path,
    IReadOnlyList<SourceRefMatchLine> Matches);

/// <summary>Result of a reverse-reference search over X++ method bodies.</summary>
/// <param name="Needle">The symbol that was searched for.</param>
/// <param name="Via">"fts" when served from MethodSourceFts, else "scan".</param>
/// <param name="FilesScanned">Source files opened. Zero with no hits means nothing was read.</param>
/// <param name="Truncated">The limit was reached, so there may be more.</param>
/// <param name="Hits">The methods that reference the symbol.</param>
/// <param name="Searched">
/// False when the corpus could not be read at all — no FTS rows and no source file opened.
/// </param>
/// <param name="Caveat">
/// Why an empty result is not evidence of absence, when it is not. Null when the search really
/// did look.
/// </param>
public sealed record SourceRefResult(
    string Needle,
    string Via,
    int FilesScanned,
    bool Truncated,
    IReadOnlyList<SourceRefHit> Hits,
    bool Searched = true,
    string? Caveat = null)
{
    /// <summary>
    /// The reachability verdict for a finished search.
    /// </summary>
    /// <remarks>
    /// A search over method bodies returns nothing when nothing matches, and also when there was
    /// nothing to search: no opt-in source index, and no source path on this machine — an index
    /// built elsewhere looks exactly like a codebase where the symbol is unused. Reporting the
    /// second as a clean zero is how "no callers" becomes "safe to delete".
    /// </remarks>
    public static SourceRefResult Finish(
        string needle, string via, int filesScanned, bool truncated,
        IReadOnlyList<SourceRefHit> hits, long ftsRows)
    {
        var searched = ftsRows > 0 || filesScanned > 0;
        return new SourceRefResult(needle, via, filesScanned, truncated, hits, searched,
            searched
                ? null
                : "Nothing was searched: the method-source index is empty and no source file could be opened. "
                  + "An empty result here is not evidence that the symbol is unused — run "
                  + "`d365fo index extract --index-source`, or point D365FO_PACKAGES_PATH at the installation "
                  + "the index was built from.");
    }
}

/// <summary>
/// Shared reverse-reference search over X++ method bodies, used by both
/// <c>find refs</c> (CLI) and the <c>find_references</c> MCP tool so the two
/// surfaces stay in parity.
///
/// When the opt-in <c>MethodSourceFts</c> index is populated
/// (<c>index extract --index-source</c>) the bulk of the work — class and table
/// methods — is answered by a single FTS5 <c>MATCH</c> instead of opening every
/// source XML on disk. Form method bodies are not extracted into the index, so
/// forms are always covered by an on-demand disk scan of just the form subset.
/// With no FTS index the whole corpus falls back to a disk scan, preserving the
/// previous behaviour.
/// </summary>
public static class MethodSourceSearch
{
    public static SourceRefResult Find(
        MetadataRepository repo,
        string needle,
        string? kind,
        string? model,
        int limit,
        int sampleLinesPerHit = 3)
    {
        var normKind = NormalizeKind(kind);
        var rx = new Regex($@"\b{Regex.Escape(needle)}\b", RegexOptions.IgnoreCase);
        var hits = new List<SourceRefHit>();
        int scanned = 0;
        bool truncated = false;

        if (repo.CountMethodSource() > 0)
        {
            // Class + Table methods: answered from the FTS index.
            if (normKind is null or "Class" or "Table")
            {
                var ftsKind = normKind is "Class" or "Table" ? normKind : null;
                // Quote the needle so identifiers with FTS-special characters can't
                // break MATCH syntax; unicode61 tokenization makes this an
                // identifier-token match, close to the \bword\b disk scan.
                var quoted = "\"" + needle.Replace("\"", "\"\"") + "\"";
                foreach (var mm in repo.SearchMethodSource(quoted, limit, model, ftsKind))
                {
                    if (hits.Count >= limit) { truncated = true; break; }
                    hits.Add(new SourceRefHit(
                        mm.Kind, mm.ObjectName, mm.Model, mm.MethodName, mm.SourcePath,
                        SampleLines(mm.SourcePath, mm.MethodName, rx, sampleLinesPerHit)));
                }
            }

            // Forms are not in the FTS index — scan just the (small) form subset.
            if (normKind is null or "Form")
                ScanSources(repo.EnumerateSourcePaths(model).Where(s => s.Kind == "Form"),
                            rx, limit, sampleLinesPerHit, hits, ref scanned, ref truncated);

            return SourceRefResult.Finish(needle, "fts", scanned, truncated, hits, repo.CountMethodSource());
        }

        // No FTS index: scan the whole corpus on disk.
        var sources = repo.EnumerateSourcePaths(model);
        if (normKind is not null)
            sources = sources.Where(s => string.Equals(s.Kind, normKind, StringComparison.OrdinalIgnoreCase)).ToList();
        ScanSources(sources, rx, limit, sampleLinesPerHit, hits, ref scanned, ref truncated);
        return SourceRefResult.Finish(needle, "scan", scanned, truncated, hits, ftsRows: 0);
    }

    private static void ScanSources(
        IEnumerable<(string Kind, string Name, string Model, string SourcePath)> sources,
        Regex rx, int limit, int sampleLinesPerHit,
        List<SourceRefHit> hits, ref int scanned, ref bool truncated)
    {
        foreach (var row in sources)
        {
            if (hits.Count >= limit) { truncated = true; break; }
            scanned++;
            var src = XppSourceReader.Read(row.SourcePath);
            if (src is null) continue;
            foreach (var m in src.Methods)
            {
                if (!rx.IsMatch(m.Body)) continue;
                if (hits.Count >= limit) { truncated = true; break; }
                hits.Add(new SourceRefHit(
                    row.Kind, row.Name, row.Model, m.Name, row.SourcePath,
                    SampleLinesFromBody(m.Body, rx, sampleLinesPerHit)));
            }
        }
    }

    private static IReadOnlyList<SourceRefMatchLine> SampleLines(string? path, string method, Regex rx, int max)
    {
        if (string.IsNullOrEmpty(path) || max <= 0) return Array.Empty<SourceRefMatchLine>();
        var src = XppSourceReader.Read(path);
        var block = src is null ? null : XppSourceReader.FindMethod(src, method);
        return block is null ? Array.Empty<SourceRefMatchLine>() : SampleLinesFromBody(block.Body, rx, max);
    }

    private static IReadOnlyList<SourceRefMatchLine> SampleLinesFromBody(string body, Regex rx, int max)
    {
        var res = new List<SourceRefMatchLine>();
        if (max <= 0) return res;
        var lines = body.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length && res.Count < max; i++)
            if (rx.IsMatch(lines[i]))
                res.Add(new SourceRefMatchLine(i + 1, lines[i].Trim()));
        return res;
    }

    /// <summary>Map a free-form kind filter to a stored source kind (Class/Table/Form), or null.</summary>
    private static string? NormalizeKind(string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return null;
        return kind.Trim().ToLowerInvariant() switch
        {
            "class" => "Class",
            "table" => "Table",
            "form"  => "Form",
            _       => kind, // pass through unknown values; they simply match nothing
        };
    }
}
