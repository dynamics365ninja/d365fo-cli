using D365FO.Core;
using D365FO.Core.Journal;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Journal;

/// <summary>
/// <c>d365fo undo [--steps N] [--dry-run]</c> — revert the last N modification-journal
/// entries, replaying each in reverse through the same write path (disk or bridge) that
/// produced it (issue #113). Deterministic, single-command rollback for agent loops: a
/// failed build/BP check can be undone without relying on <c>.bak</c> files or a clean git
/// worktree over <c>PackagesLocalDirectory</c>.
/// </summary>
public sealed class UndoCommand : Command<UndoCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--steps <N>")]
        [System.ComponentModel.Description("Number of journal entries to revert, most-recent-first (default 1).")]
        public int Steps { get; init; } = 1;

        [CommandOption("--dry-run")]
        [System.ComponentModel.Description("Preview what would be reverted without changing anything.")]
        public bool DryRun { get; init; }

        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var journal = ModificationJournal.ForIndex(settings.DatabasePath);

        if (journal.Count() == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.JournalEmpty,
                "The modification journal is empty — nothing to undo.",
                "Every write from `generate`, `labels create|rename|delete`, and `delete` appends an entry here."));

        // Undo is destructive. A caller that passes 0 or a negative count has made a mistake —
        // silently rounding that up to 1 reverts a write nobody asked to revert, so fail loudly
        // instead.
        if (settings.Steps <= 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.InvalidArgs,
                $"--steps must be 1 or greater; got {settings.Steps}.",
                "Use `--steps 1` to revert the most recent entry, or `--dry-run` to preview first."));

        var steps = settings.Steps;
        var result = UndoEngine.Undo(journal, steps, settings.DryRun);

        var touchedModels = result.Steps
            .Where(s => s.Ok && !string.IsNullOrWhiteSpace(s.Entry.Model))
            .Select(s => s.Entry.Model!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var warnings = new List<string>();
        if (!settings.DryRun && touchedModels.Count > 0)
            warnings.Add($"Index not auto-refreshed for {string.Join(", ", touchedModels)} — run `d365fo index refresh --model <MODEL>` (or without --model) so reverted objects are searchable again.");
        foreach (var rnr in result.Steps.Where(s => s.RnrProjWarning is not null).Select(s => s.RnrProjWarning!))
            warnings.Add(rnr);

        var payload = new
        {
            dryRun = result.DryRun,
            requestedSteps = steps,
            reverted = result.Steps.Count(s => s.Ok),
            steps = result.Steps.Select(s => new
            {
                id = s.Entry.Id,
                timestampUtc = s.Entry.TimestampUtc,
                command = s.Entry.Command,
                targetType = s.Entry.TargetType.ToString(),
                kind = s.Entry.Kind,
                name = s.Entry.ObjectName,
                model = s.Entry.Model,
                operation = s.Entry.Operation.ToString(),
                writePath = s.Entry.WritePath.ToString(),
                target = s.Entry.TargetPath,
                ok = s.Ok,
                error = s.Error,
                detail = s.Detail,
            }),
        };

        if (!result.AllOk)
        {
            var failedStep = result.Steps.FirstOrDefault(s => !s.Ok);
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.UndoFailed,
                $"Undo stopped after {result.Steps.Count(s => s.Ok)} of {steps} step(s): {failedStep?.Error}",
                "Earlier (older) journal entries were left untouched. Fix the underlying issue (e.g. bridge unavailable) and retry `d365fo undo`.")
                with { Data = payload });
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(payload, warnings.Count > 0 ? warnings : null));
    }
}

/// <summary>
/// <c>d365fo journal list [--limit N]</c> — inspect the modification-journal stack without
/// reverting anything.
/// </summary>
public sealed class JournalListCommand : Command<JournalListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("-n|--limit <N>")]
        [System.ComponentModel.Description("Row cap, most-recent-first (default 50).")]
        public int Limit { get; init; } = 50;

        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var journal = ModificationJournal.ForIndex(settings.DatabasePath);
        var limit = settings.Limit <= 0 ? 50 : settings.Limit;
        var entries = journal.List(limit);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            journalDirectory = journal.JournalDirectory,
            count = entries.Count,
            totalCount = journal.Count(),
            entries = entries.Select(e => new
            {
                id = e.Id,
                timestampUtc = e.TimestampUtc,
                command = e.Command,
                targetType = e.TargetType.ToString(),
                kind = e.Kind,
                name = e.ObjectName,
                secondaryKey = e.SecondaryKey,
                model = e.Model,
                operation = e.Operation.ToString(),
                writePath = e.WritePath.ToString(),
                target = e.TargetPath,
                hasPreImage = e.PreImage is not null,
                isTombstone = e.IsTombstone,
                rnrProjDelta = e.RnrProjDelta is null ? null : new
                {
                    e.RnrProjDelta.RnrProjPath,
                    e.RnrProjDelta.Include,
                    e.RnrProjDelta.WasAdded,
                },
            }),
        }));
    }
}
