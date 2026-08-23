using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds the D365FO custom SOAP service pattern: an <c>AxClass</c> service class,
/// an <c>AxService</c> XML, and an <c>AxServiceGroup</c> XML. Produces three files.
/// </summary>
public sealed class GenerateCustomServiceCommand : Command<GenerateCustomServiceCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Service name used to derive AxService name and default class/group names.")]
        public string Name { get; init; } = "";

        [CommandOption("--class-name <NAME>")]
        [System.ComponentModel.Description("Service class name. Defaults to <NAME>Service.")]
        public string? ClassName { get; init; }

        [CommandOption("--external-name <NAME>")]
        [System.ComponentModel.Description("Name the service is published under. Defaults to <NAME>; it cannot be empty, the metadata provider rejects the service.")]
        public string? ExternalName { get; init; }

        [CommandOption("--group-name <NAME>")]
        [System.ComponentModel.Description("Service group name. Defaults to <NAME>Group.")]
        public string? GroupName { get; init; }

        [CommandOption("--operation <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<returnType>. Defaults to 'process:void'.")]
        public string[] Operations { get; init; } = Array.Empty<string>();

        [CommandOption("--contract-param <SPEC>")]
        [System.ComponentModel.Description("Contract parameter for all operations. Format: <ContractClass>. Applied as the sole parameter type on all generated methods.")]
        public string? ContractParam { get; init; }

        [CommandOption("--out-class <PATH>")]
        [System.ComponentModel.Description("Output path for the service class XML. Defaults to sibling of --out.")]
        public string? OutClass { get; init; }

        [CommandOption("--out-service <PATH>")]
        [System.ComponentModel.Description("Output path for the AxService XML. Defaults to sibling of --out.")]
        public string? OutService { get; init; }

        [CommandOption("--out-group <PATH>")]
        [System.ComponentModel.Description("Output path for the AxServiceGroup XML. Defaults to sibling of --out.")]
        public string? OutGroup { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Service name required."));

        // The suffix is appended unconditionally, so "ConFmVehicleQueryService" yields
        // class "ConFmVehicleQueryServiceService". Ugly, and the shipped services do
        // name their class after themselves — but dropping the suffix makes the class
        // and the service collide on one path under a flat --out, and that trade is
        // worse than the stutter. Pass --class-name to choose the name yourself.
        var className = string.IsNullOrWhiteSpace(settings.ClassName) ? settings.Name + "Service" : settings.ClassName!;
        var groupName = string.IsNullOrWhiteSpace(settings.GroupName) ? settings.Name + "Group"   : settings.GroupName!;

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        // Resolve the three output paths.
        string? servicePath, classPath, groupPath;
        if (hasInstall && !hasOut)
        {
            servicePath = GenerateInstaller.ResolveInstallPath(kind, Folders.Service, settings.Name, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;
            classPath = string.IsNullOrWhiteSpace(settings.OutClass)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, className, settings.InstallTo!, out _)
                : settings.OutClass;
            groupPath = string.IsNullOrWhiteSpace(settings.OutGroup)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.ServiceGroup, groupName, settings.InstallTo!, out _)
                : settings.OutGroup;
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(settings.Out!)!;
            servicePath = settings.Out!;
            classPath   = settings.OutClass   ?? System.IO.Path.Combine(dir, className   + ".xml");
            groupPath   = settings.OutGroup   ?? System.IO.Path.Combine(dir, groupName   + ".xml");
        }

        var ops = ParseOperations(settings.Operations, settings.ContractParam);

        var classDoc   = CustomServiceScaffolder.ServiceClass(className, ops);
        var serviceDoc = CustomServiceScaffolder.ServiceXml(settings.Name, className, ops, settings.ExternalName);
        var groupDoc   = CustomServiceScaffolder.ServiceGroupXml(groupName, settings.Name);

        // Grounding gate (issue #161) — the service class carries the X++ the gate resolves.
        var gate = GenerateInstaller.Gate(settings, settings.Name, classDoc);
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        try
        {
            var classResult   = GenerateInstaller.Write(gate, classDoc,   classPath!,   settings.Overwrite);
            var serviceResult = GenerateInstaller.Write(gate, serviceDoc, servicePath!, settings.Overwrite);
            var groupResult   = GenerateInstaller.Write(gate, groupDoc,   groupPath!,   settings.Overwrite);

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind           = "CustomService",
                name           = settings.Name,
                className,
                groupName,
                operationCount = ops.Count,
                operations     = ops.Select(o => new { o.Name, o.ReturnType, o.ContractParam }).ToList(),
                serviceClass   = new { path = classResult.Path,   bytes = classResult.Bytes,   backup = classResult.BackupPath },
                service        = new { path = serviceResult.Path, bytes = serviceResult.Bytes, backup = serviceResult.BackupPath },
                serviceGroup   = new { path = groupResult.Path,   bytes = groupResult.Bytes,   backup = groupResult.BackupPath },
                model          = settings.InstallTo,
                grounding      = gate.Grounding,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static List<OperationSpec> ParseOperations(string[] raw, string? contractParam)
    {
        if (raw.Length == 0)
            return new List<OperationSpec> { new OperationSpec("process", "void", contractParam) };

        return raw.Select(s =>
        {
            var parts      = s.Split(':', 2, StringSplitOptions.TrimEntries);
            var opName     = parts.Length > 0 ? parts[0] : s;
            var returnType = parts.Length > 1 ? parts[1] : "void";
            return new OperationSpec(opName, returnType, contractParam);
        }).ToList();
    }
}
