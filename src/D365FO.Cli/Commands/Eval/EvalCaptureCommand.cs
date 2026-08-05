using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// Bootstraps/updates a case's golden from a human-reviewed artifact.
/// Goldens are captured, never hand-authored (docs/AGENT_EVAL_LOOP.md) —
/// this command is the capture step, run after a maintainer has inspected
/// the produced XML and confirmed it is correct.
/// </summary>
public sealed class EvalCaptureCommand : Command<EvalCaptureCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<CASE_ID>")]
        public string CaseId { get; init; } = "";

        [CommandOption("--actual <FILE>")]
        public string Actual { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        if (string.IsNullOrWhiteSpace(settings.Actual) || !File.Exists(settings.Actual))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, $"--actual file not found: {settings.Actual}"));
        }

        var (cases, catalogErrors) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));
        var @case = EvalCaseCatalog.Find(cases, settings.CaseId);
        if (@case is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_CASE_NOT_FOUND",
                $"No eval case '{settings.CaseId}' found." +
                (catalogErrors.Count > 0 ? $" Catalog errors: {string.Join("; ", catalogErrors)}" : "")));
        }

        var goldenDir = Path.Combine(EvalPaths.GoldensDir(root!), @case.GoldenPath);
        Directory.CreateDirectory(goldenDir);
        var destPath = Path.Combine(goldenDir, Path.GetFileName(settings.Actual));
        File.Copy(settings.Actual, destPath, overwrite: true);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            caseId = @case.Id,
            goldenPath = destPath,
            message = @case.GoldenPending
                ? "Golden captured. Flip golden_pending to false in the case JSON once reviewed."
                : "Golden updated.",
        }));
    }
}
