using D365FO.Core;
using D365FO.Core.Index;

namespace D365FO.Mcp;

/// <summary>
/// Shared delegate surface used by the MCP transport to invoke the same core
/// operations that back the CLI. Every method returns a <see cref="ToolResult{T}"/>
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

    public ToolResult<object> SearchTables(string query, string? model = null, int limit = 50)
    {
        var items = _repo.SearchTables(query, model, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchEdts(string query, int limit = 50)
    {
        var items = _repo.SearchEdts(query, limit);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchEnums(string query, int limit = 50)
    {
        var items = _repo.SearchEnums(query, limit);
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

    public ToolResult<object> GetEnum(string name)
    {
        var e = _repo.GetEnum(name);
        return e is null
            ? ToolResult<object>.Fail("ENUM_NOT_FOUND", $"Enum '{name}' not found.")
            : ToolResult<object>.Success(e);
    }

    public ToolResult<object> GetMenuItem(string name)
    {
        var mi = _repo.GetMenuItem(name);
        return mi is null
            ? ToolResult<object>.Fail("MENU_ITEM_NOT_FOUND", $"Menu item '{name}' not found.")
            : ToolResult<object>.Success(mi);
    }

    public ToolResult<object> GetLabel(string file, string language, string key, bool raw = false)
    {
        var hit = _repo.GetLabel(file, language, key);
        if (hit is null)
            return ToolResult<object>.Fail("LABEL_NOT_FOUND", $"{file}/{language}:{key} not found.");
        if (!raw) hit = hit with { Value = StringSanitizer.Sanitize(hit.Value) };
        return ToolResult<object>.Success(hit);
    }

    public ToolResult<object> FindCoc(string targetClass, string? method = null)
    {
        var items = _repo.FindCocExtensions(targetClass, method);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> FindUsages(string symbol, int limit = 100)
    {
        var items = _repo.FindUsages(symbol, limit)
            .Select(t => new { kind = t.Kind, name = t.Name, model = t.Model })
            .ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> SearchLabels(string query, string[]? langs = null, int limit = 100, bool raw = false)
    {
        var items = _repo.SearchLabels(query, langs, limit);
        if (!raw)
            items = items.Select(l => l with { Value = StringSanitizer.Sanitize(l.Value) }).ToList();
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> GetSecurity(string obj, string type)
        => ToolResult<object>.Success(_repo.GetSecurityCoverage(obj, type));

    public ToolResult<object> GetTableRelations(string table)
    {
        var items = _repo.GetTableRelations(table);
        return ToolResult<object>.Success(new { count = items.Count, items });
    }

    public ToolResult<object> IndexStatus()
        => ToolResult<object>.Success(_repo.CountAll());
}
