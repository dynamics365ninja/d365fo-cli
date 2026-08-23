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

        [CommandOption("--constrained <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <Table>[[/<Child>[[/<Grandchild>]]]][[:unconstrained]]. Tables the policy reaches beyond the primary one; '/' nests, ':unconstrained' marks a table the policy only traverses. Example: --constrained FmVehicleService/FmVehicleServiceLine")]
        public string[] Constrained { get; init; } = Array.Empty<string>();

        [CommandOption("--label <TEXT>")]
        [System.ComponentModel.Description("Policy label.")]
        public string? Label { get; init; }

        [CommandOption("--use-not-exist-join")]
        [System.ComponentModel.Description("Emit UseNotExistJoin=Yes — the policy excludes rows the query matches instead of restricting to them.")]
        public bool UseNotExistJoin { get; init; }
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
                settings.ContextValue,
                constrainedTables: ParseConstrained(settings.Constrained),
                label: settings.Label,
                useNotExistJoin: settings.UseNotExistJoin);

            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);

            return GenerateInstaller.Done(kind, gate, settings, new
            {
                kind             = "AxSecurityPolicy",
                name             = settings.Name,
                constrainedTable = settings.ConstrainedTable,
                policyQuery      = settings.PolicyQuery,
                operation        = operation.ToString(),
                contextType      = contextType.ToString(),
                contextValue     = settings.ContextValue,
                constrainedTables = settings.Constrained,
                path             = res.Path,
                bytes            = res.Bytes,
                backup           = res.BackupPath,
                model            = settings.InstallTo,
            });
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    /// <summary>
    /// Parse <c>--constrained &lt;Table&gt;[/&lt;Child&gt;…][:unconstrained]</c> into the
    /// nested <c>ConstrainedTables</c> tree.
    /// </summary>
    /// <remarks>
    /// A path rather than a flat list because <c>AxSecurityPolicyConstrainedEntity</c> nests: a
    /// policy reaches a line table <em>through</em> its header, and flattening the two would
    /// claim the line table is joined to the primary table directly. The <c>:unconstrained</c>
    /// marker applies to the last segment — the common shape is traversing a header to
    /// constrain its lines.
    /// </remarks>
    private static List<SecurityPolicyScaffolder.ConstrainedEntity> ParseConstrained(string[] raw)
    {
        var roots = new List<SecurityPolicyScaffolder.ConstrainedEntity>();

        foreach (var spec in raw.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var body = spec;
            var constrained = true;
            var marker = spec.LastIndexOf(':');
            if (marker > 0 && spec[(marker + 1)..].Trim().Equals("unconstrained", StringComparison.OrdinalIgnoreCase))
            {
                body = spec[..marker];
                constrained = false;
            }

            var segments = body.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0) continue;

            // Build from the leaf up: only the leaf carries the caller's constrained flag, the
            // ancestors are the path taken to reach it.
            SecurityPolicyScaffolder.ConstrainedEntity? node = null;
            for (var i = segments.Length - 1; i >= 0; i--)
            {
                node = new SecurityPolicyScaffolder.ConstrainedEntity(
                    segments[i],
                    Constrained: node is null ? constrained : true,
                    Children: node is null ? null : [node]);
            }
            roots.Add(node!);
        }

        return roots;
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
