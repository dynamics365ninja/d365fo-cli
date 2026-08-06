using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds the DataContract + Service + Controller triplet for a SysOperation
/// batch / service operation. Produces three separate AxClass XML files.
/// </summary>
public sealed class GenerateSysOperationCommand : Command<GenerateSysOperationCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Base name used to derive default class names (e.g. MyBatch → MyBatchContract, MyBatchService, MyBatchController).")]
        public string Name { get; init; } = "";

        [CommandOption("--contract-name <NAME>")]
        [System.ComponentModel.Description("DataContract class name. Defaults to <NAME>Contract.")]
        public string? ContractName { get; init; }

        [CommandOption("--service-name <NAME>")]
        [System.ComponentModel.Description("Service class name. Defaults to <NAME>Service.")]
        public string? ServiceName { get; init; }

        [CommandOption("--controller-name <NAME>")]
        [System.ComponentModel.Description("Controller class name. Defaults to <NAME>Controller.")]
        public string? ControllerName { get; init; }

        [CommandOption("--param <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<type>. Adds a parmXxx() accessor on the DataContract. Example: --param AccountNum:AccountNum")]
        public string[] Params { get; init; } = Array.Empty<string>();

        [CommandOption("--execution-mode <MODE>")]
        [System.ComponentModel.Description("Synchronous (default) | Asynchronous | ScheduledBatch.")]
        public string ExecutionMode { get; init; } = "Synchronous";

        [CommandOption("--service-method <NAME>")]
        [System.ComponentModel.Description("Name of the service entry-point method. Defaults to 'process'.")]
        public string ServiceMethod { get; init; } = "process";

        [CommandOption("--out-contract <PATH>")]
        [System.ComponentModel.Description("Output path for the DataContract class. Defaults to sibling of --out.")]
        public string? OutContract { get; init; }

        [CommandOption("--out-service <PATH>")]
        [System.ComponentModel.Description("Output path for the Service class. Defaults to sibling of --out.")]
        public string? OutService { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Base name required."));

        var contractName   = string.IsNullOrWhiteSpace(settings.ContractName)   ? settings.Name + "Contract"   : settings.ContractName!;
        var serviceName    = string.IsNullOrWhiteSpace(settings.ServiceName)     ? settings.Name + "Service"    : settings.ServiceName!;
        var controllerName = string.IsNullOrWhiteSpace(settings.ControllerName) ? settings.Name + "Controller" : settings.ControllerName!;

        if (!TryParseExecutionMode(settings.ExecutionMode, out var mode))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --execution-mode '{settings.ExecutionMode}'. Expected Synchronous | Asynchronous | ScheduledBatch."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        // Resolve all three output paths.
        string? controllerPath, contractPath, servicePath;
        if (hasInstall && !hasOut)
        {
            controllerPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, controllerName, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;
            contractPath = string.IsNullOrWhiteSpace(settings.OutContract)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, contractName, settings.InstallTo!, out var f2)
                : settings.OutContract;
            if (hasInstall && string.IsNullOrWhiteSpace(settings.OutContract) && contractPath is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Could not resolve contract path."));
            servicePath = string.IsNullOrWhiteSpace(settings.OutService)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, serviceName, settings.InstallTo!, out var f3)
                : settings.OutService;
            if (hasInstall && string.IsNullOrWhiteSpace(settings.OutService) && servicePath is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Could not resolve service path."));
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(settings.Out!)!;
            controllerPath = settings.Out!;
            contractPath   = settings.OutContract  ?? System.IO.Path.Combine(dir, contractName  + ".xml");
            servicePath    = settings.OutService    ?? System.IO.Path.Combine(dir, serviceName   + ".xml");
        }

        var parms = settings.Params.Select(ParseParam).ToList();

        var serviceDoc = SysOperationScaffolder.Service(serviceName, contractName, settings.ServiceMethod, parms);

        // Grounding gate (issue #161) — the service class is where this command's X++ lives.
        var gate = GenerateInstaller.Gate(settings, settings.Name, serviceDoc);
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        try
        {
            var contractResult    = GenerateInstaller.Write(gate,
                SysOperationScaffolder.Contract(contractName, parms), contractPath!, settings.Overwrite);
            var serviceResult     = GenerateInstaller.Write(gate, serviceDoc, servicePath!, settings.Overwrite);
            var controllerResult  = GenerateInstaller.Write(gate,
                SysOperationScaffolder.Controller(controllerName, serviceName, settings.ServiceMethod, mode), controllerPath!, settings.Overwrite);

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind           = "SysOperation",
                name           = settings.Name,
                contractName,
                serviceName,
                controllerName,
                serviceMethod  = settings.ServiceMethod,
                executionMode  = mode.ToString(),
                paramCount     = parms.Count,
                contract       = new { path = contractResult.Path,   bytes = contractResult.Bytes,   backup = contractResult.BackupPath },
                service        = new { path = serviceResult.Path,    bytes = serviceResult.Bytes,    backup = serviceResult.BackupPath },
                controller     = new { path = controllerResult.Path, bytes = controllerResult.Bytes, backup = controllerResult.BackupPath },
                model          = settings.InstallTo,
                grounding      = gate.Grounding,
            }, gate.Warnings));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static SysOperationParamSpec ParseParam(string raw)
    {
        var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
        var name  = parts.Length > 0 ? parts[0] : raw;
        var type  = parts.Length > 1 ? parts[1] : "str";
        return new SysOperationParamSpec(name, type);
    }

    private static bool TryParseExecutionMode(string raw, out SysOperationExecutionMode mode)
    {
        mode = raw.ToLowerInvariant() switch
        {
            "asynchronous" or "async"  => SysOperationExecutionMode.Asynchronous,
            "scheduledbatch" or "batch" => SysOperationExecutionMode.ScheduledBatch,
            "synchronous" or "sync" or "" => SysOperationExecutionMode.Synchronous,
            _ => (SysOperationExecutionMode)(-1),
        };
        return (int)mode >= 0;
    }
}
