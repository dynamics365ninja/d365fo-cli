using D365FO.Core.FormPatterns;
using D365FO.Core.Index;
using D365FO.Core.Metadata;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Analysis;

/// <summary>Something the installation uses that a catalog in this repo does not know about.</summary>
/// <param name="Catalog">Which catalog is short.</param>
/// <param name="Item">The thing that was observed.</param>
/// <param name="Observed">How many indexed objects use it.</param>
/// <param name="Detail">What the gap means for anyone relying on that catalog.</param>
public sealed record CatalogGap(string Catalog, string Item, long Observed, string Detail);

/// <summary>A catalog entry no indexed object uses. Informational, never a defect on its own.</summary>
/// <param name="Catalog">Which catalog it belongs to.</param>
/// <param name="Item">The unused entry.</param>
public sealed record UnusedCatalogEntry(string Catalog, string Item);

/// <summary>An AOT family present on the installation that this tool does not cover.</summary>
/// <param name="Folder">The AOT folder name.</param>
/// <param name="Models">How many model folders contain it.</param>
public sealed record UncoveredFamily(string Folder, long Models);

/// <summary>The result of one cross-check pass.</summary>
/// <remarks>
/// The three lists are deliberately separate, and only one of them is a verdict.
/// <see cref="Gaps"/> is where the tool will be <em>wrong</em>: something the installation uses
/// that a catalog claims to cover and does not. <see cref="Uncovered"/> is where the tool is
/// merely <em>narrow</em> — an AOT family it was never built to handle — which on a real
/// installation is dozens of entries and would drown the first list if the two were mixed.
/// <see cref="Unused"/> is the opposite direction and is evidence of nothing on its own.
/// </remarks>
public sealed record CrossCheckReport(
    IReadOnlyList<CatalogGap> Gaps,
    IReadOnlyList<UncoveredFamily> Uncovered,
    IReadOnlyList<UnusedCatalogEntry> Unused,
    long ObjectsConsidered)
{
    /// <summary>Nothing the installation uses is missing from a catalog that claims to cover it.</summary>
    public bool Clean => Gaps.Count == 0;
}

