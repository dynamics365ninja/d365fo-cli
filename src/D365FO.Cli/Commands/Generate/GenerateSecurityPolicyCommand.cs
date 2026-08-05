using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds an <c>AxSecurityPolicy</c> (XDS / extensible data security policy)
/// that limits access to rows in the constrained table based on a policy query.
/// </summary>
public sealed class GenerateSecurityPolicyCommand : Command<GenerateSecurityPolicyCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Security policy name (e.g. MyTablePolicy).")]
        public string Name { get; init; } = "";

        [CommandOption("--constrained-table <TABLE>")]
        [System.ComponentModel.Description("The table whose rows this policy restricts. Required.")]
        public string? ConstrainedTable { get; init; }

        [CommandOption("--policy-query <QUERY>")]
        [System.ComponentModel.Description("AOT query name that defines the allowed rows. Required.")]
        public string? PolicyQuery { get; init; }

        [CommandOption("--operation <OP>")]
        [System.ComponentModel.Description("Policy operation scope: Select (default) | All.")]
        public string Operation { get; init; } = "Select";

        [CommandOption("--context-type <TYPE>")]
        [System.ComponentModel.Description("Context type: RoleName (default) | ContextString.")]
        public string ContextType { get; init; } = "RoleName";

        [CommandOption("--context-value <VALUE>")]
        [System.ComponentModel.Description("Context value (role name or context string). Optional.")]
        public string? ContextValue { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Policy name required."));
        if (string.IsNullOrWhiteSpace(settings.ConstrainedTable))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--constrained-table is required."));
        if (string.IsNullOrWhiteSpace(settings.PolicyQuery))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--policy-query is required."));

        if (!TryParseOperation(settings.Operation, out var operation))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --operation '{settings.Operation}'. Expected Select | All."));

        if (!TryParseContextType(settings.ContextType, out var contextType))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --context-type '{settings.ContextType}'. Expected RoleName | ContextString."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.SecurityPolicy, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        try
        {
            var doc = SecurityPolicyScaffolder.Policy(
                settings.Name,
                settings.ConstrainedTable!,
                settings.PolicyQuery!,
                operation,
                contextType,
                settings.ContextValue);

            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind             = "AxSecurityPolicy",
                name             = settings.Name,
                constrainedTable = settings.ConstrainedTable,
                policyQuery      = settings.PolicyQuery,
                operation        = operation.ToString(),
                contextType      = contextType.ToString(),
                contextValue     = settings.ContextValue,
                path             = res.Path,
                bytes            = res.Bytes,
                backup           = res.BackupPath,
                model            = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static bool TryParseOperation(string raw, out PolicyOperation op)
    {
        op = raw.ToLowerInvariant() switch
        {
            "all"    => PolicyOperation.All,
            "select" => PolicyOperation.Select,
            _        => (PolicyOperation)(-1),
        };
        return (int)op >= 0;
    }

    private static bool TryParseContextType(string raw, out PolicyContextType ct)
    {
        ct = raw.ToLowerInvariant() switch
        {
            "rolename"      => PolicyContextType.RoleName,
            "contextstring" => PolicyContextType.ContextString,
            _               => (PolicyContextType)(-1),
        };
        return (int)ct >= 0;
    }
}
