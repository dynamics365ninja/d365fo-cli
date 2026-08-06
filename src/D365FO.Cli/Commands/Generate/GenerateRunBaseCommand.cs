using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds a legacy <c>RunBase</c> / <c>RunBaseBatch</c> class for D365FO codebases
/// that have not yet migrated to SysOperation. Includes <c>dialog()</c>,
/// <c>getFromDialog()</c>, <c>pack()</c>, <c>unpack()</c>, <c>run()</c>, <c>main()</c>,
/// and optionally <c>canGoBatch()</c>.
/// </summary>
public sealed class GenerateRunBaseCommand : Command<GenerateRunBaseCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Class name (e.g. MyBatchRunBase).")]
        public string Name { get; init; } = "";

        [CommandOption("--batch")]
        [System.ComponentModel.Description("Extend RunBaseBatch and emit canGoBatch() { return true; }.")]
        public bool Batch { get; init; }

        [CommandOption("--dialog-param <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<edt>. Adds a dialog field and pack/unpack entry. Example: --dialog-param FromDate:TransDate")]
        public string[] DialogParams { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Class name required."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var dialogParams = settings.DialogParams.Select(ParseDialogParam).ToList();

        try
        {
            var doc = RunBaseScaffolder.RunBaseClass(settings.Name, settings.Batch, dialogParams);
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind            = "RunBase",
                name            = settings.Name,
                baseClass       = settings.Batch ? "RunBaseBatch" : "RunBase",
                isBatch         = settings.Batch,
                dialogParamCount = dialogParams.Count,
                dialogParams    = dialogParams.Select(p => new { p.Name, p.Edt }).ToList(),
                path            = res.Path,
                bytes           = res.Bytes,
                backup          = res.BackupPath,
                model           = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static DialogParamSpec ParseDialogParam(string raw)
    {
        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        var name  = parts.Length > 0 ? parts[0] : raw;
        var edt   = parts.Length > 1 ? parts[1] : "Name";
        return new DialogParamSpec(name, edt);
    }
}
