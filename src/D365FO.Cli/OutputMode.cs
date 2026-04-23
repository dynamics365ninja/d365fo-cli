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

    [CommandOption("--resolve-labels")]
    [System.ComponentModel.Description("Resolve @File+Id label tokens inline in the response. Language from D365FO_LABEL_LANGUAGES (default en-us).")]
    public bool ResolveLabels { get; init; }

    public override ValidationResult Validate()
    {
        // Piggy-back on Validate (invoked before Execute) to propagate the
        // --resolve-labels flag to RenderHelpers via AsyncLocal, so commands
        // don't each need to call EnableLabelResolution.
        if (ResolveLabels)
        {
            RenderHelpers.EnableLabelResolution(true);
        }
        return ValidationResult.Success();
    }
}
