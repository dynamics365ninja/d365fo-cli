using D365FO.Core.Bridge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Modify;

// The structural half of `d365fo modify`: the collections a table owns (indexes, relations,
// field groups, delete actions) plus the removals every kind needs. Before these, an agent that
// wanted to add an index to an existing table had no path through the CLI at all and fell back
// to editing AOT XML by hand — which walks straight past the grounding, form-pattern and
// reference gates the tool exists to enforce.
//
// Every one of them goes through ObjectModifyEngine, so they inherit the same three guarantees
// as `modify add-field`: the base object is extended rather than overwritten when it belongs to
// a model this installation does not own, the pre-image is journalled so `d365fo undo` reverts
// it, and the document is canonicalised into contract order before the write.

/// <summary><c>d365fo modify add-index</c> — add an index to a live table.</summary>
public sealed class ModifyAddIndexCommand : Command<ModifyAddIndexCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table to add the index to.")]
        public string Table { get; init; } = "";

        [CommandArgument(1, "<INDEX>")]
        [System.ComponentModel.Description("New index name, e.g. VehicleIdx.")]
        public string Index { get; init; } = "";

        [CommandOption("--field <FIELD>")]
        [System.ComponentModel.Description("Field in the index key (repeatable). Order matters — it decides which queries the index can serve.")]
        public string[]? Field { get; init; }

        [CommandOption("--allow-duplicates")]
        [System.ComponentModel.Description("Permit duplicate keys. Omitted, the index is unique (AllowDuplicates=No).")]
        public bool AllowDuplicates { get; init; }

        [CommandOption("--alternate-key")]
        [System.ComponentModel.Description("Mark the index an alternate key. Implies a unique index.")]
        public bool AlternateKey { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddIndex,
            Kind = "table",
            ObjectName = settings.Table,
            Member = settings.Index,
            Fields = settings.Field,
            // An alternate key is unique by definition; accepting --allow-duplicates beside it
            // would let the caller ask for something the AOS refuses after a build cycle.
            AllowDuplicates = settings.AllowDuplicates && !settings.AlternateKey,
            AlternateKey = settings.AlternateKey,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-relation</c> — add a foreign-key relation to a live table.</summary>
public sealed class ModifyAddRelationCommand : Command<ModifyAddRelationCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table the relation is declared on.")]
        public string Table { get; init; } = "";

        [CommandArgument(1, "<FIELD>")]
        [System.ComponentModel.Description("Field on this table that holds the foreign key. Also names the relation.")]
        public string Field { get; init; } = "";

        [CommandOption("--related-table <TABLE>")]
        [System.ComponentModel.Description("Table the relation points at. Required.")]
        public string? RelatedTable { get; init; }

        [CommandOption("--related-field <FIELD>")]
        [System.ComponentModel.Description("Field on the related table. Defaults to the same name as <FIELD>.")]
        public string? RelatedField { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddRelation,
            Kind = "table",
            ObjectName = settings.Table,
            Member = settings.Field,
            RelatedTable = settings.RelatedTable,
            RelatedField = settings.RelatedField,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-field-group</c> — add a field group to a live table.</summary>
public sealed class ModifyAddFieldGroupCommand : Command<ModifyAddFieldGroupCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table to add the field group to.")]
        public string Table { get; init; } = "";

        [CommandArgument(1, "<GROUP>")]
        [System.ComponentModel.Description("New field group name. Overview, General and the five Auto* groups already exist on every table.")]
        public string Group { get; init; } = "";

        [CommandOption("--field <FIELD>")]
        [System.ComponentModel.Description("Field in the group (repeatable). Order is the order the form renders them in.")]
        public string[]? Field { get; init; }

        [CommandOption("--label <LABEL>")]
        [System.ComponentModel.Description("Label token (@File:Id) for the group.")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddFieldGroup,
            Kind = "table",
            ObjectName = settings.Table,
            Member = settings.Group,
            Fields = settings.Field,
            Label = settings.Label,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-delete-action</c> — add a delete action to a live table.</summary>
public sealed class ModifyAddDeleteActionCommand : Command<ModifyAddDeleteActionCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table the delete action is declared on.")]
        public string Table { get; init; } = "";

        [CommandOption("--related-table <TABLE>")]
        [System.ComponentModel.Description("Table whose rows the action governs. Required — it also identifies the action.")]
        public string? RelatedTable { get; init; }

        [CommandOption("--action <ACTION>")]
        [System.ComponentModel.Description("Cascade | Restricted | CascadeRestricted | None. Required.")]
        public string? Action { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddDeleteAction,
            Kind = "table",
            ObjectName = settings.Table,
            // A delete action carries no name of its own; the related table is the identity,
            // and Member has to be non-empty for the shared validation.
            Member = settings.RelatedTable ?? "",
            RelatedTable = settings.RelatedTable,
            DeleteAction = settings.Action,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify rename-field</c> — rename a field and the members that reference it.</summary>
public sealed class ModifyRenameFieldCommand : Command<ModifyRenameFieldCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table holding the field.")]
        public string Table { get; init; } = "";

        [CommandArgument(1, "<FIELD>")]
        [System.ComponentModel.Description("Current field name.")]
        public string Field { get; init; } = "";

        [CommandOption("--new-name <NAME>")]
        [System.ComponentModel.Description("New field name. Required. Indexes, field groups and relation constraints naming the field are rewritten with it; X++ is not.")]
        public string? NewName { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.RenameField,
            Kind = "table",
            ObjectName = settings.Table,
            Member = settings.Field,
            NewName = settings.NewName,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary>
/// Shared settings for the removals: kind and object come from the sub-command, the member
/// name is the one argument they all take.
/// </summary>
/// <remarks>
/// Deliberately NOT abstract. Every remove sub-command uses this type directly as its
/// <c>TSettings</c>, and Spectre.Console.Cli constructs the settings type by reflection — an
/// abstract one fails at RUN time with "Could not resolve type
/// 'ModifyRemoveSettings'", not at compile time. Engine tests cannot see it either, since they
/// call the engine rather than the command. Caught on a live run; pinned by
/// <c>CommandSurfaceTests</c>.
/// </remarks>
public class ModifyRemoveSettings : ModifyObjectSettings
{
    [CommandArgument(0, "<OBJECT>")]
    [System.ComponentModel.Description("Object to remove the member from.")]
    public string ObjectName { get; init; } = "";

    [CommandArgument(1, "<MEMBER>")]
    [System.ComponentModel.Description("Name of the member to remove.")]
    public string Member { get; init; } = "";
}

/// <summary>Builds the removal request every remove sub-command shares.</summary>
internal static class RemoveRunner
{
    internal static int Run(ModifyRemoveSettings settings, ObjectModifyEngine.Operation operation, string kind) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = operation,
            Kind = kind,
            ObjectName = settings.ObjectName,
            Member = settings.Member,
            // A delete action is addressed by its related table, which arrives in the same slot.
            RelatedTable = operation == ObjectModifyEngine.Operation.RemoveDeleteAction ? settings.Member : null,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify remove-field</c> — remove a field from a live table.</summary>
public sealed class ModifyRemoveFieldCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveField, "table");
}

/// <summary><c>d365fo modify remove-index</c> — remove an index from a live table.</summary>
public sealed class ModifyRemoveIndexCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveIndex, "table");
}

/// <summary><c>d365fo modify remove-relation</c> — remove a relation from a live table.</summary>
public sealed class ModifyRemoveRelationCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveRelation, "table");
}

/// <summary><c>d365fo modify remove-field-group</c> — remove a field group from a live table.</summary>
public sealed class ModifyRemoveFieldGroupCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveFieldGroup, "table");
}

/// <summary><c>d365fo modify remove-delete-action</c> — remove a delete action, named by its related table.</summary>
public sealed class ModifyRemoveDeleteActionCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveDeleteAction, "table");
}

/// <summary><c>d365fo modify remove-enum-value</c> — remove a value from a live base enum.</summary>
public sealed class ModifyRemoveEnumValueCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveEnumValue, "enum");
}

/// <summary><c>d365fo modify remove-control</c> — remove a control, and its children, from a live form.</summary>
public sealed class ModifyRemoveControlCommand : Command<ModifyRemoveSettings>
{
    public override int Execute(CommandContext ctx, ModifyRemoveSettings settings) =>
        RemoveRunner.Run(settings, ObjectModifyEngine.Operation.RemoveControl, "form");
}
