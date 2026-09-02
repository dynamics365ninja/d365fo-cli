using D365FO.Core.Analysis;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Review;

/// <summary>
/// <c>d365fo review diff</c> — what changed in the working tree, and the D365FO review hazards
/// among it.
/// </summary>
/// <remarks>
/// The diff and the rule engine are <see cref="WorkspaceReview"/>, shared with the MCP
/// <c>get_workspace_info(changes=true)</c> tool.
/// </remarks>
public sealed class ReviewDiffCommand : Command<ReviewDiffCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--base <REV>")]
        public string BaseRev { get; init; } = "HEAD";

        [CommandOption("--head <REV>")]
        public string HeadRev { get; init; } = "";

        [CommandOption("--repo <PATH>")]
        public string? RepoPath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            WorkspaceReview.Diff(settings.RepoPath, settings.BaseRev, settings.HeadRev));
}
