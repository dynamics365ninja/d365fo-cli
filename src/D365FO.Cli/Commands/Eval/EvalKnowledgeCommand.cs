using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// Turns the corpus's <c>KNOWLEDGE_GAP</c>/<c>MODEL_ERROR</c> runs into topic-edit
/// proposals with provenance — the feedback path audit finding K4 called for and
/// docs/AGENT_EVAL_LOOP.md §12 listed as unbuilt.
/// </summary>
public sealed class EvalKnowledgeCommand : Command<EvalKnowledgeCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--out <FILE>")]
        [System.ComponentModel.Description("Write the proposals as a markdown brief instead of only reporting them.")]
        public string? Out { get; init; }

        [CommandOption("--class <CLASS>")]
        [System.ComponentModel.Description("KNOWLEDGE_GAP (default: both) | MODEL_ERROR.")]
        public string? Classification { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        string? only = null;
        if (!string.IsNullOrWhiteSpace(settings.Classification))
        {
            only = EvalTriage.Normalize(settings.Classification);
            if (only is null || !EvalKnowledgeFeedback.FeedbackClasses.Contains(only, StringComparer.Ordinal))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.BadInput,
                    $"--class '{settings.Classification}' is not a knowledge class. Use KNOWLEDGE_GAP or MODEL_ERROR."));
            }
        }

        var records = EvalCorpusStore.ReadAll(EvalPaths.CorpusRunsDir(root!));
        var (cases, _) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));

        var proposals = EvalKnowledgeFeedback.Build(records, cases, KnowledgeBase.Topics);
        if (only is not null)
            proposals = proposals.Where(p => p.Classification == only).ToList();

        string? written = null;
        if (!string.IsNullOrWhiteSpace(settings.Out))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(settings.Out!));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(settings.Out!, EvalKnowledgeFeedback.RenderMarkdown(proposals));
            written = settings.Out;
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = proposals.Count,
            corpusRuns = records.Count,
            written,
            proposals = proposals.Select(p => new
            {
                caseId = p.CaseId,
                classification = p.Classification,
                runs = p.Runs,
                runIds = p.RunIds,
                notes = p.Notes,
                instruction = p.Instruction,
                candidateTopics = p.Candidates.Select(c => new { topicId = c.TopicId, path = c.SourcePath, signal = c.Signal }),
            }),
        }));
    }
}
