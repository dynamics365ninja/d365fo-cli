using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// Scores an already-produced artifact against a case's golden — the
/// scoring-only entry point for the eval-runner agent, which drives the
/// case's natural-language <c>instruction</c> by hand through the regular
/// `d365fo` commands (against whatever index it already has connected) and
/// then hands the resulting file here.
/// </summary>
public sealed class EvalScoreCommand : Command<EvalScoreCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<CASE_ID>")]
        public string CaseId { get; init; } = "";

        [CommandOption("--actual <FILE>")]
        [System.ComponentModel.Description("Path to the produced artifact XML.")]
        public string Actual { get; init; } = "";

        [CommandOption("--source <SOURCE>")]
        [System.ComponentModel.Description("agent (default) | replay.")]
        public string? Source { get; init; }

        [CommandOption("--hypothesis <CLASS>")]
        [System.ComponentModel.Description("TOOL_DEFECT | VALIDATOR_GAP | KNOWLEDGE_GAP | MODEL_ERROR | ENV_FLAKE — recorded as a hypothesis, confirmed later by the eval-improver.")]
        public string? Hypothesis { get; init; }

        [CommandOption("--note <TEXT>")]
        public string? Note { get; init; }

        [CommandOption("--write")]
        [System.ComponentModel.Description("Append a corpus record to eval/corpus/runs/.")]
        public bool Write { get; init; }
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

        D365FO.Core.Index.MetadataRepository repo;
        try
        {
            repo = RepoFactory.Create();
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "NO_INDEX", $"Reference resolution requires the SQLite index: {ex.Message}",
                "Run `d365fo index build` then `d365fo index extract` first."));
        }

        var score = EvalScorer.Score(@case, settings.Actual, EvalPaths.GoldensDir(root!), repo);
        var source = string.IsNullOrWhiteSpace(settings.Source) ? "agent" : settings.Source!;

        if (settings.Write)
        {
            var record = new EvalCorpusRecord(
                RunId: $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}__{@case.Id}__{Guid.NewGuid():N}",
                CaseId: @case.Id,
                Tier: @case.Tier,
                TimestampUtc: DateTimeOffset.UtcNow,
                Source: source,
                Score: score,
                Classification: settings.Hypothesis,
                Note: settings.Note);
            EvalCorpusStore.Append(EvalPaths.CorpusRunsDir(root!), record);
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            caseId = @case.Id,
            tier = @case.Tier,
            source,
            xppClean = score.XppClean,
            xppErrors = score.XppErrors,
            referencesClean = score.ReferencesClean,
            referenceErrors = score.ReferenceErrors,
            goldenMatch = score.GoldenMatch,
            goldenDiff = new
            {
                missing = score.GoldenDiff.Missing,
                extra = score.GoldenDiff.Extra,
                changed = score.GoldenDiff.Changed.Select(c => new { path = c.Path, expected = c.Expected, actual = c.Actual }),
            },
            recorded = settings.Write,
        }));
    }
}
