using D365FO.Core;
using D365FO.Core.Analysis;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Analyze;

/// <summary>
/// <c>d365fo analyze completeness</c> — cross-check a workspace folder's AOT XML against the
/// index and report references that resolve to nothing.
///
/// The walk itself is <see cref="CompletenessAnalyzer"/>, shared with the MCP
/// <c>analyze(mode=completeness)</c> tool.
/// </summary>
public sealed class AnalyzeCompletenessCommand : Command<AnalyzeCompletenessCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<PATH>")]
        [System.ComponentModel.Description("Path to a model folder, PackagesLocalDirectory, or single AOT XML file to analyse.")]
        public string Path { get; init; } = "";

        [CommandOption("--skip-labels")]
        [System.ComponentModel.Description("Skip label-key existence checks (faster).")]
        public bool SkipLabels { get; init; }

        [CommandOption("--skip-edts")]
        [System.ComponentModel.Description("Skip EDT existence checks.")]
        public bool SkipEdts { get; init; }

        [CommandOption("--skip-security")]
        [System.ComponentModel.Description("Skip security role/duty/privilege cross-checks.")]
        public bool SkipSecurity { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var path = settings.Path.Trim();

        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, $"Path not found: {path}"));

        var report = CompletenessAnalyzer.Analyze(
            path, RepoFactory.Create(),
            new CompletenessAnalyzer.Options(settings.SkipLabels, settings.SkipEdts, settings.SkipSecurity));

        return RenderHelpers.Render(kind, report.IssueCount == 0
            ? ToolResult<object>.Success(report)
            : ToolResult<object>.Success(report, warnings: [$"{report.IssueCount} completeness issue(s) found."]));
    }
}
