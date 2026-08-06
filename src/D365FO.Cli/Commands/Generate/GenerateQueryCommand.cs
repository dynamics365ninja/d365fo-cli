using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>Scaffolds an <c>AxQuery</c> with data sources and optional joins.</summary>
public sealed class GenerateQueryCommand : Command<GenerateQueryCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Query name.")]
        public string Name { get; init; } = "";

        [CommandOption("--ds <TABLE>")]
        [System.ComponentModel.Description("Root data source table (repeatable). First entry becomes the root; additional entries with no --join flag are treated as InnerJoin children of the first root.")]
        public string[] DataSources { get; init; } = Array.Empty<string>();

        [CommandOption("--join <SPEC>")]
        [System.ComponentModel.Description("Repeatable: <table>:<joinKind>:<parentDs>. JoinKind: InnerJoin (default) | OuterJoin | ExistsJoin | NoExistsJoin (note: no \"t\" — this is the real AOT enum spelling, not NotExistsJoin). Example: --join SalesLine:InnerJoin:SalesTable")]
        public string[] Joins { get; init; } = Array.Empty<string>();
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Query name required."));
        if (settings.DataSources.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "At least one --ds <TABLE> required."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Query, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        // Build the data-source list: roots first (no ParentDs), then joins.
        var dsList = new List<QueryDataSourceSpec>();

        // First --ds is the root; subsequent --ds entries without a matching --join
        // are treated as additional roots.
        foreach (var ds in settings.DataSources)
            dsList.Add(new QueryDataSourceSpec(ds));

        // --join specs override: they nominate a parent and are nested.
        foreach (var j in settings.Joins)
        {
            if (!TryParseJoin(j, out var spec))
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                    $"Invalid --join '{j}'. Expected <table>:<joinKind>:<parentDs>."));
            // Remove any duplicate root entry for the same table added by --ds.
            dsList.RemoveAll(d => string.Equals(d.Table, spec!.Table, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(d.ParentDs));
            dsList.Add(spec!);
        }

        XDocument doc;
        try
        {
            doc = QueryScaffolder.Query(settings.Name, dsList);
        }
        catch (ArgumentException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, ex.Message));
        }

        try
        {
            // Grounding gate (issue #161): uniform across every generate subcommand.
            var gate = GenerateInstaller.Gate(settings, settings.Name, doc);
            if (gate.Failure is not null) return RenderHelpers.Render(kind, gate.Failure);

            var res = GenerateInstaller.Write(gate, doc, outPath!, settings.Overwrite);
            var roots = dsList.Where(d => string.IsNullOrEmpty(d.ParentDs)).Select(d => d.Table).ToList();
            var joins  = dsList.Where(d => !string.IsNullOrEmpty(d.ParentDs))
                               .Select(d => new { d.Table, d.JoinMode, parentDs = d.ParentDs }).ToList();

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind        = "AxQuery",
                name        = settings.Name,
                rootSources = roots,
                joins,
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

    private static bool TryParseJoin(string raw, out QueryDataSourceSpec? spec)
    {
        spec = null;
        // Format: <table>[:<joinKind>[:<parentDs>]]
        var parts    = raw.Split(':', 3, StringSplitOptions.TrimEntries);
        var table    = parts.Length > 0 ? parts[0] : "";
        if (string.IsNullOrEmpty(table)) return false;

        var joinMode = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? parts[1] : "InnerJoin";
        var parent   = parts.Length > 2 ? parts[2] : null;

        var validModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "InnerJoin", "OuterJoin", "ExistsJoin", "NoExistsJoin" };
        if (!validModes.Contains(joinMode)) return false;

        spec = new QueryDataSourceSpec(table, null, parent, joinMode);
        return true;
    }
}
