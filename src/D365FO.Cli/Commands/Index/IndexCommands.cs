using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Index;

public sealed class IndexBuildCommand : Command<IndexBuildCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(cfg.DatabasePath)!);
        var repo = new MetadataRepository(cfg.DatabasePath);
        repo.EnsureSchema();

        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            packagesPath = cfg.PackagesPath,
            schemaVersion = 1,
            note = "Schema ensured. Use 'd365fo index extract' (phase 1 pending) to ingest metadata.",
        });

        return RenderHelpers.Render(kind, result, data =>
        {
            AnsiConsole.MarkupLine($"[green]OK[/] index ready at [bold]{cfg.DatabasePath}[/]");
            if (cfg.PackagesPath is null)
                AnsiConsole.MarkupLine("[yellow]warn[/] D365FO_PACKAGES_PATH not set; extraction will require --packages.");
        });
    }
}

public sealed class IndexStatusCommand : Command<IndexStatusCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment();
        var exists = File.Exists(cfg.DatabasePath);
        long sizeBytes = exists ? new FileInfo(cfg.DatabasePath).Length : 0;

        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            exists,
            sizeBytes,
            packagesPath = cfg.PackagesPath,
            workspacePath = cfg.WorkspacePath,
            customModels = cfg.CustomModels,
            labelLanguages = cfg.LabelLanguages,
        });
        return RenderHelpers.Render(kind, result);
    }
}
