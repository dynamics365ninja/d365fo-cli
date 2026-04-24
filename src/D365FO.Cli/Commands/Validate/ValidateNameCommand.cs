using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Validate;

/// <summary>
/// Static naming-rule check. Mirrors upstream MCP tool
/// <c>validate_object_naming</c> — compile-free gate for scaffold names
/// before they hit the workspace.
/// </summary>
public sealed class ValidateNameCommand : Command<ValidateNameCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Object kind: Table, Class, Edt, Enum, Form, View, Query, Report, Entity, Service, MenuItem, TableExtension, FormExtension, Coc.")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<NAME>")]
        public string Name { get; init; } = "";

        [CommandOption("--prefix <PREFIX>")]
        [System.ComponentModel.Description("Required publisher prefix (e.g. Contoso_). Reports MISSING_PUBLISHER_PREFIX if missing.")]
        public string? Prefix { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var violations = ObjectNamingRules.Validate(settings.Kind, settings.Name, settings.Prefix);
        var hasError = violations.Any(v => v.Severity == "error");
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            objectKind = settings.Kind,
            name = settings.Name,
            prefix = settings.Prefix,
            ok = !hasError,
            count = violations.Count,
            violations = violations.Select(v => new { code = v.Code, severity = v.Severity, message = v.Message }),
        }));
    }
}
