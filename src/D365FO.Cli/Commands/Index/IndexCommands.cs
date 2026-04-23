using D365FO.Core;
using D365FO.Core.Extract;
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
        var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var repo = new MetadataRepository(cfg.DatabasePath);
        repo.EnsureSchema();

        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            packagesPath = cfg.PackagesPath,
            schemaVersion = MetadataRepository.CurrentSchemaVersion,
            note = "Schema ready. Run 'd365fo index extract' to ingest metadata from PACKAGES_PATH.",
        });

        return RenderHelpers.Render(kind, result, _ =>
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
        ExtractCounts? counts = null;
        if (exists)
        {
            try
            {
                var repo = RepoFactory.Create();
                counts = repo.CountAll();
            }
            catch { /* swallow — status must not fail */ }
        }

        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            exists,
            sizeBytes,
            packagesPath = cfg.PackagesPath,
            workspacePath = cfg.WorkspacePath,
            customModels = cfg.CustomModels,
            labelLanguages = cfg.LabelLanguages,
            counts,
        });
        return RenderHelpers.Render(kind, result);
    }
}

public sealed class IndexExtractCommand : Command<IndexExtractCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        public string? PackagesPath { get; init; }

        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Limit extraction to a single model folder (optional).")]
        public string? OnlyModel { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
        var root = settings.PackagesPath ?? cfg.PackagesPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "MISSING_PACKAGES_PATH",
                "No packages path provided.",
                "Pass --packages <PATH> or set D365FO_PACKAGES_PATH."));
        }
        if (!Directory.Exists(root))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "PACKAGES_PATH_NOT_FOUND", $"Path does not exist: {root}"));
        }

        var repo = RepoFactory.Create(settings.DatabasePath);
        var extractor = new MetadataExtractor();
        int modelCount = 0;
        var per = new List<object>();

        foreach (var batch in extractor.ExtractAll(root, cfg.LabelLanguages))
        {
            if (settings.OnlyModel is { Length: > 0 } only &&
                !string.Equals(batch.Model, only, StringComparison.OrdinalIgnoreCase))
                continue;
            repo.ApplyExtract(batch);
            modelCount++;
            per.Add(new
            {
                model = batch.Model,
                tables = batch.Tables.Count,
                classes = batch.Classes.Count,
                edts = batch.Edts.Count,
                enums = batch.Enums.Count,
                menuItems = batch.MenuItems.Count,
                coc = batch.CocExtensions.Count,
                labels = batch.Labels.Count,
            });
        }

        var totals = repo.CountAll();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            packagesRoot = root,
            modelsProcessed = modelCount,
            perModel = per,
            totals,
        }));
    }
}
