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
/// <see cref="FormPattern"/>. Mirrors <c>FormPatternTemplates.normalizePattern</c>
/// in upstream MCP. Falls back to <see cref="FormPattern.SimpleList"/> for
/// unknown input — that is the most common shape for new setup tables.
/// </summary>
public static class FormPatternNormalizer
{
    public static FormPattern Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return FormPattern.SimpleList;
        var s = new string(raw.Where(char.IsLetter).Select(char.ToLowerInvariant).ToArray());
        if (s.Contains("simplelist") && s.Contains("detail")) return FormPattern.SimpleListDetails;
        if (s.Contains("simplelist")) return FormPattern.SimpleList;
        if (s.Contains("listpage")) return FormPattern.ListPage;
        if (s.Contains("detailmaster") || s.Contains("detailsmaster") || s == "master") return FormPattern.DetailsMaster;
        if (s.Contains("detailtransaction") || s.Contains("detailstransaction") || s == "transaction") return FormPattern.DetailsTransaction;
        if (s.Contains("dropdialog") || s.Contains("dialog")) return FormPattern.Dialog;
        if (s.Contains("tableofcontents") || s.Contains("toc") || s.Contains("parameter")) return FormPattern.TableOfContents;
        if (s.Contains("lookup")) return FormPattern.Lookup;
        if (s.Contains("workspace") || s.Contains("panorama") || s.Contains("operational")) return FormPattern.Workspace;
        if (s == "list") return FormPattern.SimpleList;
        return FormPattern.SimpleList;
    }
}