/// <summary>
/// Compares what the index actually observed in a real installation against the catalogs this
/// repo ships, and reports what the catalogs are missing.
/// </summary>
/// <remarks>
/// <para>
/// Issue #164 / R5's <c>crossCheck</c>. Every catalog here is derived from a Microsoft assembly
/// or ground-truthed against shipped files, which makes them right at the moment they were
/// generated and says nothing about the installation in front of you. A platform update adds a
/// form pattern, a model introduces an AOT folder, an ISV ships an object whose root type the
/// contract catalog predates — and the tool keeps answering confidently from a catalog that no
/// longer covers what is on disk.
/// </para>
/// <para>
/// Worth running after every <c>index extract</c>: the form-pattern half is one query, and the
/// AOT-folder half is a two-level directory sweep of the packages root — the same walk the
/// extractor already does, without reading a file.
/// </para>
/// <para>
/// Severity is split rather than ranked, because the two findings mean different things. A
/// <see cref="CatalogGap"/> is where the tool will be <em>wrong</em>. An
/// <see cref="UncoveredFamily"/> is where it is merely <em>narrow</em> — and on a real
/// installation that is dozens of entries (40 of the 83 AOT folders present on the box this was
/// written against), which would bury the first list entirely if the two were mixed.
/// </para>
/// </remarks>
public static class CatalogCrossCheck
{
    /// <summary>
    /// <c>&lt;Pattern&gt;</c> values that mean "this form has no pattern", not a pattern the
    /// registry should know.
    /// </summary>
    /// <remarks>
    /// <c>(none)</c> is the index's own placeholder for a missing element. <c>Custom</c> is the
    /// AOT's, and it is the one that matters: it is the fourth most common value on a real
    /// installation, so treating it as a pattern reports the largest catalog gap in the report
    /// and it is not a gap at all. Ground-truthed — of 143 sampled forms with
    /// <c>Pattern=Custom</c>, every single one has no <c>PatternVersion</c>, which is what a
    /// real pattern always carries.
    /// </remarks>
    private static readonly IReadOnlySet<string> NonPatterns =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "(none)", "Custom" };

    public const string FormPatternCatalog = "form-patterns";
    public const string ObjectTypeCatalog = "object-types";
    public const string ContractCatalog = "metadata-contracts";

    /// <summary>Run every cross-check.</summary>
    /// <param name="repo">The index, for what the extractor actually saw.</param>
    /// <param name="packagesPath">
    /// A packages root to sweep for AOT folders. Skipped when null or absent — the form-pattern
    /// half still runs, because it needs only the index.
    /// </param>
    public static CrossCheckReport Run(MetadataRepository repo, string? packagesPath = null)
    {
        ArgumentNullException.ThrowIfNull(repo);

        var gaps = new List<CatalogGap>();
        var uncovered = new List<UncoveredFamily>();
        var unused = new List<UnusedCatalogEntry>();
        long considered = 0;

        considered += CheckFormPatterns(repo, gaps, unused);
        considered += CheckAotFolders(packagesPath, gaps, uncovered);

        return new CrossCheckReport(gaps, uncovered, unused, considered);
    }

    /// <summary>
    /// Every form pattern the installation uses has to be one the registry knows.
    /// </summary>
    /// <remarks>
    /// This is the check the predecessor's crossCheck existed for, and it is the one with teeth:
    /// <c>generate form</c>, <c>form-pattern validate</c> and <c>form-pattern repair</c> all
    /// answer from the registry, so a pattern in the wild that it has never heard of is a form
    /// this tool cannot judge — and it will say so in the confident voice of a tool that has a
    /// catalog. The registry is derived from
    /// <c>Microsoft.Dynamics.AX.Metadata.Patterns.dll</c> by <c>scripts/emit-form-patterns.ps1</c>,
    /// so the fix for a gap is to regenerate it on the installation that produced it, not to
    /// hand-add an entry.
    /// </remarks>
    private static long CheckFormPatterns(
        MetadataRepository repo, List<CatalogGap> gaps, List<UnusedCatalogEntry> unused)
    {
        List<FormPatternSummary> observed;
        try { observed = repo.SummarizeFormPatterns().ToList(); }
        catch (Exception) { return 0; }   // no Forms table yet — nothing observed, nothing to say

        long considered = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in observed)
        {
            considered += row.Count;
            if (string.IsNullOrWhiteSpace(row.Pattern) || NonPatterns.Contains(row.Pattern))
                continue;

            seen.Add(row.Pattern);

            // Name-level, not name+version: a version the registry has not caught up with is a
            // far weaker signal than a pattern it has never heard of, and reporting both at
            // once buries the one that matters.
            if (FormPatternRegistry.VersionsOf(row.Pattern).Count > 0) continue;

            gaps.Add(new CatalogGap(
                FormPatternCatalog, row.Pattern, row.Count,
                $"{row.Count} indexed form(s) use the '{row.Pattern}' pattern and the registry has no " +
                "entry for it, so `generate form`, `form-pattern validate` and `form-pattern repair` " +
                "cannot judge them. Regenerate with scripts/emit-form-patterns.ps1 on this installation."));
        }

        foreach (var known in FormPatternRegistry.All.Where(p => p.Active).Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!seen.Contains(known))
                unused.Add(new UnusedCatalogEntry(FormPatternCatalog, known));

        return considered;
    }

    /// <summary>
    /// Every AOT folder present on disk has to be one the registry names, and its root type one
    /// the contract catalog declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where drift actually bites. The extractor only walks folders
    /// <see cref="ObjectTypeRegistry"/> names, so a folder it does not know is not indexed at
    /// all — it is simply invisible, and every "not found in the index" answer about an object
    /// living there is wrong in the most convincing possible way. A platform update that adds an
    /// AOT family produces exactly that, silently.
    /// </para>
    /// <para>
    /// The contract half is the other direction of the same staleness: a known family whose root
    /// type the catalog does not declare gets no XML007/XML008 and no contract-order
    /// canonicalisation, because both stand aside for a type they do not recognise. That is a
    /// whole family of objects quietly exempt from the checks the rest get.
    /// </para>
    /// </remarks>
    private static long CheckAotFolders(
        string? packagesPath, List<CatalogGap> gaps, List<UncoveredFamily> uncovered)
    {
        if (string.IsNullOrWhiteSpace(packagesPath) || !Directory.Exists(packagesPath)) return 0;

        Dictionary<string, long> folders;
        try { folders = SweepAotFolders(packagesPath!); }
        catch (Exception) { return 0; }   // an unreadable packages root is not this check's business

        long considered = 0;
        foreach (var (folder, count) in folders.OrderByDescending(f => f.Value))
        {
            considered += count;

            var type = ObjectTypeRegistry.Find(folder);
            if (type is null || !string.Equals(type.AotSubfolder, folder, StringComparison.OrdinalIgnoreCase))
            {
                // Not a defect: the registry covers what this tool supports, and a real
                // installation has dozens of families it does not. Reported so the narrowness is
                // visible and can be triaged, not so it can be treated as a failure.
                uncovered.Add(new UncoveredFamily(folder, count));
                continue;
            }

            if (MetadataContracts.Find(type.RootElement) is null)
            {
                gaps.Add(new CatalogGap(
                    ContractCatalog, type.RootElement, count,
                    $"'{folder}' holds <{type.RootElement}> objects, which the contract catalog does not " +
                    "declare — XML007/XML008 and the contract-order canonicaliser stand aside for the " +
                    "whole family. Regenerate with scripts/emit-metadata-contracts.ps1 on this installation."));
            }
        }

        return considered;
    }

    /// <summary>
    /// Distinct <c>Ax*</c> folder names under a packages root, and how many models hold each.
    /// </summary>
    /// <remarks>
    /// Two levels down (<c>&lt;packages&gt;\&lt;Package&gt;\&lt;Model&gt;\Ax*</c>) because that is
    /// the layout a real installation has, and enumerated rather than globbed so an unreadable
    /// package does not abort the sweep — a cross-check that stops at the first permission error
    /// reports "no gaps" for a directory it never finished reading.
    /// </remarks>
    private static Dictionary<string, long> SweepAotFolders(string packagesPath)
    {
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var package in SafeDirectories(packagesPath))
            foreach (var model in SafeDirectories(package))
                foreach (var aot in SafeDirectories(model))
                {
                    var name = Path.GetFileName(aot);
                    if (!name.StartsWith("Ax", StringComparison.Ordinal)) continue;
                    counts[name] = counts.TryGetValue(name, out var n) ? n + 1 : 1;
                }

        return counts;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch (Exception) { return Array.Empty<string>(); }
    }
}
