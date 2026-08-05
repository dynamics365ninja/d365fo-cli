using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds a data-migration <c>SysRunnable</c> class with the proper
/// <c>doInsert</c> / <c>doUpdate</c> pattern, batch-safe transaction commits,
/// and progress logging.
/// </summary>
public sealed class GenerateMigrationScriptCommand : Command<GenerateMigrationScriptCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Migration class name (e.g. MyTableMigration).")]
        public string Name { get; init; } = "";

        [CommandOption("--source-table <TABLE>")]
        [System.ComponentModel.Description("Source table to read from. Required.")]
        public string? SourceTable { get; init; }

        [CommandOption("--target-table <TABLE>")]
        [System.ComponentModel.Description("Target table to write to. Defaults to the same as --source-table.")]
        public string? TargetTable { get; init; }

        [CommandOption("--batch-size <N>")]
        [System.ComponentModel.Description("Number of records per transaction batch. Defaults to 1000.")]
        public int BatchSize { get; init; } = 1000;

        [CommandOption("--mode <MODE>")]
        [System.ComponentModel.Description("Migration operation: Insert (default), Update, Upsert.")]
        public string Mode { get; init; } = "Insert";
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Migration class name required."));
        if (string.IsNullOrWhiteSpace(settings.SourceTable))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--source-table is required."));

        if (!TryParseMode(settings.Mode, out var mode))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                $"Unknown --mode '{settings.Mode}'. Expected Insert | Update | Upsert."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var outPath = settings.Out;
        if (hasInstall && !hasOut)
        {
            outPath = GenerateInstaller.ResolveInstallPath(kind, Folders.Class, settings.Name, settings.InstallTo!, out var fail);
            if (fail.HasValue) return fail.Value;
        }

        var targetTable = string.IsNullOrWhiteSpace(settings.TargetTable) ? settings.SourceTable! : settings.TargetTable!;
        var batchSize   = settings.BatchSize > 0 ? settings.BatchSize : 1000;

        try
        {
            var doc = MigrationScriptScaffolder.MigrationClass(
                settings.Name, settings.SourceTable!, targetTable, mode, batchSize);
            var res = ScaffoldFileWriter.Write(doc, outPath!, settings.Overwrite);

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind        = "MigrationScript",
                name        = settings.Name,
                sourceTable = settings.SourceTable,
                targetTable,
                batchSize,
                mode        = mode.ToString(),
                path        = res.Path,
                bytes       = res.Bytes,
                backup      = res.BackupPath,
                model       = settings.InstallTo,
            }));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }

    private static bool TryParseMode(string raw, out MigrationMode mode)
    {
        mode = raw.ToLowerInvariant() switch
        {
            "insert"           => MigrationMode.Insert,
            "update"           => MigrationMode.Update,
            "upsert"           => MigrationMode.Upsert,
            _                  => (MigrationMode)(-1),
        };
        return (int)mode >= 0;
    }
}
