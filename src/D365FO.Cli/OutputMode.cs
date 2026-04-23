using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli;

/// <summary>
/// Detects interactive TTY. In non-TTY (piped, CI, agent) mode we default to
/// machine-readable JSON so downstream agents never parse ANSI tables.
/// </summary>
public static class OutputMode
{
    public static bool IsTty =>
        !Console.IsOutputRedirected
        && !Console.IsErrorRedirected
        && Environment.GetEnvironmentVariable("D365FO_FORCE_JSON") != "1";

    public enum Kind { Json, Table, Raw }

    public static Kind Resolve(string? flag)
    {
        if (!string.IsNullOrEmpty(flag))
        {
            return flag.ToLowerInvariant() switch
            {
                "json" => Kind.Json,
                "table" => Kind.Table,
                "raw" => Kind.Raw,
                _ => Kind.Json,
            };
        }
        return IsTty ? Kind.Table : Kind.Json;
    }
}

public abstract class D365OutputSettings : CommandSettings
{
    [CommandOption("-o|--output <FORMAT>")]
    [System.ComponentModel.Description("Output format: json (default when piped), table (default when TTY), raw")]
    public string? Output { get; init; }

    [CommandOption("--raw-text")]
    [System.ComponentModel.Description("Skip sanitization of metadata strings (labels). Default: sanitize.")]
    public bool RawText { get; init; }
}
