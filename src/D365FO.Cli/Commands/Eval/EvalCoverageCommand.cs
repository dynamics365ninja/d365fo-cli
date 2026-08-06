using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// The K ∧ E ∧ T coverage taxonomy: which AOT families and <c>generate</c>
/// capabilities are taught by the knowledge corpus, proven by an eval case, and
/// built by the tool. <c>--check</c> is the CI mode — it regenerates the report
/// and fails when <c>eval/COVERAGE.md</c> no longer matches, the same drift gate
/// the emitted skill variants get.
/// </summary>
public sealed class EvalCoverageCommand : Command<EvalCoverageCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--write")]
        [System.ComponentModel.Description("Regenerate eval/COVERAGE.md.")]
        public bool Write { get; init; }

        [CommandOption("--check")]
        [System.ComponentModel.Description("Fail when eval/COVERAGE.md differs from the derived report (CI gate).")]
        public bool Check { get; init; }

        [CommandOption("--gaps")]
        [System.ComponentModel.Description("List only the leaves the tool builds that knowledge or evals do not cover.")]
        public bool GapsOnly { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        var (cases, catalogErrors) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));
        if (catalogErrors.Count > 0)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_CATALOG_INVALID",
                $"The case catalog does not load cleanly, so coverage would be understated: {string.Join("; ", catalogErrors)}"));
        }

        var report = EvalCoverage.Build(cases, KnowledgeBase.Topics);
        var markdown = EvalCoverage.RenderMarkdown(report);
        var path = Path.Combine(root!, "eval", "COVERAGE.md");

        if (settings.Write)
        {
            File.WriteAllText(path, markdown);
        }
        else if (settings.Check)
        {
            var current = File.Exists(path) ? File.ReadAllText(path) : null;
            if (!string.Equals(Normalize(current), Normalize(markdown), StringComparison.Ordinal))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "COVERAGE_DRIFT",
                    current is null
                        ? "eval/COVERAGE.md does not exist."
                        : "eval/COVERAGE.md is stale — a family, capability, case or topic changed since it was generated.",
                    "Run `d365fo eval coverage --write` and commit the result."));
            }
        }

        var leaves = (settings.GapsOnly ? report.All.Where(l => !l.Complete && l.Tool) : report.All).ToList();

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            totalLeaves = report.TotalLeaves,
            completeLeaves = report.CompleteLeaves,
            families = report.Families.Count,
            capabilities = report.Capabilities.Count,
            written = settings.Write ? path : null,
            checkedAgainst = settings.Check ? path : null,
            leaves = leaves.Select(l => new
            {
                group = l.Group,
                id = l.Id,
                label = l.Label,
                status = l.Status,
                knowledge = l.Knowledge,
                eval = l.Eval,
                tool = l.Tool,
                knowledgeTopics = l.KnowledgeTopics,
                evalCases = l.EvalCases,
                builtBy = l.ToolNote,
            }),
        }));
    }

    /// <summary>Line endings differ between a Windows checkout and CI; the content is what must match.</summary>
    private static string? Normalize(string? text) => text?.Replace("\r\n", "\n").TrimEnd('\n');
}
