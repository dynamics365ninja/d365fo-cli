namespace D365FO.Core.Index;

/// <summary>
/// Structural where-used, issue #249 item 8: which objects reference a name in their metadata —
/// form and query data sources, table relations, field EDTs, menu item targets, privilege entry
/// points, extensions, CoC, event handlers — answered from the index, with no build and no XML
/// read. The X++ text scan answers "which code mentions it" and the compiler's cross-reference
/// answers both but needs a build; this answers "where is this table bound" in well under a
/// second on a full installation. <c>find refs --structural</c>, <c>find where-used</c>, and MCP
/// <c>find_references</c> with <c>structural=true</c> all run it.
/// </summary>
public static class StructuralWhereUsed
{
    /// <summary>The referencing kinds the index can report, for validating <c>kind</c>.</summary>
    private static readonly string[] Kinds =
    [
        "Table", "TableExtension", "Map", "Edt", "Form", "Query", "View", "DataEntityView",
        "MenuItemDisplay", "MenuItemAction", "MenuItemOutput", "Tile",
        "SecurityPrivilege", "SecurityDuty", "SecurityRole", "SecurityPolicy",
        "Service", "ServiceGroup", "Report", "Class", "ConfigurationKey",
        "FormExtension", "EdtExtension", "EnumExtension", "ViewExtension", "MapExtension",
    ];

    /// <summary>What the answer cannot contain, so a short list is not read as complete.</summary>
    public const string NotCovered =
        "X++ code (find_references without structural, or xref=true), form control data fields, data entity "
        + "data sources, table fields typed directly on an enum, report RDP classes";

    /// <param name="repo">The index.</param>
    /// <param name="name">The referenced object (table, EDT, enum, class, form, query, menu item, privilege, duty, service …).</param>
    /// <param name="kind">Keep only referencing objects of this kind, or whose kind starts with it (<c>MenuItem</c>, <c>Security</c>).</param>
    /// <param name="model">Keep only referencing objects declared in this model.</param>
    /// <param name="limit">Most items returned; the counts stay complete.</param>
    public static ToolResult<object> Run(MetadataRepository repo, string name, string? kind, string? model, int limit)
    {
        if (string.IsNullOrWhiteSpace(name))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "name is required.");
        kind = string.IsNullOrWhiteSpace(kind) ? null : kind.Trim();
        if (kind is not null && !Kinds.Any(k => k.StartsWith(kind, StringComparison.OrdinalIgnoreCase)))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown kind '{kind}' for the structural where-used.",
                "Use a referencing object kind, or the start of one: " + string.Join(", ", Kinds) + ".");
        if (!ModelFilter.TryResolve(repo, model, out model, out var badModel)) return badModel!;
        if (limit <= 0) limit = 200;

        var all = repo.FindStructuralReferences(name)
            .Where(r => kind is null || r.Kind.StartsWith(kind, StringComparison.OrdinalIgnoreCase))
            .Where(r => model is null || string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Element, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var items = all.Take(limit)
            .Select(r => new { kind = r.Kind, name = r.Name, model = r.Model, element = r.Element, via = r.Via })
            .ToList();

        return ToolResult<object>.Success(new
        {
            name = name.Trim(),
            mode = "structural",
            source = "index",
            scope = MetadataRepository.StructuralReferenceSources,
            notCovered = NotCovered,
            count = all.Count,
            truncated = all.Count > items.Count,
            // Complete whatever the limit: "720 relations, 361 form/query data sources" is often
            // the whole answer an agent needs.
            byVia = Tally(all.Select(r => BareVia(r.Via))),
            byKind = Tally(all.Select(r => r.Kind)),
            items,
            hint = all.Count == 0
                ? "No metadata in the index references this name. For X++ code, run find_references without structural "
                  + "(`d365fo find refs <NAME>`); check the name with `search`."
                : null,
        });
    }

    /// <summary>The via without its detail: <c>EntryPoint (Read)</c> counts as <c>EntryPoint</c>.</summary>
    private static string BareVia(string via)
    {
        var paren = via.IndexOf(" (", StringComparison.Ordinal);
        return paren < 0 ? via : via[..paren];
    }

    private static Dictionary<string, int> Tally(IEnumerable<string> keys) =>
        keys.GroupBy(k => k, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
}
