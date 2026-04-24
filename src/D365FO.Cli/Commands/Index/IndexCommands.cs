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
        var applied = repo.EnsureSchema();

        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            packagesPath = cfg.PackagesPath,
            schemaVersion = MetadataRepository.CurrentSchemaVersion,
            schemaApplied = applied,
            note = applied
                ? $"Schema v{MetadataRepository.CurrentSchemaVersion} applied. Run 'd365fo index extract' to ingest metadata from PACKAGES_PATH."
                : $"Schema already at v{MetadataRepository.CurrentSchemaVersion}. Run 'd365fo index extract' to ingest metadata from PACKAGES_PATH.",
        });

        return RenderHelpers.Render(kind, result, _ =>
        {
            if (applied)
                AnsiConsole.MarkupLine($"[green]OK[/] schema v{MetadataRepository.CurrentSchemaVersion} applied at [bold]{cfg.DatabasePath}[/]");
            else
                AnsiConsole.MarkupLine($"[green]OK[/] index ready at [bold]{cfg.DatabasePath}[/] (schema v{MetadataRepository.CurrentSchemaVersion})");
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

        [CommandOption("--since <ISO8601>")]
        [System.ComponentModel.Description("Skip models whose newest XML mtime is older than this timestamp. Enables mtime-based incremental refresh.")]
        public string? Since { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
        => ExtractCore(
            OutputMode.Resolve(settings.Output),
            settings.PackagesPath,
            settings.DatabasePath,
            settings.OnlyModel,
            settings.Since);

    internal static int ExtractCore(
        OutputMode.Kind kind,
        string? packagesOverride,
        string? databaseOverride,
        string? onlyModel,
        string? sinceIso)
    {
        var cfg = D365FoSettings.FromEnvironment(databaseOverride);
        var root = packagesOverride ?? cfg.PackagesPath;
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

        var repo = RepoFactory.Create(databaseOverride);
        var extractor = new MetadataExtractor();
        var matcher = new ModelMatcher(cfg.CustomModels);
        int modelCount = 0;
        int customCount = 0;
        int skippedCount = 0;
        DateTime? since = null;
        if (!string.IsNullOrWhiteSpace(sinceIso))
        {
            if (!DateTime.TryParse(sinceIso, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var sinceParsed))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "BAD_INPUT", $"Invalid --since timestamp: '{sinceIso}'."));
            }
            since = sinceParsed;
        }
        var per = new List<object>();
        var runSw = System.Diagnostics.Stopwatch.StartNew();

        // Pre-enumerate candidate model folders so we can report progress
        // *before* each model is parsed (useful when a single model like
        // ApplicationSuite takes many minutes).
        var modelDirs = EnumerateModelDirs(root, onlyModel).ToList();
        var showProgress = kind != OutputMode.Kind.Json && !System.Console.IsOutputRedirected;

        void ProcessAll(Action<string>? onStart, Action<string, ExtractBatch, long>? onDone)
        {
            foreach (var modelDir in modelDirs)
            {
                var model = Path.GetFileName(modelDir)!;
                if (since.HasValue && NewestMtime(modelDir) is { } newest && newest < since.Value)
                {
                    skippedCount++;
                    continue;
                }
                onStart?.Invoke(model);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                ExtractBatch batch;
                try
                {
                    batch = extractor.ExtractModel(modelDir, model, cfg.LabelLanguages, matcher.IsMatch(model));
                }
                catch
                {
                    continue;
                }
                repo.ApplyExtract(batch);
                sw.Stop();
                var elapsedMs = sw.ElapsedMilliseconds;
                modelCount++;
                if (batch.IsCustom) customCount++;
                per.Add(new
                {
                    model = batch.Model,
                    isCustom = batch.IsCustom,
                    tables = batch.Tables.Count,
                    classes = batch.Classes.Count,
                    edts = batch.Edts.Count,
                    enums = batch.Enums.Count,
                    menuItems = batch.MenuItems.Count,
                    coc = batch.CocExtensions.Count,
                    labels = batch.Labels.Count,
                    elapsedMs,
                });
                onDone?.Invoke(model, batch, elapsedMs);
            }
        }

        if (showProgress)
        {
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start("Extracting metadata…", sctx =>
                {
                    ProcessAll(
                        onStart: model =>
                        {
                            var pos = modelCount + 1;
                            sctx.Status($"[[{pos}/{modelDirs.Count}]] {Markup.Escape(model)}");
                        },
                        onDone: (model, batch, elapsedMs) =>
                        {
                            AnsiConsole.MarkupLine(
                                $"[green]✓[/] [[{modelCount}/{modelDirs.Count}]] {Markup.Escape(model)} " +
                                $"[grey](tables={batch.Tables.Count} classes={batch.Classes.Count} " +
                                $"edts={batch.Edts.Count} enums={batch.Enums.Count} " +
                                $"labels={batch.Labels.Count}{(batch.IsCustom ? " custom" : "")} " +
                                $"{elapsedMs}ms)[/]");
                        });
                });
        }
        else
        {
            ProcessAll(null, null);
        }

        runSw.Stop();
        var totals = repo.CountAll();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            packagesRoot = root,
            modelsProcessed = modelCount,
            modelsSkipped = skippedCount,
            since = since?.ToString("O"),
            customModelsMatched = customCount,
            customModelPatterns = cfg.CustomModels,
            elapsedMs = runSw.ElapsedMilliseconds,
            perModel = per,
            totals,
        }));
    }

    private static DateTime? NewestMtime(string dir)
    {
        DateTime? max = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    var t = File.GetLastWriteTimeUtc(f);
                    if (!max.HasValue || t > max.Value) max = t;
                }
                catch { }
            }
        }
        catch { }
        return max;
    }

    private static IEnumerable<string> EnumerateModelDirs(string packagesRoot, string? onlyModel)
    {
        IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        static bool HasAot(string dir)
        {
            foreach (var s in new[] {
                "AxTable", "AxClass", "AxEdt", "AxEnum", "AxLabelFile", "AxForm",
                "AxTableExtension", "AxFormExtension", "AxEdtExtension", "AxEnumExtension",
                "AxSecurityRole", "AxSecurityDuty", "AxSecurityPrivilege",
                "AxMenuItemDisplay", "AxMenuItemAction", "AxMenuItemOutput",
                "AxQuery", "AxQuerySimple", "AxView", "AxDataEntityView",
                "AxReport", "AxReportSsrs", "AxService", "AxServiceGroup", "AxWorkflowType",
            })
                if (Directory.Exists(Path.Combine(dir, s))) return true;
            return false;
        }

        foreach (var pkg in SafeDirs(packagesRoot))
        {
            // Mirror MetadataExtractor: skip FormAdaptor shim packages.
            if (D365FO.Core.Extract.MetadataExtractor.IsFormAdaptorPackage(Path.GetFileName(pkg))) continue;
            foreach (var model in SafeDirs(pkg))
            {
                if (D365FO.Core.Extract.MetadataExtractor.IsFormAdaptorPackage(Path.GetFileName(model))) continue;
                if (!HasAot(model)) continue;
                if (onlyModel is { Length: > 0 } only &&
                    !string.Equals(Path.GetFileName(model), only, StringComparison.OrdinalIgnoreCase))
                    continue;
                yield return model;
            }
        }
    }
}

