using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds an <c>AxMenuItemDisplay</c>, <c>AxMenuItemAction</c>, or
/// <c>AxMenuItemOutput</c> AOT object.
/// </summary>
public sealed class GenerateMenuItemCommand : Command<GenerateMenuItemCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Menu item name.")]
        public string Name { get; init; } = "";

        [CommandOption("--kind <KIND>")]
        [System.ComponentModel.Description("Display (default) | Action | Output.")]
        public string Kind { get; init; } = "Display";

        [CommandOption("--object <NAME>")]
        [System.ComponentModel.Description("Target object name (form, class, or report).")]
        public string? ObjectName { get; init; }

        [CommandOption("--object-type <TYPE>")]
        [System.ComponentModel.Description("Form (default) | Class | Report | Query.")]
        public string ObjectType { get; init; } = "Form";

        [CommandOption("--label <TEXT>")]
        [System.ComponentModel.Description("Label text or @File:Key label reference.")]
        public string? Label { get; init; }

        [CommandOption("--enum-type <ENUM>")]
        [System.ComponentModel.Description("EnumTypeParameter: enum whose value is passed to the target.")]
        public string? EnumTypeParameter { get; init; }

        [CommandOption("--enum-value <VALUE>")]
        [System.ComponentModel.Description("EnumParameter: the value passed. Requires --enum-type.")]
        public string? EnumParameter { get; init; }

        [CommandOption("--parameters <TEXT>")]
        [System.ComponentModel.Description("Parameters string handed to the target object.")]
        public string? Parameters { get; init; }

        [CommandOption("--config-key <KEY>")]
        [System.ComponentModel.Description("Configuration key gating this menu item.")]
        public string? ConfigurationKey { get; init; }

        [CommandOption("--query <NAME>")]
        [System.ComponentModel.Description("Query the target is opened over.")]
        public string? Query { get; init; }

        [CommandOption("--needed-permission <LEVEL>")]
        [System.ComponentModel.Description("Read (default when omitted: unset) | Update | Create | Correct | Delete. Expanded into the item's *Permissions flags.")]
        public string? NeededPermission { get; init; }

        [CommandOption("--linked-permission-object <NAME>")]
        [System.ComponentModel.Description("Object whose permissions this item inherits.")]
        public string? LinkedPermissionObject { get; init; }

        [CommandOption("--linked-permission-type <TYPE>")]
        [System.ComponentModel.Description("Kind of --linked-permission-object, e.g. MenuItemDisplay.")]
        public string? LinkedPermissionType { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Menu item name required."));
        if (string.IsNullOrWhiteSpace(settings.ObjectName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--object <NAME> required."));

        if (!TryParseKind(settings.Kind, out var menuKind))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --kind '{settings.Kind}'. Expected Display | Action | Output."));

        if (!TryParseObjectType(settings.ObjectType, out var objType))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --object-type '{settings.ObjectType}'. Expected Form | Class | Report | Query."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var axSubfolder = MenuItemScaffolder.AxSubfolder(menuKind);
        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, axSubfolder, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        if (!string.IsNullOrWhiteSpace(settings.EnumParameter) && string.IsNullOrWhiteSpace(settings.EnumTypeParameter))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--enum-value needs --enum-type: without the enum's name the value identifies nothing and is dropped."));

        var doc = MenuItemScaffolder.MenuItem(
            menuKind, settings.Name, settings.ObjectName!, objType, settings.Label,
            settings.EnumTypeParameter, settings.EnumParameter, settings.Parameters,
            settings.ConfigurationKey, settings.Query, settings.NeededPermission,
            settings.LinkedPermissionObject, settings.LinkedPermissionType);
        try
        {
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind        = axSubfolder,
                name        = settings.Name,
                menuKind    = menuKind.ToString(),
                objectName  = settings.ObjectName,
                objectType  = objType.ToString(),
                label       = settings.Label,
                enumType    = settings.EnumTypeParameter,
                enumValue   = settings.EnumParameter,
                parameters  = settings.Parameters,
                query       = settings.Query,
                configKey   = settings.ConfigurationKey,
                permission  = settings.NeededPermission,
                path        = res.Path,
                bytes       = res.Bytes,
                backup      = res.BackupPath,
                model       = settings.InstallTo,
            }, gate.Warnings));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static bool TryParseKind(string raw, out MenuItemKind menuKind)
    {
        menuKind = raw.ToLowerInvariant() switch
        {
            "action"  => MenuItemKind.Action,
            "output"  => MenuItemKind.Output,
            "display" or "" => MenuItemKind.Display,
            _ => (MenuItemKind)(-1),
        };
        return (int)menuKind >= 0;
    }

    private static bool TryParseObjectType(string raw, out MenuItemObjectType objType)
    {
        objType = raw.ToLowerInvariant() switch
        {
            "class"          => MenuItemObjectType.Class,
            "report" or "ssrsreport" => MenuItemObjectType.Report,
            "query"          => MenuItemObjectType.Query,
            "form" or ""     => MenuItemObjectType.Form,
            _ => (MenuItemObjectType)(-1),
        };
        return (int)objType >= 0;
    }
}
