using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds a custom D365FO business event class (extending <c>BusinessEventsBase</c>)
/// and its companion data contract class (implementing <c>BusinessEventsContract</c>).
/// Produces two separate <c>AxClass</c> XML files.
/// </summary>
public sealed class GenerateBusinessEventCommand : Command<GenerateBusinessEventCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Business event class name (e.g. MyOrderCreatedBusinessEvent).")]
        public string Name { get; init; } = "";

        [CommandOption("--contract-name <NAME>")]
        [System.ComponentModel.Description("Contract class name. Defaults to <NAME>Contract.")]
        public string? ContractName { get; init; }

        [CommandOption("--payload <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<type>. Adds a parmXxx() accessor on the contract. Example: --payload AccountNum:AccountNum")]
        public string[] Payload { get; init; } = Array.Empty<string>();

        [CommandOption("--category <MODULE>")]
        // Literal brackets must be doubled — Spectre parses a bare '[' in a description
        // as a style tag and `--help` throws instead of printing (GenerateHelpRenderTests).
        [System.ComponentModel.Description("ModuleAxapta value for the [[BusinessEvents]] attribute (Ledger, SalesOrder, HumanResource, FleetManagement, …). Defaults to NotApplicable.")]
        public string Category { get; init; } = BusinessEventScaffolder.DefaultModule;

        [CommandOption("--primary-table <TABLE>")]
        [System.ComponentModel.Description("Primary table used in newFromTable(). Optional.")]
        public string? PrimaryTable { get; init; }

        [CommandOption("--out-contract <PATH>")]
        [System.ComponentModel.Description("Output path for the contract class. Defaults to sibling of --out.")]
        public string? OutContract { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Business event class name required."));

        var contractName = string.IsNullOrWhiteSpace(settings.ContractName)
            ? settings.Name + "Contract"
            : settings.ContractName!;

        // The attribute's fourth argument is a ModuleAxapta value, not free text. A
        // name outside the enum is a compile error on the AOS, so it fails here
        // instead — with the list, the way `generate form --pattern` does.
        var module = BusinessEventScaffolder.NormalizeModule(settings.Category);
        if (module is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                $"--category '{settings.Category}' is not a ModuleAxapta value.",
                "Valid values: " + string.Join(", ", BusinessEventScaffolder.ModuleAxaptaValues)));
        }

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        // Resolve output paths for both files.
        string? eventPath, contractPath;
        if (hasInstall && !hasOut)
        {
            eventPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, settings.Name, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;
            contractPath = string.IsNullOrWhiteSpace(settings.OutContract)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, contractName, settings.InstallTo!, out _)
                : settings.OutContract;
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(settings.Out!)!;
            eventPath    = settings.Out!;
            contractPath = settings.OutContract ?? System.IO.Path.Combine(dir, contractName + ".xml");
        }

        var payload = settings.Payload.Select(ParsePayload).ToList();

        var eventDoc    = BusinessEventScaffolder.EventClass(settings.Name, contractName, module, settings.PrimaryTable);
        var contractDoc = BusinessEventScaffolder.ContractClass(contractName, payload);

        // Grounding gate (issue #161). The driving table is the one claim this command makes
        // about the existing AOT, so it is what the gate proves.
        var gate = GenerateInstaller.Gate(
            settings, settings.Name, eventDoc,
            requiredSymbols: string.IsNullOrWhiteSpace(settings.PrimaryTable) ? null : new[] { settings.PrimaryTable! });
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        try
        {
            var eventResult    = GenerateInstaller.Write(gate, eventDoc, eventPath!, settings.Overwrite);
            var contractResult = GenerateInstaller.Write(gate, contractDoc, contractPath!, settings.Overwrite);

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind         = "BusinessEvent",
                name         = settings.Name,
                contractName,
                category     = module,
                primaryTable = settings.PrimaryTable,
                payloadCount = payload.Count,
                @event       = new { path = eventResult.Path,    bytes = eventResult.Bytes,    backup = eventResult.BackupPath },
                contract     = new { path = contractResult.Path, bytes = contractResult.Bytes, backup = contractResult.BackupPath },
                model        = settings.InstallTo,
                grounding    = gate.Grounding,
            }, gate.Warnings));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static PayloadSpec ParsePayload(string raw)
    {
        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        var name  = parts.Length > 0 ? parts[0] : raw;
        var type  = parts.Length > 1 ? parts[1] : "str";
        return new PayloadSpec(name, type);
    }
}
