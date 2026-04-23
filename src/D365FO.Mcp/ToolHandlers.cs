using D365FO.Core;
using D365FO.Core.Index;

namespace D365FO.Mcp;

/// <summary>
/// Shared delegate surface used by the MCP transport to invoke the same core
/// operations that back the CLI. Keeping this interface here lets us snap a
/// stdio/streamable-HTTP server on top in a follow-up commit without touching
/// D365FO.Core semantics. Every method returns a <see cref="ToolResult{T}"/>
/// so MCP tool handlers and CLI commands produce byte-identical envelopes.
/// </summary>
public sealed class ToolHandlers
{
    private readonly MetadataRepository _repo;

    public ToolHandlers(MetadataRepository repo) => _repo = repo;

    public ToolResult<object> SearchClasses(string query, string? model = null, int limit = 50)
    {
        var items = _repo.SearchClasses(query, model, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetTable(string name)
    {
        var t = _repo.GetTableDetails(name);
        return t is null
            ? ToolResult<object>.Fail("TABLE_NOT_FOUND", $"Table '{name}' not found.", "Run 'd365fo index build'.")
            : ToolResult<object>.Success(new { table = t.Table, fields = t.Fields, relations = t.Relations });
    }

    public ToolResult<object> GetEdt(string name)
    {
        var e = _repo.GetEdt(name);
        return e is null
            ? ToolResult<object>.Fail("EDT_NOT_FOUND", $"EDT '{name}' not found.")
            : ToolResult<object>.Success(e);
    }

    public ToolResult<object> GetClass(string name)
    {
        var c = _repo.GetClassDetails(name);
        return c is null
            ? ToolResult<object>.Fail("CLASS_NOT_FOUND", $"Class '{name}' not found.")
            : ToolResult<object>.Success(c);
    }

    public ToolResult<object> FindCoc(string targetClass, string? method = null)
    {
        var items = _repo.FindCocExtensions(targetClass, method);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchLabels(string query, string[]? langs = null, int limit = 100)
    {
        var items = _repo.SearchLabels(query, langs, limit)
            .Select(l => l with { Value = StringSanitizer.Sanitize(l.Value) })
            .ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetSecurity(string obj, string type)
        => ToolResult<object>.Success(_repo.GetSecurityCoverage(obj, type));

    public ToolResult<object> GetTableRelations(string table)
    {
        var items = _repo.GetTableRelations(table);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }
}
