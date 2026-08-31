using System.Text.RegularExpressions;
using D365FO.Core;
using D365FO.Core.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Validate;

/// <summary>
/// Offline X++ / XML Best Practice validator (&lt;50 ms, all-platform). Port of
/// the upstream MCP <c>validate_xpp</c> tool: checks generated code against the
/// D365FO rule canon without xppbp.exe or a Windows VM. Property rules
/// (XML002–XML005) are data-driven when PropertyStats have been mined during
/// <c>index extract</c>.
/// Exit codes: 0 = clean (or warnings only), 1 = command failure, 2 = errors found.
/// </summary>
public sealed class ValidateXppCommand : Command<ValidateXppCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[FILE]")]
        [System.ComponentModel.Description("Path to the X++ / XML file to validate. Omit to read from stdin.")]
        public string? File { get; init; }

        [CommandOption("--code-type <TYPE>")]
        [System.ComponentModel.Description("xpp (default) for X++ source, xml-table for AxTable XML, xml-report for AxReport XML, xml-any for other XML. Auto-detected from file extension/content when omitted.")]
        public string? CodeType { get; init; }

        [CommandOption("--context <NAME>")]
        [System.ComponentModel.Description("Owning class/table name, used in diagnostic messages.")]
        public string? Context { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var (code, error) = ValidateInput.ReadCode(settings.File);
        if (error is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("INPUT_NOT_FOUND", error,
                "Pass a file path or pipe code via stdin: `cat MyClass.xpp | d365fo validate xpp`."));

        var codeType = XppValidator.NormalizeCodeType(settings.CodeType ?? DetectCodeType(settings.File, code!));

        // Property rules read mined statistics when an index exists; validation
        // itself never requires one (static defaults apply otherwise).
        IPropertyStatsProvider? stats = null;
        try
        {
            var repo = RepoFactory.Create();
            if (repo.HasPropertyStats()) stats = repo;
        }
        catch { /* no index — static defaults */ }

        // The path is passed through so XML013 can judge the folder the file sits in — it has
        // nothing to say when the document arrived on stdin.
        var violations = XppValidator.Validate(code!, codeType, stats, settings.File);
        var errors = violations.Count(v => v.Severity == "error");
        var warnings = violations.Count(v => v.Severity == "warning");

        var result = ToolResult<object>.Success(new
        {
            context = settings.Context,
            codeType,
            propertyRulesDataDriven = stats is not null,
            errors,
            warnings,
            violations = violations.Select(v => new
            {
                rule = v.Rule,
                severity = v.Severity,
                line = v.Line,
                excerpt = v.Excerpt,
                fix = v.Fix,
            }),
            verdict = errors > 0
                ? "Fix all errors before writing the file."
                : warnings > 0
                    ? "Address warnings where practical, then proceed."
                    : "No violations found.",
        });

        var rc = RenderHelpers.Render(kind, result, _ =>
        {
            foreach (var v in violations)
            {
                var colour = v.Severity == "error" ? "red" : "yellow";
                var line = v.Line is { } l ? $" (line {l})" : "";
                AnsiConsole.MarkupLine($"[{colour}]{v.Rule}[/]{line} {RenderHelpers.Escape(v.Excerpt)}");
                AnsiConsole.MarkupLine($"  [grey]{RenderHelpers.Escape(v.Fix)}[/]");
            }
            AnsiConsole.MarkupLine(errors > 0
                ? $"[red]{errors} error(s)[/], [yellow]{warnings} warning(s)[/]"
                : $"[green]clean[/] ({warnings} warning(s))");
        });
        return rc != 0 ? rc : errors > 0 ? 2 : 0;
    }

    private static string DetectCodeType(string? file, string code)
    {
        // Match the exact <AxTable> root tag, not other elements that merely start with
        // that prefix (e.g. <AxTableMapping> inside an AxMap, <AxTableExtension>).
        if (Regex.IsMatch(code, @"<AxTable[\s>]", RegexOptions.IgnoreCase)) return XppValidator.CodeTypeXmlTable;
        // An AxReport embeds RDL in CDATA — the X++ keyword rules would only produce noise
        // over it, so the report document routes to its own rule set (RPT101/RPT102).
        if (Regex.IsMatch(code, @"<AxReport[\s>]", RegexOptions.IgnoreCase)) return XppValidator.CodeTypeXmlReport;
        var trimmed = code.TrimStart();
        if (trimmed.StartsWith("<?xml") || trimmed.StartsWith('<')) return XppValidator.CodeTypeXmlAny;
        if (file is not null && file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return XppValidator.CodeTypeXmlAny;
        return XppValidator.CodeTypeXpp;
    }
}

/// <summary>Shared file/stdin input reader for the validate commands.</summary>
internal static class ValidateInput
{
    public static (string? Code, string? Error) ReadCode(string? file)
    {
        if (!string.IsNullOrEmpty(file))
        {
            if (!File.Exists(file)) return (null, $"File not found: {file}");
            return (File.ReadAllText(file), null);
        }
        if (Console.IsInputRedirected)
        {
            var code = Console.In.ReadToEnd();
            if (!string.IsNullOrWhiteSpace(code)) return (code, null);
        }
        return (null, "No input: pass a FILE argument or pipe code via stdin.");
    }
}
