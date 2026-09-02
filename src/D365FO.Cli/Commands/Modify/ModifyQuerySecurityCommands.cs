using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Bridge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Modify;

/// <summary><c>d365fo modify add-query-range</c> — add a range to an AOT query's datasource.</summary>
public sealed class ModifyAddQueryRangeCommand : Command<ModifyAddQueryRangeCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [System.ComponentModel.Description("AOT query to add the range to.")]
        public string Query { get; init; } = "";

        [CommandArgument(1, "<FIELD>")]
        [System.ComponentModel.Description("Field the range filters on. Also names the range, as every shipped query does.")]
        public string Field { get; init; } = "";

        [CommandOption("--data-source <NAME>")]
        [System.ComponentModel.Description("Datasource the range belongs to. Optional when the query has exactly one.")]
        public string? DataSource { get; init; }

        [CommandOption("--value <EXPRESSION>")]
        [System.ComponentModel.Description("Range expression, e.g. \"1\" or \"!Closed\". Omit for a range whose value is set at run time via QueryBuildRange.value().")]
        public string? Value { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddQueryRange,
            Kind = "query",
            ObjectName = settings.Query,
            Member = settings.Field,
            DataSourceName = settings.DataSource,
            RangeValue = settings.Value,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify remove-query-range</c> — remove a range from an AOT query's datasource.</summary>
public sealed class ModifyRemoveQueryRangeCommand : Command<ModifyRemoveQueryRangeCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [System.ComponentModel.Description("AOT query holding the range.")]
        public string Query { get; init; } = "";

        [CommandArgument(1, "<FIELD>")]
        [System.ComponentModel.Description("Field the range filters on.")]
        public string Field { get; init; } = "";

        [CommandOption("--data-source <NAME>")]
        [System.ComponentModel.Description("Datasource the range belongs to. Optional when the query has exactly one.")]
        public string? DataSource { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.RemoveQueryRange,
            Kind = "query",
            ObjectName = settings.Query,
            Member = settings.Field,
            DataSourceName = settings.DataSource,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-entry-point</c> — grant an entry point on a security privilege.</summary>
public sealed class ModifyAddEntryPointCommand : Command<ModifyAddEntryPointCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<PRIVILEGE>")]
        [System.ComponentModel.Description("Security privilege to grant on.")]
        public string Privilege { get; init; } = "";

        [CommandArgument(1, "<OBJECT>")]
        [System.ComponentModel.Description("Object granted, e.g. the menu item name.")]
        public string ObjectName { get; init; } = "";

        [CommandOption("--type <TYPE>")]
        [System.ComponentModel.Description("AOT type of the entry point: MenuItemDisplay, MenuItemAction, MenuItemOutput, Form, … Required.")]
        public string? Type { get; init; }

        [CommandOption("--access <LEVEL>")]
        [System.ComponentModel.Description("Read (default) | Update | Create | Correct | Delete | Invoke. Levels are cumulative, and are written as the six independent permissions the security model actually has.")]
        public string? Access { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddEntryPoint,
            Kind = "securityprivilege",
            ObjectName = settings.Privilege,
            Member = settings.ObjectName,
            EntryPointType = settings.Type,
            Access = settings.Access,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify remove-entry-point</c> — revoke an entry point from a security privilege.</summary>
public sealed class ModifyRemoveEntryPointCommand : Command<ModifyRemoveEntryPointCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<PRIVILEGE>")]
        [System.ComponentModel.Description("Security privilege to revoke on.")]
        public string Privilege { get; init; } = "";

        [CommandArgument(1, "<OBJECT>")]
        [System.ComponentModel.Description("Entry point to revoke, by name or object name.")]
        public string ObjectName { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.RemoveEntryPoint,
            Kind = "securityprivilege",
            ObjectName = settings.Privilege,
            Member = settings.ObjectName,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary>
/// <c>d365fo modify batch</c> — several changes to one object in a single read-edit-write.
/// </summary>
/// <remarks>
/// Adding a field, an index over it and a field group showing it is three commands, three bridge
/// round trips, three journal entries — and two intermediate states published to disk, one of
/// them a table carrying a field no index covers. Batched, the object moves from one valid state
/// to the next in a single write. A step that refuses discards the whole batch with nothing
/// written, so a half-applied change cannot reach the AOT.
/// </remarks>
public sealed class ModifyBatchCommand : Command<ModifyBatchCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Object kind: table, form, enum, query, securityprivilege, …")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<OBJECT>")]
        [System.ComponentModel.Description("Object every step applies to.")]
        public string ObjectName { get; init; } = "";

        [CommandOption("--operations <JSON>")]
        [System.ComponentModel.Description("JSON array of steps, e.g. [{\"operation\":\"add-field\",\"member\":\"Note\",\"type\":\"Notes\"},{\"operation\":\"add-index\",\"member\":\"NoteIdx\",\"fields\":[\"Note\"]}]. Use --operations-file for anything long.")]
        public string? Operations { get; init; }

        [CommandOption("--operations-file <PATH>")]
        [System.ComponentModel.Description("File holding the same JSON array. Shell quoting makes a long inline array painful; this avoids it.")]
        public string? OperationsFile { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var outputKind = OutputMode.Resolve(settings.Output);

        var json = settings.Operations;
        if (!string.IsNullOrWhiteSpace(settings.OperationsFile))
        {
            if (!File.Exists(settings.OperationsFile))
            {
                return RenderHelpers.Render(outputKind, ToolResult<object>.Fail(
                    D365FoErrorCodes.BadInput, $"No such file: {settings.OperationsFile}"));
            }
            json = File.ReadAllText(settings.OperationsFile);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return RenderHelpers.Render(outputKind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, "--operations or --operations-file is required.",
                "Each step is {\"operation\":\"<name>\", \"member\":\"<name>\", …} — the operation names are the "
                + "`modify` sub-commands: add-field, add-index, add-relation, add-field-group, add-delete-action, "
                + "rename-field, property, add-enum-value, add-control, add-query-range, add-entry-point, and the remove-* forms."));
        }

        List<ObjectModifyEngine.ModifyRequest> steps;
        try
        {
            steps = BatchStepParser.Parse(json!, settings.Kind, settings.ObjectName, settings.Model);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(outputKind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, $"--operations is not a usable step array: {ex.Message}"));
        }

        if (steps.Count == 0)
        {
            return RenderHelpers.Render(outputKind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, "The step array is empty."));
        }

        return ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            // Placeholders: the header names the object, the steps carry the operations.
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = settings.Kind,
            ObjectName = settings.ObjectName,
            Member = "batch",
            Batch = steps,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
    }
}
