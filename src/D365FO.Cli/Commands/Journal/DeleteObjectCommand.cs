using D365FO.Cli.Commands.Get;
using D365FO.Core;
using D365FO.Core.Journal;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

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

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrWhiteSpace(settings.Kind))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--kind <KIND> is required."));
        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--name <NAME> is required."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasPath = !string.IsNullOrWhiteSpace(settings.Path);
        if (hasInstall == hasPath)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "Exactly one of --install-to <MODEL> or --path <PATH> is required."));

        if (hasInstall)
        {
            var axKind = settings.Kind.Trim().ToLowerInvariant();
            // Capture the exact pre-image BEFORE deleting so `d365fo undo` can recreate it.
            var preImage = BridgeGate.TryReadObjectXml(axKind, settings.Name);
            if (preImage is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed,
                    $"Could not read '{settings.Name}' via the bridge before deleting — refusing to delete without a pre-image to undo with.",
                    "Ensure D365FO_BRIDGE_ENABLED=1 and the object exists in the given model."));

            var (ok, err) = BridgeGate.TryDeleteObject(axKind, settings.Name, settings.InstallTo!);
            if (!ok)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.DeleteFailed,
                    $"Could not delete {axKind} '{settings.Name}' in model '{settings.InstallTo}' via the metadata bridge: {err}"));

            RecordBridgeDelete(axKind, settings.Name, settings.InstallTo!, preImage);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = axKind,
                name = settings.Name,
                model = settings.InstallTo,
                source = "bridge",
            }, new List<string> { "Index not auto-refreshed — run `d365fo index refresh --model " + settings.InstallTo + "`." }));
        }

        try
        {
            var res = ScaffoldFileWriter.Delete(settings.Path!, settings.Model);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = settings.Kind,
                name = settings.Name,
                path = res.Path,
                source = "scaffold",
            }, new List<string> { "Index not auto-refreshed — run `d365fo index refresh`." }));
        }
        catch (FileNotFoundException)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, $"File not found: {settings.Path}"));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.DeleteFailed, ex.Message));
        }
    }

    private static void RecordBridgeDelete(string axKind, string name, string model, string preImage)
    {
        try
        {
            ModificationJournal.ForIndex().Append(new JournalEntry(
                Id: Guid.NewGuid().ToString("N"),
                TimestampUtc: DateTimeOffset.UtcNow,
                Command: "delete --install-to",
                TargetType: JournalTargetType.AotObject,
                Kind: axKind,
                ObjectName: name,
                SecondaryKey: null,
                Model: model,
                Operation: JournalOperation.Delete,
                WritePath: JournalWritePath.Bridge,
                TargetPath: null,
                PreImage: preImage,
                IsTombstone: false,
                RnrProjDelta: null));
        }
        catch { /* best-effort */ }
    }
}
