using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds the D365FO number-sequence integration pattern:
/// a CoC extension on the module's NumberSeqApplicationModule class, an EDT,
/// and a form event-handler that wires up auto-generation in the UI.
/// </summary>
public sealed class GenerateNumberSequenceCommand : Command<GenerateNumberSequenceCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<MODULE_NAME>")]
        [System.ComponentModel.Description("Existing number-sequence module, e.g. 'Customer' or 'Asset'. The CoC target will be NumberSeqModule<MODULE_NAME>.")]
        public string ModuleName { get; init; } = "";

        [CommandOption("--edt <NAME>")]
        [System.ComponentModel.Description("EDT name for the sequence field. Defaults to <MODULE_NAME>Num.")]
        public string? EdtName { get; init; }

        [CommandOption("--edt-label <TEXT>")]
        [System.ComponentModel.Description("Label for the EDT.")]
        public string? EdtLabel { get; init; }

        [CommandOption("--scope <SCOPE>")]
        [System.ComponentModel.Description("Company (default) | Shared.")]
        public string Scope { get; init; } = "Company";

        [CommandOption("--table <NAME>")]
        [System.ComponentModel.Description("Table name for the form event-handler. When omitted, the form handler file is not generated.")]
        public string? TableName { get; init; }

        [CommandOption("--out-edt <PATH>")]
        [System.ComponentModel.Description("Output path for the EDT XML. Defaults to sibling of --out.")]
        public string? OutEdt { get; init; }

        [CommandOption("--out-handler <PATH>")]
        [System.ComponentModel.Description("Output path for the form event-handler class. Defaults to sibling of --out when --table is supplied.")]
        public string? OutHandler { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.ModuleName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Module name required."));

        var edtName = string.IsNullOrWhiteSpace(settings.EdtName)
            ? settings.ModuleName + "Num"
            : settings.EdtName!;

        if (!TryParseScope(settings.Scope, out var scope))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --scope '{settings.Scope}'. Expected Company | Shared."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var extensionName = NumberSequenceScaffolder.ModuleClassName(settings.ModuleName) + "_Extension";
        var handlerClass  = string.IsNullOrWhiteSpace(settings.TableName)
            ? null
            : settings.TableName + "_NumberSeqHandler";

        // Resolve output paths.
        string? modulePath, edtPath, handlerPath;
        if (hasInstall && !hasOut)
        {
            modulePath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, extensionName, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;
            edtPath = string.IsNullOrWhiteSpace(settings.OutEdt)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.Edt, edtName, settings.InstallTo!, out _)
                : settings.OutEdt;
            handlerPath = handlerClass is null ? null
                : string.IsNullOrWhiteSpace(settings.OutHandler)
                    ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, handlerClass, settings.InstallTo!, out _)
                    : settings.OutHandler;
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(settings.Out!)!;
            modulePath  = settings.Out!;
            edtPath     = settings.OutEdt     ?? System.IO.Path.Combine(dir, edtName + ".xml");
            handlerPath = handlerClass is null ? null
                : settings.OutHandler ?? System.IO.Path.Combine(dir, handlerClass + ".xml");
        }

        var moduleDoc = NumberSequenceScaffolder.ModuleExtension(settings.ModuleName, edtName, scope);
        var edtDoc    = NumberSequenceScaffolder.Edt(edtName, settings.ModuleName, scope, settings.EdtLabel);

        // Grounding gate (issue #161). The module extension names an existing module class and
        // the optional form handler an existing table — both are claims about the real AOT.
        var gate = GenerateInstaller.Gate(
            settings, edtName, moduleDoc,
            requiredSymbols: new[] { settings.ModuleName, settings.TableName }
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!));
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        try
        {
            var moduleResult = GenerateInstaller.Write(gate, moduleDoc, modulePath!, settings.Overwrite);
            var edtResult    = GenerateInstaller.Write(gate, edtDoc,    edtPath!,    settings.Overwrite);

            ScaffoldFileWriter.WriteResult? handlerResult = null;
            if (handlerClass is not null && handlerPath is not null)
            {
                handlerResult = GenerateInstaller.Write(gate,
                    NumberSequenceScaffolder.FormHandler(settings.TableName!, edtName, handlerClass, settings.ModuleName),
                    handlerPath, settings.Overwrite);
            }

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind         = "NumberSequence",
                moduleName   = settings.ModuleName,
                edtName,
                scope        = scope.ToString(),
                module       = new { path = moduleResult.Path,  bytes = moduleResult.Bytes,  backup = moduleResult.BackupPath },
                edt          = new { path = edtResult.Path,     bytes = edtResult.Bytes,     backup = edtResult.BackupPath },
                handler      = handlerResult is null ? null : new { path = handlerResult.Path, bytes = handlerResult.Bytes, backup = handlerResult.BackupPath },
                model        = settings.InstallTo,
                grounding    = gate.Grounding,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static bool TryParseScope(string raw, out NumberSequenceScope scope)
    {
        scope = raw.ToLowerInvariant() switch
        {
            "shared" => NumberSequenceScope.Shared,
            "company" or "" => NumberSequenceScope.Company,
            _ => (NumberSequenceScope)(-1),
        };
        return (int)scope >= 0;
    }
}
