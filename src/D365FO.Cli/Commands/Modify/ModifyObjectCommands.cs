using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Modify;

/// <summary>
/// Options every structured-modify sub-command shares: how to reach the object, and
/// whether the change goes into the object itself or into an extension of it.
/// </summary>
public abstract class ModifyObjectSettings : D365OutputSettings
{
    [CommandOption("--model <MODEL>")]
    [System.ComponentModel.Description("Owning model of the target object. Resolved via the index when omitted.")]
    public string? Model { get; init; }

    [CommandOption("--extension [SUFFIX]")]
    [System.ComponentModel.Description("Write to the <Target>.<SUFFIX> extension instead of the object itself (default suffix: the one an existing extension already uses, else 'Extension'). Implied automatically when the object lives outside D365FO_CUSTOM_MODELS.")]
    public Spectre.Console.Cli.FlagValue<string>? Extension { get; init; }

    [CommandOption("--extension-model <MODEL>")]
    [System.ComponentModel.Description("Model to create/update the extension in. Defaults to the first configured custom model.")]
    public string? ExtensionModel { get; init; }

    [CommandOption("--require-extension")]
    [System.ComponentModel.Description("Fail rather than ever modifying the base object in place.")]
    public bool RequireExtension { get; init; }

    /// <summary>Suffix requested via <c>--extension</c>; null when the flag was not passed.</summary>
    internal string? ResolvedExtensionSuffix =>
        Extension is { IsSet: true } f ? (string.IsNullOrWhiteSpace(f.Value) ? "Extension" : f.Value) : null;
}

internal static class ModifyRunner
{
    /// <summary>
    /// Shared execution tail: build a repository (degrading to bridge-only when the index
    /// is unavailable), run the engine, and render.
    /// </summary>
    internal static int Run(ModifyObjectSettings settings, ObjectModifyEngine.ModifyRequest request)
    {
        var kind = OutputMode.Resolve(settings.Output);
        MetadataRepository? repo = null;
        try { repo = RepoFactory.Create(); } catch { /* engine surfaces the degraded-mode warning */ }
        return RenderHelpers.Render(kind, ObjectModifyEngine.Modify(request, repo));
    }
}

/// <summary>
/// <c>d365fo modify property</c> — set a simple property on a live AOT object
/// (Label, ConfigurationKey, TableGroup, …).
/// </summary>
public sealed class ModifyPropertyCommand : Command<ModifyPropertyCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<KIND>")]
        [System.ComponentModel.Description("Object kind: class, table, edt, enum, or form.")]
        public string Kind { get; init; } = "";

        [CommandArgument(1, "<OBJECT>")]
        [System.ComponentModel.Description("Object name.")]
        public string ObjectName { get; init; } = "";

        [CommandArgument(2, "<PROPERTY>")]
        [System.ComponentModel.Description("Property element name, e.g. Label, ConfigurationKey, TableGroup.")]
        public string Property { get; init; } = "";

        [CommandOption("--value <VALUE>")]
        [System.ComponentModel.Description("New value. Required.")]
        public string? Value { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = settings.Kind,
            ObjectName = settings.ObjectName,
            Member = settings.Property,
            Value = settings.Value,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-field</c> — add a field to a live table (or to its extension).</summary>
public sealed class ModifyAddFieldCommand : Command<ModifyAddFieldCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<TABLE>")]
        [System.ComponentModel.Description("Table to add the field to.")]
        public string Table { get; init; } = "";

        [CommandArgument(1, "<FIELD>")]
        [System.ComponentModel.Description("New field name.")]
        public string Field { get; init; } = "";

        [CommandOption("--edt <EDT>")]
        [System.ComponentModel.Description("Extended Data Type backing the field. Required — it decides the concrete AxTableField subtype.")]
        public string? Edt { get; init; }

        [CommandOption("--label <LABEL>")]
        [System.ComponentModel.Description("Label token (@File:Id) for the field.")]
        public string? Label { get; init; }

        [CommandOption("--mandatory")]
        [System.ComponentModel.Description("Set Mandatory=Yes on the new field.")]
        public bool Mandatory { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = settings.Table,
            Member = settings.Field,
            Type = settings.Edt,
            Label = settings.Label,
            Mandatory = settings.Mandatory,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-enum-value</c> — add a value to a live base enum (or its extension).</summary>
public sealed class ModifyAddEnumValueCommand : Command<ModifyAddEnumValueCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<ENUM>")]
        [System.ComponentModel.Description("Base enum to extend.")]
        public string Enum { get; init; } = "";

        [CommandArgument(1, "<VALUE>")]
        [System.ComponentModel.Description("New enum value name.")]
        public string Value { get; init; } = "";

        [CommandOption("--label <LABEL>")]
        [System.ComponentModel.Description("Label token (@File:Id) for the value.")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddEnumValue,
            Kind = "enum",
            ObjectName = settings.Enum,
            Member = settings.Value,
            Label = settings.Label,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}

/// <summary><c>d365fo modify add-control</c> — add a control to a live form's design (or its extension).</summary>
public sealed class ModifyAddControlCommand : Command<ModifyAddControlCommand.Settings>
{
    public sealed class Settings : ModifyObjectSettings
    {
        [CommandArgument(0, "<FORM>")]
        [System.ComponentModel.Description("Form to add the control to.")]
        public string Form { get; init; } = "";

        [CommandArgument(1, "<CONTROL>")]
        [System.ComponentModel.Description("New control name.")]
        public string Control { get; init; } = "";

        [CommandOption("--type <TYPE>")]
        [System.ComponentModel.Description("Normalized control type: Grid, Group, Tab, TabPage, ActionPane, ButtonGroup, String, Int, CheckBox, … Required.")]
        public string? Type { get; init; }

        [CommandOption("--parent <CONTROL>")]
        [System.ComponentModel.Description("Container control to add into. Omit to add at the Design root.")]
        public string? Parent { get; init; }

        [CommandOption("--datasource <DS>")]
        [System.ComponentModel.Description("Form datasource to bind to (with --datafield).")]
        public string? DataSource { get; init; }

        [CommandOption("--datafield <FIELD>")]
        [System.ComponentModel.Description("Table field to bind to. Makes the control data-bound.")]
        public string? DataField { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        ModifyRunner.Run(settings, new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddControl,
            Kind = "form",
            ObjectName = settings.Form,
            Member = settings.Control,
            Type = settings.Type,
            Parent = settings.Parent,
            DataSource = settings.DataSource,
            DataField = settings.DataField,
            Model = settings.Model,
            ExtensionSuffix = settings.ResolvedExtensionSuffix,
            ExtensionModel = settings.ExtensionModel,
            RequireExtension = settings.RequireExtension,
        });
}
