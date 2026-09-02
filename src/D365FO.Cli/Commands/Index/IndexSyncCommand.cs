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
        [System.ComponentModel.Description("Also full-text index X++ method bodies, as `index extract --index-source` does.")]
        public bool IndexSource { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        // A bare TARGET is a model name when it is not a path, and a path when it is — the
        // caller should not have to say which, since one of the two always parses.
        var target = settings.Target?.Trim();
        var looksLikePath = !string.IsNullOrEmpty(target)
            && (target.Contains(System.IO.Path.DirectorySeparatorChar)
                || target.Contains('/')
                || File.Exists(target)
                || Directory.Exists(target));

        return RenderHelpers.Render(OutputMode.Resolve(settings.Output), IndexSync.Sync(
            model: settings.Model ?? (looksLikePath ? null : target),
            path: looksLikePath ? target : null,
            packagesOverride: settings.PackagesPath,
            databasePath: settings.DatabasePath,
            indexSource: settings.IndexSource));
    }
}
