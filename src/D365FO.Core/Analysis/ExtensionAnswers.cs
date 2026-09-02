using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// "What extends this object, and what does the object look like once they are all folded in?"
/// </summary>
/// <remarks>
/// The merged view was reachable over MCP (<c>extension_info(mode=table-merge)</c>) and from
/// nowhere on the CLI, which is the wrong way round for a repository whose CLI is the primary
/// surface. It lives here now so <c>d365fo find extensions --merged</c> and the MCP tool return
/// the same answer rather than two that agree by inspection.
/// </remarks>
public static class ExtensionAnswers
{
    /// <param name="repo">Index to read.</param>
    /// <param name="table">Base table, or the name of one of its extensions.</param>
    public static ToolResult<object> TableMerge(MetadataRepository repo, string table)
    {
        ArgumentNullException.ThrowIfNull(repo);
        if (string.IsNullOrWhiteSpace(table))
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Target required.");

        var items = repo.FindExtensions(table, "Table");
        var merged = TableMergeAnalyzer.Merge(repo, table);

        var warnings = merged.Unreadable.Count > 0
            ? new List<string>
            {
                $"{merged.Unreadable.Count} extension(s) could not be read, so the merged schema is INCOMPLETE: "
                + string.Join("; ", merged.Unreadable),
            }
            : null;

        return ToolResult<object>.Success(new
        {
            target = merged.Table,
            baseModel = merged.BaseModel,
            count = items.Count,
            extensions = items,
            merged = new
            {
                fields = merged.Fields,
                indexes = merged.Indexes,
                relations = merged.Relations,
                fieldGroups = merged.FieldGroups,
                complete = merged.Unreadable.Count == 0,
            },
        }, warnings);
    }
}
