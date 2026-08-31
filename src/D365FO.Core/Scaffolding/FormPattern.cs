namespace D365FO.Core.Scaffolding;

/// <summary>
/// D365FO form patterns supported by <see cref="FormPatternTemplates"/>.
/// Mirrors the catalogue from <c>d365fo-mcp-server</c>'s
/// <c>formPatternTemplates.ts</c> (validated against real AOT forms in
/// <c>K:\AosService\PackagesLocalDirectory</c>).
/// </summary>
public enum FormPattern
{
    /// <summary>Setup / config tables (&lt; 10 fields). Reference: <c>CustGroup</c>.</summary>
    SimpleList,

    /// <summary>Medium entities — left list panel + right details panel. Reference: <c>PaymTerm</c>.</summary>
    SimpleListDetails,

    /// <summary>Full master record with FastTabs. Reference: <c>CustTable</c>.</summary>
    DetailsMaster,

    /// <summary>Header + lines (orders, journals). Reference: <c>SalesTable</c>.</summary>
    DetailsTransaction,

    /// <summary>Modal popup dialog form. Reference: <c>ProjTableCreate</c>.</summary>
    Dialog,

    /// <summary>Tabbed parameters / setup pages. Reference: <c>CustParameters</c>.</summary>
    TableOfContents,

    /// <summary>Lookup form — grid + custom filter. Reference: <c>SysLanguageLookup</c>.</summary>
    Lookup,

    /// <summary>Workspace / area page (no edit). Reference: <c>CustTableListPage</c>.</summary>
    ListPage,

    /// <summary>Operational workspace — KPI tiles + panorama sections.</summary>
    Workspace,
}

/// <summary>
/// Maps fuzzy / casing-insensitive pattern names to the canonical
/// <see cref="FormPattern"/>, resolving through the 20-entry
/// <see cref="FormPatterns.FormPatternCatalog"/> so a name means the same thing
/// here as it does to <c>get form-pattern</c> and the form-pattern validator.
/// </summary>
/// <remarks>
/// Only the nine patterns <see cref="FormPatternTemplates"/> can actually build
/// resolve. A name the catalog knows but the scaffolder cannot emit (Wizard,
/// DropDialog, FormPart*, the legacy Task* pair, …) is an error, not a silent
/// downgrade: quietly handing back a <c>SimpleList</c> produced a form that
/// looked generated-to-spec and was not (audit finding G5). An empty
/// <c>--pattern</c> still means "no preference" and keeps the SimpleList default,
/// which is the right shape for a new setup table.
/// </remarks>
public static class FormPatternNormalizer
{
    /// <summary>Patterns <c>generate form</c> can emit, in catalog order.</summary>
    public static IReadOnlyList<string> GeneratableNames { get; } =
        FormPatterns.FormPatternCatalog.Patterns
            .Where(p => Enum.TryParse<FormPattern>(p.Id, ignoreCase: false, out _))
            .Select(p => p.XmlName)
            .ToArray();

    /// <summary>
    /// Patterns the catalog documents but the scaffolder cannot emit — usable
    /// via <c>d365fo get form-pattern &lt;NAME&gt;</c> as an authoring spec.
    /// </summary>
    public static IReadOnlyList<string> CatalogOnlyNames { get; } =
        FormPatterns.FormPatternCatalog.Patterns
            .Where(p => !Enum.TryParse<FormPattern>(p.Id, ignoreCase: false, out _))
            .Select(p => p.XmlName)
            .ToArray();

    /// <summary>
    /// Resolves <paramref name="raw"/> to a generatable pattern. Returns false with a
    /// caller-renderable <paramref name="error"/> when the name is unknown, or known to
    /// the catalog but not generatable.
    /// </summary>
    public static bool TryNormalize(string? raw, out FormPattern pattern, out string? error)
    {
        pattern = FormPattern.SimpleList;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var spec = FormPatterns.FormPatternCatalog.Resolve(raw);
        if (spec is not null && Enum.TryParse<FormPattern>(spec.Id, ignoreCase: false, out var generatable))
        {
            pattern = generatable;
            return true;
        }

        // Two catalog entries can describe the SAME AOS pattern — `Workspace` and
        // `WorkspaceOperational` both write <Pattern>WorkspaceOperational</Pattern>, one as
        // the templated entry and one as a catalog-only variant. Asking for either must reach
        // the template: it is the emitter the compiler has signed off, and routing the second
        // spelling to the generic expander produced a form the metadata reader rejected.
        if (spec is not null)
        {
            var templated = FormPatterns.FormPatternCatalog.Patterns.FirstOrDefault(p =>
                !ReferenceEquals(p, spec)
                && string.Equals(p.XmlName, spec.XmlName, StringComparison.OrdinalIgnoreCase)
                && Enum.TryParse<FormPattern>(p.Id, ignoreCase: false, out _));
            if (templated is not null && Enum.TryParse<FormPattern>(templated.Id, ignoreCase: false, out var sameAosPattern))
            {
                pattern = sameAosPattern;
                return true;
            }
        }

        var generatableList = string.Join("|", GeneratableNames);
        if (spec is null)
        {
            error = $"Unknown form pattern '{raw}'. Generatable: {generatableList}. " +
                    $"Catalog-only (author manually, see `d365fo get form-pattern <NAME>`): {string.Join("|", CatalogOnlyNames)}.";
            return false;
        }

        var variantHint = spec.VariantOf is not null
                          && Enum.TryParse<FormPattern>(spec.VariantOf, ignoreCase: false, out var parent)
            ? $" {spec.XmlName} is a variant of {parent} — pass --pattern {parent} if that shape will do."
            : string.Empty;

        error = $"Form pattern '{spec.XmlName}' ({spec.DisplayName}) is in the pattern catalog but " +
                $"`generate form` cannot scaffold it. Generatable: {generatableList}." + variantHint +
                $" Run `d365fo get form-pattern {spec.XmlName}` for the structure to author by hand.";
        return false;
    }
}
