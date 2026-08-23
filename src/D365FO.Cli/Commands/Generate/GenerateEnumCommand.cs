using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxEnum</c> base enumeration.</summary>
public sealed class GenerateEnumCommand : Command<GenerateEnumCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Enum name.")]
        public string Name { get; init; } = "";

        [CommandOption("--value <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<intValue>[[:<label>]]. Example: --value None:0 --value Active:1:'Active record'")]
        public string[] Values { get; init; } = Array.Empty<string>();

        [CommandOption("--non-extensible")]
        [System.ComponentModel.Description("Emit IsExtensible=false (default is true, the recommended D365FO setting).")]
        public bool NonExtensible { get; init; }

        [CommandOption("--label <TEXT>")]
        [System.ComponentModel.Description("Enum-level label.")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Enum name required."));

        var values = settings.Values.Select(ParseValue).ToList();
        var doc = XppScaffolder.Enum(settings.Name, values, !settings.NonExtensible, settings.Label);

        var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
        if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

        return GenerateInstaller.Emit(
            kind, gate, settings, "enum", Folders.Enum, settings.Name,
            doc,
            r => new
            {
                kind         = "AxEnum",
                name         = settings.Name,
                isExtensible = !settings.NonExtensible,
                label        = settings.Label,
                valueCount   = values.Count,
                values       = values.Select(v => new { v.Name, v.IntValue, v.Label }).ToList(),
                source       = r.Source,
                path         = r.Path,
                bytes        = r.Bytes,
                backup       = r.Backup,
                model        = settings.InstallTo,
                grounding    = gate.Grounding,
            },
            gate.Warnings);
    }

    private static EnumValueSpec ParseValue(string raw)
    {
        // Format: <name>:<intValue>[:<label>]
        var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
        var name  = parts.Length > 0 ? parts[0] : raw;
        var intVal = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        var label  = parts.Length > 2 ? parts[2].Trim('\'', '"') : null;
        return new EnumValueSpec(name, intVal, string.IsNullOrEmpty(label) ? null : label);
    }
}
