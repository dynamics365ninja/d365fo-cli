using D365FO.Core;
using D365FO.Core.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Validate;

/// <summary>
/// Semantic reference resolver (&lt;200 ms, index-only) — port of the upstream
/// MCP <c>resolve_references</c> anti-hallucination gate. Verifies that every
/// type, table field, method (incl. arity), enum, label and intrinsic target
/// (tableStr, fieldStr, classStr, …) in generated X++ code EXISTS in the
/// indexed codebase. Catches hallucinated symbols BEFORE the compiler does.
/// Exit codes: 0 = clean (or warnings only), 1 = command failure, 2 = errors found.
/// </summary>
public sealed class ValidateReferencesCommand : Command<ValidateReferencesCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[FILE]")]
        [System.ComponentModel.Description("Path to the X++ file to resolve. Omit to read from stdin.")]
        public string? File { get; init; }

        [CommandOption("--file <PATH>")]
        [System.ComponentModel.Description("Same as the FILE argument, for callers that pass the path as an option.")]
        public string? FileOption { get; init; }

        [CommandOption("--context <NAME>")]
        [System.ComponentModel.Description("Owning class/table name, used in diagnostic messages.")]
        public string? Context { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var (file, conflict) = ValidateInput.ResolveFile(settings.File, settings.FileOption, "validate references");
        if (conflict is not null) return RenderHelpers.Render(kind, conflict);

        var (code, error) = ValidateInput.ReadCode(file);
        if (error is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("INPUT_NOT_FOUND", error,
                "Pass a file path or pipe code via stdin: `cat MyClass.xpp | d365fo validate references`."));

        D365FO.Core.Index.MetadataRepository repo;
        try
        {
            repo = RepoFactory.Create();
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("NO_INDEX",
                $"Reference resolution requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var result = ReferenceResolver.Resolve(code!, repo);
        var errors = result.Violations.Count(v => v.Severity == "error");
        var warnings = result.Violations.Count(v => v.Severity == "warning");

        var envelope = ToolResult<object>.Success(new
        {
            context = settings.Context,
            verifiedCount = result.VerifiedCount,
            errors,
            warnings,
            violations = result.Violations.Select(v => new
            {
                kind = v.Kind,
                severity = v.Severity,
                line = v.Line,
                identifier = v.Identifier,
                detail = v.Detail,
            }),
            verdict = errors > 0
                ? "Fix all errors before writing — these identifiers do not exist in the indexed codebase."
                : warnings > 0
                    ? "Warnings are informational (kernel classes and new labels are not indexable). Review, then proceed."
                    : $"All {result.VerifiedCount} reference(s) verified against the index. No hallucinated symbols detected.",
        });

        var rc = RenderHelpers.Render(kind, envelope, _ =>
        {
            foreach (var v in result.Violations)
            {
                var colour = v.Severity == "error" ? "red" : "yellow";
                var line = v.Line > 0 ? $" (line {v.Line})" : "";
                AnsiConsole.MarkupLine($"[{colour}]{v.Kind}[/]{line} {RenderHelpers.Escape(v.Identifier)}");
                AnsiConsole.MarkupLine($"  [grey]{RenderHelpers.Escape(v.Detail)}[/]");
            }
            AnsiConsole.MarkupLine(errors > 0
                ? $"[red]{errors} error(s)[/], [yellow]{warnings} warning(s)[/] — {result.VerifiedCount} verified"
                : $"[green]{result.VerifiedCount} reference(s) verified[/] ({warnings} warning(s))");
        });
        return rc != 0 ? rc : errors > 0 ? 2 : 0;
    }
}
