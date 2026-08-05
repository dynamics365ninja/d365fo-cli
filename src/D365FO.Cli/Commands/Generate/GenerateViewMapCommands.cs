using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxView</c> — a read-only projection over an <c>AxQuery</c>.</summary>
public sealed class GenerateViewCommand : Command<GenerateViewCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("View name.")]
        public string Name { get; init; } = "";

        [CommandOption("--query <NAME>")]
        [System.ComponentModel.Description("Backing AxQuery name. Required — a view projects a query.")]
        public string? Query { get; init; }

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable bound field: <name>:<dataSource>[[:<dataField>]]. dataField defaults to <name>. Example: --field AccountNum:CustTable")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--computed <SPEC>")]
        [System.ComponentModel.Description("Repeatable computed field: <name>:<viewMethod>:<type>. Type: String|Int|Int64|Real|Date|UtcDateTime|Enum. Example: --computed Total:getTotalSQL:Real")]
        public string[] Computed { get; init; } = Array.Empty<string>();

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }

        [CommandOption("--configuration-key <NAME>")]
        [System.ComponentModel.Description("AxConfigurationKey gating the view. Omitted when not set.")]
        public string? ConfigurationKey { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "View name required."));
        if (string.IsNullOrWhiteSpace(settings.Query))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--query <NAME> required.",
                hint: "A view projects an AxQuery; generate the query first with `d365fo generate query`."));
        if (settings.Fields.Length == 0 && settings.Computed.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "At least one --field or --computed required."));

        var specs = new List<ViewFieldSpec>();
        foreach (var raw in settings.Fields)
        {
            // <name>:<dataSource>[:<dataField>]
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --field '{raw}'. Expected <name>:<dataSource>[:<dataField>]."));
            var dataField = parts.Length > 2 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : parts[0];
            specs.Add(new ViewFieldSpec(parts[0], DataSource: parts[1], DataField: dataField));
        }
        foreach (var raw in settings.Computed)
        {
            // <name>:<viewMethod>:<type>
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || parts.Any(string.IsNullOrEmpty))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --computed '{raw}'. Expected <name>:<viewMethod>:<type>."));
            specs.Add(new ViewFieldSpec(parts[0], ViewMethod: parts[1], ComputedType: parts[2]));
        }

        XDocument doc;
        try
        {
            doc = ViewScaffolder.View(settings.Name, settings.Query!, specs, settings.Label, settings.ConfigurationKey);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        // Written straight to disk rather than through GenerateInstaller.Emit: the
        // bridge's createObject only accepts class|table|edt|enum|form, so routing a
        // view through it would fail with INVALID_KIND and report a misleading
        // "Metadata API rejected the object" warning on every install. Same approach
        // as `generate query`, which has the same constraint. (`--verify` is likewise
        // inapplicable — there is no readView verb to check against.)
        if (!TryResolveOutPath(kind, settings, Folders.View, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxView",
                name = settings.Name,
                query = settings.Query,
                boundFields = settings.Fields.Length,
                computedFields = settings.Computed.Length,
                configurationKey = settings.ConfigurationKey,
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    /// <summary>
    /// Resolve the output path from <c>--out</c> or <c>--install-to</c>. Shared by the
    /// view and map commands, which both bypass the bridge-backed installer.
    /// </summary>
    internal static bool TryResolveOutPath(
        OutputMode.Kind kind, GenerateSettings settings, string axSubfolder, string name,
        out string? outPath, out int failure)
    {
        outPath = settings.Out;
        failure = 0;

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
        {
            failure = RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, "--out or --install-to is required."));
            return false;
        }

        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, axSubfolder, name, settings.InstallTo!, out var fail);
            if (fail.HasValue)
            {
                failure = fail.Value;
                return false;
            }
        }
        return true;
    }
}

/// <summary>Scaffolds an <c>AxMap</c> — a shared field template mapped onto several tables.</summary>
public sealed class GenerateMapCommand : Command<GenerateMapCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Map name.")]
        public string Name { get; init; } = "";

        [CommandOption("--field <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <name>:<edt>[[:<label>]]. The EDT determines the field's concrete type. Example: --field AccountNum:CustAccount")]
        public string[] Fields { get; init; } = Array.Empty<string>();

        [CommandOption("--map-to <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <table>[[:<mapField>=<tableField>,…]]. Omit the pairs to map every field by identical name. Example: --map-to CustTable:AccountNum=AccountNum")]
        public string[] MapTo { get; init; } = Array.Empty<string>();

        [CommandOption("--label <KEY>")]
        public string? Label { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Map name required."));
        if (settings.Fields.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "At least one --field <name>:<edt> required."));

        var fields = new List<MapFieldSpec>();
        foreach (var raw in settings.Fields)
        {
            var parts = raw.Split(':', 3, StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --field '{raw}'. Expected <name>:<edt>[:<label>]."));
            fields.Add(new MapFieldSpec(parts[0], parts[1], parts.Length > 2 ? parts[2] : null));
        }

        var mappings = new List<MapTableMappingSpec>();
        foreach (var raw in settings.MapTo)
        {
            var parts = raw.Split(':', 2, StringSplitOptions.TrimEntries);
            var table = parts.Length > 0 ? parts[0] : "";
            if (string.IsNullOrEmpty(table))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --map-to '{raw}'. Expected <table>[:<mapField>=<tableField>,…]."));

            List<MapFieldConnection> connections;
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[1]))
            {
                // No explicit pairs — connect every map field to the identically named
                // table field, which is the overwhelmingly common shape.
                connections = fields.Select(f => new MapFieldConnection(f.Name)).ToList();
            }
            else
            {
                connections = new List<MapFieldConnection>();
                foreach (var pair in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var eq = pair.Split('=', 2, StringSplitOptions.TrimEntries);
                    if (eq.Length == 0 || string.IsNullOrEmpty(eq[0]))
                        return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                            $"Invalid connection '{pair}' in --map-to '{raw}'. Expected <mapField>=<tableField>."));
                    connections.Add(new MapFieldConnection(eq[0], eq.Length > 1 && !string.IsNullOrEmpty(eq[1]) ? eq[1] : null));
                }
            }
            mappings.Add(new MapTableMappingSpec(table, connections));
        }

        XDocument doc;
        try
        {
            doc = MapScaffolder.Map(settings.Name, fields, mappings, settings.Label,
                GenerateInstaller.BuildEdtBaseTypeResolver());
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        // Same reasoning as GenerateViewCommand: the bridge has no "map" kind.
        if (!GenerateViewCommand.TryResolveOutPath(kind, settings, Folders.Map, settings.Name, out var outPath, out var pathFailure))
            return pathFailure;

        try
        {
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind = "AxMap",
                name = settings.Name,
                fieldCount = fields.Count,
                mappedTables = mappings.Select(m => m.Table).ToList(),
                path = res.Path,
                bytes = res.Bytes,
                backup = res.BackupPath,
                model = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
