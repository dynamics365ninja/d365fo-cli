using D365FO.Core;
using D365FO.Core.Skills;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

/// <summary><c>d365fo skills install</c>: put this build's agent skills into an X++ repository.</summary>
public sealed class SkillsInstallCommand : Command<SkillsInstallCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<REPOSITORY>")]
        [System.ComponentModel.Description("Root of the X++ repository. For Copilot in Visual Studio, the folder that holds the .sln you open.")]
        public string Repository { get; init; } = "";

        [CommandOption("--for <AGENT>")]
        [System.ComponentModel.Description("claude (.claude/skills), copilot (.github/skills/d365fo-cli) or all.")]
        public string? For { get; init; }

        [CommandOption("--dry-run")]
        [System.ComponentModel.Description("Report what would be written and removed, and change nothing.")]
        public bool DryRun { get; init; }

        [CommandOption("--force")]
        [System.ComponentModel.Description("Overwrite files of the same name that d365fo did not write.")]
        public bool Force { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        SkillTarget[]? targets = settings.For?.ToLowerInvariant() switch
        {
            "claude" => [SkillTarget.Claude],
            "copilot" => [SkillTarget.Copilot],
            "all" => [SkillTarget.Claude, SkillTarget.Copilot],
            _ => null,
        };
        if (targets is null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                settings.For is null ? "--for is required." : $"Unknown agent '{settings.For}'.",
                "Pass --for claude, --for copilot or --for all."));

        // A mistyped path would otherwise become a new folder no agent reads.
        if (!Directory.Exists(settings.Repository))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"'{settings.Repository}' is not an existing folder.",
                "Pass the root folder of your X++ repository."));

        try
        {
            // Every target is checked before any is written, so a conflict leaves nothing half done.
            foreach (var target in targets)
                SkillInstaller.Install(target, settings.Repository, dryRun: true, settings.Force);

            var results = targets
                .Select(t => SkillInstaller.Install(t, settings.Repository, settings.DryRun, settings.Force))
                .Select(r => new
                {
                    target = r.Target,
                    directory = r.Directory,
                    written = r.Written.Count,
                    unchanged = r.Unchanged.Count,
                    removed = r.Removed,
                    leftInPlace = r.LeftInPlace,
                })
                .ToList();

            var warnings = results
                .Where(r => r.leftInPlace.Count > 0)
                .Select(r => $"{r.directory} holds {r.leftInPlace.Count} skill(s) this version does not have and "
                             + "has no record of writing; delete them if they are from an earlier d365fo install.")
                .ToList();

            return RenderHelpers.Render(kind, ToolResult<object>.Success(
                new { version = ProductVersion.Informational, dryRun = settings.DryRun, targets = results },
                warnings.Count > 0 ? warnings : null));
        }
        catch (SkillConflictException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("SKILL_CONFLICT", ex.Message,
                "Rename or move those skills, or pass --force to overwrite them."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("SKILL_INSTALL_FAILED", ex.Message,
                "Check that the repository is writable and no editor holds the files."));
        }
    }
}
