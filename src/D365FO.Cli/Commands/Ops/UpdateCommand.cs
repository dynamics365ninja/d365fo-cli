using System.Text.RegularExpressions;
using D365FO.Core;
using D365FO.Core.Ops;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

/// <summary>
/// <c>d365fo update</c> as the binary answers it. An npm installation never gets here: its
/// launcher (<c>npm/d365fo-cli/launcher/update.js</c>) runs the update itself, because a
/// running <c>d365fo.exe</c> cannot be replaced on Windows. Every other installation is told
/// the step that updates it — nothing is changed.
/// </summary>
public sealed partial class UpdateCommand : Command<UpdateCommand.Settings>
{
    private const string Repository = "https://github.com/dynamics365ninja/d365fo-cli";
    private const string Scripts = "https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main";

    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--check")]
        [System.ComponentModel.Description("npm installation: only report whether a newer version is published.")]
        public bool Check { get; init; }

        [CommandOption("--to <VERSION>")]
        [System.ComponentModel.Description("Version to move to, older or newer. Default: the latest release.")]
        public string? To { get; init; }
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex SemanticVersion();

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var to = settings.To?.TrimStart('v');
        if (to is not null && !SemanticVersion().IsMatch(to))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"'{settings.To}' is not a version.", "Pass --to 1.2.3."));

        var install = Installation.Detect(AppContext.BaseDirectory);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            updated = false,
            version = ProductVersion.Version,
            installKind = install.ToString().ToLowerInvariant(),
            howToUpdate = Instructions(install, to, OperatingSystem.IsWindows()),
            releases = $"{Repository}/releases",
        }));
    }

    /// <summary>The step that updates an installation of <paramref name="kind"/>.</summary>
    internal static string Instructions(InstallKind kind, string? to, bool windows)
    {
        var tag = to is null ? null : $"v{to}";
        return kind switch
        {
            InstallKind.Npm =>
                $"Close editors running d365fo-cli-mcp, run `d365fo daemon stop`, then `npm install -g {Installation.NpmPackage}@{to ?? "latest"}`.",
            InstallKind.Installer when windows =>
                (tag is null ? "" : $"Set $env:D365FO_CLI_VERSION = '{tag}', then run ")
                + (tag is null ? "Run " : "")
                + $"`irm {Scripts}/install.ps1 | iex`.",
            InstallKind.Installer =>
                $"Run `curl -fsSL {Scripts}/install.sh | {(tag is null ? "" : $"D365FO_CLI_VERSION={tag} ")}bash`.",
            InstallKind.Source =>
                $"This is a source checkout: `{(tag is null ? "git pull" : $"git fetch --tags && git checkout {tag}")}`, then rebuild.",
            _ =>
                $"Download the archive of {tag ?? "the latest release"} from {Repository}/releases and replace this folder with it.",
        };
    }
}
