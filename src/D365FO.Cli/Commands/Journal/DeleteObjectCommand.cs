using D365FO.Cli.Commands.Get;
using D365FO.Core;
using D365FO.Core.Journal;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;
using D365FO.Core.Bridge;

namespace D365FO.Cli.Commands.Journal;

/// <summary>
/// Delete an AOT object — either via the live metadata provider (bridge <c>deleteObject</c>,
/// <c>--install-to</c>) or a direct on-disk file delete (<c>--path</c>). Added alongside the
/// modification journal (issue #113) so the "delete → undo restores the file" acceptance
/// criterion is reachable through the CLI, not just through the journal/undo engine's unit
/// tests: both paths capture the exact pre-image before removing anything, so `d365fo undo`
/// can recreate the object afterwards.
/// </summary>
public sealed class DeleteObjectCommand : Command<DeleteObjectCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("AOT kind: class | table | edt | enum | form.")]
        public string Kind { get; init; } = "";

        [CommandOption("--name <NAME>")]
        [System.ComponentModel.Description("Object name.")]
        public string Name { get; init; } = "";

        [CommandOption("--install-to <MODEL>")]
        [System.ComponentModel.Description("Delete via the metadata bridge (D365FO_BRIDGE_ENABLED=1). Mutually exclusive with --path.")]
        public string? InstallTo { get; init; }

        [CommandOption("--path <PATH>")]
        [System.ComponentModel.Description("Delete the on-disk XML file directly at this path. Mutually exclusive with --install-to.")]
        public string? Path { get; init; }

        [CommandOption("--model <MODEL>")]
        [System.ComponentModel.Description("Model name for --path deletes (used only for the optional .rnrproj bookkeeping delta; not required).")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            AotObjectDeleter.Delete(settings.Kind, settings.Name, settings.InstallTo, settings.Path, settings.Model));
}
