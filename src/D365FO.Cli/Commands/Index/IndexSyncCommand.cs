using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Index;

/// <summary>
/// <c>d365fo index sync</c> — re-index one model, named directly or by a file inside it.
/// </summary>
/// <remarks>
/// <para>
/// The narrow counterpart to <c>index refresh</c>: after an edit made outside this tool — in
/// Visual Studio, by a git pull, by a colleague — the index is stale for exactly one model, and
/// re-walking every package to fix that costs minutes for work that takes seconds.
/// </para>
/// <para>
/// A model, not a file, because that is the unit the index writer replaces atomically. See
/// <see cref="IndexSync"/> for why a single-object write would delete the rest of the model.
/// </para>
/// </remarks>
public sealed class IndexSyncCommand : Command<IndexSyncCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[TARGET]")]
        [System.ComponentModel.Description("Model name, or a path to any file inside the model (the model is read off the packages layout).")]
        public string? Target { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Model to re-read, when the target is ambiguous or omitted.")]
        public string? Model { get; init; }

        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("Packages root to look in first. The configured roots are searched after it.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--index-source")]
        [System.ComponentModel.Description("Also full-text index X++ method bodies, as `index extract --index-source` does. Without this or --no-index-source the model keeps what its last extract did.")]
        public bool IndexSource { get; init; }

        [CommandOption("--no-index-source")]
        [System.ComponentModel.Description("Drop the model's method bodies from MethodSourceFts and stop re-indexing them.")]
        public bool NoIndexSource { get; init; }

        [CommandOption("--prune")]
        [System.ComponentModel.Description("Also delete the rows of models whose folder is no longer under any configured packages root. Needs no TARGET: `index sync --prune` only prunes.")]
        public bool Prune { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        if (IndexSync.ConflictingSourceFlags(settings.IndexSource, settings.NoIndexSource) is { } conflict)
            return RenderHelpers.Render(OutputMode.Resolve(settings.Output), conflict);

        // A bare TARGET is a model name when it is not a path, and a path when it is — the
        // caller should not have to say which, since one of the two always parses.
        // A folder that happens to share the model's name is not one of them (see SplitTarget).
        var (targetModel, targetPath) = IndexSync.SplitTarget(settings.Target);

        return RenderHelpers.Render(OutputMode.Resolve(settings.Output), IndexPrune.SyncThenPrune(
            model: settings.Model ?? targetModel,
            path: targetPath,
            packagesOverride: settings.PackagesPath,
            databasePath: settings.DatabasePath,
            indexSource: IndexSync.SourceChoice(settings.IndexSource, settings.NoIndexSource),
            prune: settings.Prune));
    }
}