/// <summary>
/// Incremental refresh: runs <see cref="IndexExtractCommand"/> but seeds
/// <c>--since</c> from the index DB file's last-write timestamp (with a
/// 5-minute safety lookback). Models whose XMLs haven't changed since the
/// last extract are skipped — turning a full multi-minute rescan into a
/// couple of seconds on a warm workspace.
/// </summary>
public sealed class IndexRefreshCommand : Command<IndexRefreshCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        public string? PackagesPath { get; init; }

        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--model <NAME>")]
        public string? OnlyModel { get; init; }

        [CommandOption("--since <ISO8601>")]
        public string? Since { get; init; }

        [CommandOption("--force")]
        [System.ComponentModel.Description("Ignore any computed threshold and re-extract every model.")]
        public bool Force { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var since = settings.Since;
        if (!settings.Force && string.IsNullOrWhiteSpace(since))
        {
            var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
            if (File.Exists(cfg.DatabasePath))
            {
                var dbMtime = File.GetLastWriteTimeUtc(cfg.DatabasePath);
                since = (dbMtime - TimeSpan.FromMinutes(5)).ToString("O");
            }
        }
        return IndexExtractCommand.ExtractCore(
            OutputMode.Resolve(settings.Output),
            settings.PackagesPath,
            settings.DatabasePath,
            settings.OnlyModel,
            since);
    }
}
