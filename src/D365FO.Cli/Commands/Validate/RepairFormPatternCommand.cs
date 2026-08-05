using D365FO.Core;
using D365FO.Core.FormPatterns;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Validate;

/// <summary>
/// Deterministic form auto-repair — the write-side counterpart of
/// <see cref="ValidateFormPatternCommand"/>. Applies the structural fixes the
/// validator already describes (missing required controls, control order, pattern
/// version, pattern-default properties, unambiguous sub-patterns) and reports what
/// it deliberately left alone.
///
/// Dry-run by default: without <c>--apply</c> (or <c>--out</c>) it prints the plan
/// and the repaired XML but writes nothing. Exit codes: 0 = clean or fully repaired,
/// 1 = command failure, 2 = violations remain that need a human decision.
/// </summary>
public sealed class RepairFormPatternCommand : Command<RepairFormPatternCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[FILE]")]
        [System.ComponentModel.Description("Path to the AxForm XML file to repair. Omit to read from stdin (implies dry-run).")]
        public string? File { get; init; }

        [CommandOption("--pattern <PATTERN>")]
        [System.ComponentModel.Description("Pattern to repair against. Required to adopt a form that declares none; overrides the declared pattern otherwise.")]
        public string? Pattern { get; init; }

        [CommandOption("--apply")]
        [System.ComponentModel.Description("Write the repaired XML back over FILE (a .bak copy is kept). Default: dry-run.")]
        public bool Apply { get; init; }

        [CommandOption("--out <PATH>")]
        [System.ComponentModel.Description("Write the repaired XML to PATH instead of over FILE.")]
        public string? Out { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var (xml, error) = ValidateInput.ReadCode(settings.File);
        if (error is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("INPUT_NOT_FOUND", error,
                "Pass a file path or pipe XML via stdin: `cat MyForm.xml | d365fo form-pattern repair`."));

        var result = FormPatternRepairer.Repair(xml!, settings.Pattern);

        // --apply needs a real file to overwrite; stdin has nowhere to write back to.
        string? written = null;
        var warnings = new List<string>();
        if (settings.Out is not null || settings.Apply)
        {
            var target = settings.Out ?? settings.File;
            if (string.IsNullOrWhiteSpace(target))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "--apply needs a FILE argument (there is nothing to write back to when reading stdin).",
                    "Pass the form path, or use --out <PATH>."));
            }

            if (!result.Changed)
            {
                warnings.Add("Nothing to write — the form already matches its pattern.");
            }
            else
            {
                try
                {
                    if (settings.Out is null && System.IO.File.Exists(target))
                        System.IO.File.Copy(target, target + ".bak", overwrite: true);
                    System.IO.File.WriteAllText(target, result.Xml);
                    written = target;
                }
                catch (Exception ex)
                {
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed,
                        $"Could not write repaired form to {target}: {ex.Message}"));
                }
            }
        }

        if (result.Skipped.Count > 0)
            warnings.Add($"{result.Skipped.Count} violation(s) need a human decision — see `skipped`.");

        var payload = ToolResult<object>.Success(new
        {
            formName = result.Before.FormName,
            pattern = result.After.Pattern,
            patternVersion = result.After.PatternVersion,
            changed = result.Changed,
            fullyRepaired = result.FullyRepaired,
            written,
            dryRun = written is null,
            errorsBefore = result.Before.ErrorCount,
            errorsAfter = result.After.ErrorCount,
            changes = result.Changes.Select(c => new { rule = c.Rule, path = c.Path, action = c.Action, detail = c.Detail }),
            skipped = result.Skipped.Select(c => new { rule = c.Rule, path = c.Path, reason = c.Detail }),
            remaining = result.After.Violations
                .Where(v => v.Severity == "error")
                .Select(v => new { rule = v.Rule, path = v.Path, excerpt = v.Excerpt, fix = v.Fix }),
            // Only carry the XML when the caller has nowhere else to get it from.
            repairedXml = written is null ? result.Xml : null,
        }, warnings.Count > 0 ? warnings : null);

        var rc = RenderHelpers.Render(kind, payload, _ =>
        {
            AnsiConsole.MarkupLine(
                $"[bold]{RenderHelpers.Escape(result.Before.FormName ?? "(form)")}[/] — pattern " +
                $"[blue]{RenderHelpers.Escape(result.After.Pattern ?? "(none)")}[/]");
            foreach (var c in result.Changes)
                AnsiConsole.MarkupLine($"[green]{c.Rule}[/] {RenderHelpers.Escape(c.Path)}: {RenderHelpers.Escape(c.Detail)}");
            foreach (var s in result.Skipped)
                AnsiConsole.MarkupLine($"[yellow]{s.Rule}[/] {RenderHelpers.Escape(s.Path)}: {RenderHelpers.Escape(s.Detail)}");
            AnsiConsole.MarkupLine(written is not null
                ? $"[green]wrote[/] {RenderHelpers.Escape(written)} ({result.Changes.Count} change(s), {result.After.ErrorCount} error(s) left)"
                : $"[grey]dry run[/] — {result.Changes.Count} change(s) would be made, {result.After.ErrorCount} error(s) would remain");
        });
        return rc != 0 ? rc : result.After.HasErrors ? 2 : 0;
    }
}
