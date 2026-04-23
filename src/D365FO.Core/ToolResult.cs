namespace D365FO.Core;

/// <summary>
/// Standard envelope for every CLI and MCP tool response.
/// Matches the schema in docs/SCHEMA.md so downstream agents can rely on it.
/// </summary>
public sealed record ToolResult<T>(
    bool Ok,
    T? Data,
    ToolError? Error,
    IReadOnlyList<string>? Warnings = null)
{
    public static ToolResult<T> Success(T data, IReadOnlyList<string>? warnings = null)
        => new(true, data, null, warnings);

    public static ToolResult<T> Fail(string code, string message, string? hint = null)
        => new(false, default, new ToolError(code, message, hint));
}

public sealed record ToolError(string Code, string Message, string? Hint);
