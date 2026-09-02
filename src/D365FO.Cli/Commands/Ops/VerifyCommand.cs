using D365FO.Core;
using D365FO.Core.Journal;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

/// <summary>
/// <c>d365fo verify</c> — does the model on disk and its Visual Studio project agree?
/// </summary>
/// <remarks>
/// <c>generate --verify</c> answers a different question: it reads one freshly written artefact
/// back through the metadata provider. This one asks about the model as a whole — every object
/// file present, every project entry pointing at a file that exists. An object the project does
/// not list is not compiled, and nothing anywhere says so.
/// </remarks>
public sealed class VerifyCommand : Command<VerifyCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[MODEL]")]
        [System.ComponentModel.Description("Model name (resolved under the configured packages paths), or a path to the inner <Model>/<Model> folder.")]
        public string? Model { get; init; }

        [CommandOption("--path <PATH>")]
        [System.ComponentModel.Description("Model folder to check, when the name cannot be resolved.")]
        public string? Path { get; init; }

        [CommandOption("--expect <NAME>")]
        [System.ComponentModel.Description("Repeatable: an object you believe you created — AxTable/ConFleetVehicle or plain ConFleetVehicle. Each is answered by name.")]
        public string[] Expect { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var folder = settings.Path;
        if (string.IsNullOrWhiteSpace(folder))
        {
            if (string.IsNullOrWhiteSpace(settings.Model))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    "A model name or --path is required."));

            // A path given as the argument is used as one; otherwise the name is resolved the
            // same way `index sync` resolves it, so both agree about where a model lives.
            folder = Directory.Exists(settings.Model!)
                ? settings.Model!
                : ResolveModelFolder(settings.Model!);

            if (folder is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("MODEL_NOT_FOUND",
                    $"No model directory called '{settings.Model}' under any configured packages path.",
                    "Set D365FO_PACKAGES_PATH (and D365FO_CUSTOM_PACKAGES_PATH for custom-model roots), or pass --path."));
        }

        var result = ProjectVerifier.Verify(folder!, settings.Expect);
        var rc = RenderHelpers.Render(kind, result);

        // A disagreement is a finding, not a failure of the command — but it must not read as
        // "all good" to a script.
        if (rc != 0) return rc;
        return result.Ok && result.Data is not null && HasIssues(result.Data) ? 2 : 0;
    }

    private static string? ResolveModelFolder(string model)
    {
        var cfg = D365FoSettings.FromEnvironment();
        var roots = cfg.CustomPackagesPaths.Concat(new[] { cfg.PackagesPath });
        return roots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
            .SelectMany(r => D365FO.Core.Index.IndexSync.EnumerateModelDirs(r!, model))
            .FirstOrDefault();
    }

    private static bool HasIssues(object data)
    {
        var prop = data.GetType().GetProperty("issueCount");
        return prop?.GetValue(data) is int count && count > 0;
    }
}
