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

        var extraNote = cfg.CustomPackagesPaths.Count > 0
            ? $" Extra roots ({cfg.CustomPackagesPaths.Count}): {string.Join("; ", cfg.CustomPackagesPaths)}."
            : string.Empty;
        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            packagesPath = cfg.PackagesPath,
            extraPackagesPaths = cfg.CustomPackagesPaths.Count > 0 ? cfg.CustomPackagesPaths : null,
            schemaVersion = MetadataRepository.CurrentSchemaVersion,
            schemaApplied = applied,
            note = applied
                ? $"Schema v{MetadataRepository.CurrentSchemaVersion} applied. Run 'd365fo index extract' to ingest metadata from PACKAGES_PATH.{extraNote}"
                : $"Schema already at v{MetadataRepository.CurrentSchemaVersion}. Run 'd365fo index extract' to ingest metadata from PACKAGES_PATH.{extraNote}",
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
        string? statusWarning = null;
        IndexStaleness.StalenessResult? staleness = null;
        if (exists)
        {
            try
            {
                var repo = RepoFactory.Create();
                counts = repo.CountAll();

                // Staleness check: newest metadata mtime vs. index bookkeeping.
                var roots = new List<string>();
                if (!string.IsNullOrEmpty(cfg.PackagesPath)) roots.Add(cfg.PackagesPath!);
                roots.AddRange(cfg.CustomPackagesPaths);
                if (roots.Count > 0)
                    staleness = IndexStaleness.Check(repo, roots);
            }
            catch (Exception ex)
            {
                statusWarning = $"Could not read index: {ex.Message}";
            }
        }

        var warnings = new List<string>();
        if (staleness is { IsStale: true })
            warnings.Add("stale-index");

        var result = ToolResult<object>.Success(new
        {
            databasePath = cfg.DatabasePath,
            exists,
            sizeBytes,
            packagesPath = cfg.PackagesPath,
            extraPackagesPaths = cfg.CustomPackagesPaths.Count > 0 ? cfg.CustomPackagesPaths : null,
            workspacePath = cfg.WorkspacePath,
            customModels = cfg.CustomModels,
            labelLanguages = cfg.LabelLanguages,
            counts,
            staleness = staleness is null ? null : new
            {
                isStale = staleness.IsStale,
                detail = staleness.Detail,
                newestFile = staleness.NewestFile,
                newestFileMtimeUtc = staleness.NewestFileMtimeUtc,
                lastExtractedUtc = staleness.LastExtractedUtc,
                scannedFiles = staleness.ScannedFiles,
                truncated = staleness.Truncated,
            },
            warning = statusWarning,
        }, warnings: warnings.Count > 0 ? warnings : null);
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

        [CommandOption("--parallelism <N>")]
        [System.ComponentModel.Description("Number of models to parse in parallel. Defaults to half the CPU core count. SQLite writes are always serialized.")]
        public int? Parallelism { get; init; }

        [CommandOption("--extra-packages <PATH>")]
        [System.ComponentModel.Description("Additional PackagesLocalDirectory root(s) to include alongside --packages. Repeatable. Also reads D365FO_CUSTOM_PACKAGES_PATH (semicolon/comma-separated). Supports UDE setups with two separate metadata folders.")]
        public string[]? ExtraPackagesPaths { get; init; }

        [CommandOption("--index-source")]
        [System.ComponentModel.Description("Also full-text index X++ method bodies into MethodSourceFts (opt-in; enlarges the DB). Speeds up `find refs` and enables source search.")]
        public bool IndexSource { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
        var extraPaths = MergeExtraPaths(settings.ExtraPackagesPaths, cfg.CustomPackagesPaths);
        return ExtractCore(
            OutputMode.Resolve(settings.Output),
            settings.PackagesPath,
            settings.DatabasePath,
            settings.OnlyModel,
            settings.Since,
            extraPackagesPaths: extraPaths,
            parallelism: settings.Parallelism,
            indexSource: settings.IndexSource);
    }

    internal static int ExtractCore(
        OutputMode.Kind kind,
        string? packagesOverride,
        string? databaseOverride,
        string? onlyModel,
        string? sinceIso,
        IReadOnlyDictionary<string, string?>? fingerprintsByModel = null,
        int? parallelism = null,
        IReadOnlyList<string>? extraPackagesPaths = null,
        bool indexSource = false)
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
        var extractor = new MetadataExtractor { CaptureMethodSource = indexSource };
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

        // Effective model-level parallelism: default = half the CPU core count.
        // SQLite writes (ApplyExtract) are always serialized by the consumer
        // thread — only XML parsing runs in parallel.
        int effectiveParallelism = parallelism is > 0
            ? parallelism.Value
            : Math.Max(1, Environment.ProcessorCount / 2);

        // Pre-enumerate candidate model folders so we can report progress
        // *before* each model is parsed (useful when a single model like
        // ApplicationSuite takes many minutes).
        // For UDE setups (two separate PackagesLocalDirectory folders), callers
        // can pass extra roots via --extra-packages / D365FO_CUSTOM_PACKAGES_PATH.
        var modelDirs = EnumerateModelDirs(root, onlyModel).ToList();
        var validExtraRoots = new List<string>();
        foreach (var extra in extraPackagesPaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(extra)) continue;
            if (!Directory.Exists(extra)) continue; // silently skip missing extra roots
            validExtraRoots.Add(extra);
            modelDirs.AddRange(EnumerateModelDirs(extra, onlyModel));
        }
        // Deduplicate: same model dir from overlapping roots should only extract once.
        modelDirs = modelDirs
            .GroupBy(d => d, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Largest models first: with bounded parallelism, starting the big
        // models (ApplicationSuite & co.) early shortens the overall makespan —
        // small models fill the tail instead of a giant model running alone.
        modelDirs = modelDirs
            .OrderByDescending(ApproximateModelSize)
            .ToList();
        var showProgress = kind != OutputMode.Kind.Json && !System.Console.IsOutputRedirected;

        // Per-model fingerprint-based skip list for refresh (§1.1). Callers
        // pass the current DB fingerprints; we skip any model whose freshly
        // computed fingerprint already matches. `--force` is modelled as an
        // empty dictionary so every model runs.
        var dbFingerprints = fingerprintsByModel ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void ProcessAll(Action<string>? onStart, Action<string, ExtractBatch, long>? onDone)
        {
            // Producer-consumer pipeline: one or more producer threads parse model XML
            // while the single consumer (this thread) writes to SQLite. This overlaps
            // CPU-intensive XML parsing with I/O-intensive ApplyExtract calls.
            // Bounded capacity = effectiveParallelism + 1 limits peak RAM to roughly
            // (parallelism + 1) parsed batches at a time, capping memory usage while
            // keeping the consumer fed.
            var queue = new System.Collections.Concurrent.BlockingCollection<(
                ExtractBatch Batch, string Fp, DateTime StartedUtc, long ParseMs)>(
                boundedCapacity: effectiveParallelism + 1);
            var cts = new System.Threading.CancellationTokenSource();

            var producer = System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    // Parallel.ForEach over model directories: each iteration parses one
                    // model's XML fully before handing the ExtractBatch to the write queue.
                    // ExtractModel is itself parallel-internally (Parallel.Invoke over
                    // artifact kinds), so we avoid unbounded thread proliferation by using
                    // a degree capped to effectiveParallelism at the model level.
                    System.Threading.Tasks.Parallel.ForEach(
                        modelDirs,
                        new System.Threading.Tasks.ParallelOptions
                        {
                            MaxDegreeOfParallelism = effectiveParallelism,
                            CancellationToken = cts.Token,
                        },
                        modelDir =>
                        {
                            var model = Path.GetFileName(modelDir)!;
                            var fp = ComputeFingerprint(modelDir, cfg.LabelLanguages);
                            if (since.HasValue && NewestMtime(modelDir) is { } newest && newest < since.Value)
                            {
                                System.Threading.Interlocked.Increment(ref skippedCount);
                                return;
                            }
                            if (dbFingerprints.TryGetValue(model, out var stored)
                                && !string.IsNullOrEmpty(stored)
                                && string.Equals(stored, fp, StringComparison.Ordinal))
                            {
                                System.Threading.Interlocked.Increment(ref skippedCount);
                                return;
                            }
                            var startedUtc = DateTime.UtcNow;
                            var parseSw = System.Diagnostics.Stopwatch.StartNew();
                            ExtractBatch batch;
                            try
                            {
                                batch = extractor.ExtractModel(modelDir, model, cfg.LabelLanguages, matcher.IsMatch(model));
                            }
                            catch { return; }
                            parseSw.Stop();
                            try { queue.Add((batch, fp, startedUtc, parseSw.ElapsedMilliseconds), cts.Token); }
                            catch (OperationCanceledException) { /* consumer cancelled; stop */ }
                        });
                }
                catch (OperationCanceledException) { /* consumer cancelled; normal exit */ }
                finally { queue.CompleteAdding(); }
            });

            try
            {
                foreach (var (batch, fp, startedUtc, parseMs) in queue.GetConsumingEnumerable())
                {
                    onStart?.Invoke(batch.Model);
                    var writeSw = System.Diagnostics.Stopwatch.StartNew();
                    repo.ApplyExtract(batch, fp, indexSource);
                    writeSw.Stop();
                    var elapsedMs = parseMs + writeSw.ElapsedMilliseconds;
                    repo.RecordExtractionRun(batch.Model, startedUtc, elapsedMs,
                        batch.Tables.Count, batch.Classes.Count, batch.Edts.Count,
                        batch.Enums.Count, batch.Labels.Count, batch.IsCustom);
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
                        // Split out so a slow extract can be attributed without a profiler:
                        // parse is XML off the packages folder, write is ApplyExtract's
                        // delete-and-reinsert against SQLite.
                        parseMs,
                        writeMs = writeSw.ElapsedMilliseconds,
                    });
                    onDone?.Invoke(batch.Model, batch, elapsedMs);
                }
            }
            finally
            {
                cts.Cancel(); // unblock producer if consumer exits early (exception or normal)
            }
            producer.GetAwaiter().GetResult(); // re-throw any unexpected producer exception
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

        // ApplyExtract only checkpoints once the WAL crosses its threshold, so flush what the
        // last models left behind rather than leaving it for whichever process opens next.
        repo.CheckpointWal();

        runSw.Stop();
        var totals = repo.CountAll();
        var result = ToolResult<object>.Success(new
        {
            packagesRoot = root,
            extraPackagesRoots = validExtraRoots.Count > 0 ? validExtraRoots : null,
            modelsProcessed = modelCount,
            modelsSkipped = skippedCount,
            since = since?.ToString("O"),
            customModelsMatched = customCount,
            customModelPatterns = cfg.CustomModels,
            elapsedMs = runSw.ElapsedMilliseconds,
            perModel = per,
            totals,
        });

        return RenderHelpers.Render(kind, result, _ =>
        {
            AnsiConsole.MarkupLine("[green]OK[/] index extract completed");
            AnsiConsole.MarkupLine($"models processed: [bold]{modelCount}[/], skipped: [bold]{skippedCount}[/], custom: [bold]{customCount}[/]");
            AnsiConsole.MarkupLine($"elapsed: [bold]{runSw.ElapsedMilliseconds}[/] ms");

            var table = new Table().Border(TableBorder.Rounded).Title("[bold]Index Totals[/]");
            table.AddColumn("Artifact");
            table.AddColumn(new TableColumn("Count").RightAligned());
            table.AddRow("Tables", totals.Tables.ToString());
            table.AddRow("Classes", totals.Classes.ToString());
            table.AddRow("EDTs", totals.Edts.ToString());
            table.AddRow("Enums", totals.Enums.ToString());
            table.AddRow("Menu Items", totals.MenuItems.ToString());
            table.AddRow("CoC Extensions", totals.Coc.ToString());
            table.AddRow("Labels", totals.Labels.ToString());
            AnsiConsole.Write(table);
        });
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

    /// <summary>
    /// Cheap per-model content fingerprint used by <c>d365fo index refresh</c>.
    /// Format: <c>"{fileCount}:{newestMtimeTicks}:{langs}"</c>. Sensitive enough
    /// to catch touches (re-export, rebase, partial sync) without paying the
    /// cost of hashing every byte. The trade-off is documented in ROADMAP §1.1.
    /// <para>
    /// Both <c>*.xml</c> and <c>*.label.txt</c> files are included so that
    /// label-only changes (new translations, deletions) are not silently skipped.
    /// The sorted <paramref name="labelLanguages"/> suffix ensures that adding
    /// a language to <c>D365FO_LABEL_LANGUAGES</c> forces re-extraction even
    /// when the underlying files have not changed.
    /// </para>
    /// </summary>
    internal static string ComputeFingerprint(string dir, IReadOnlyList<string>? labelLanguages = null)
    {
        long newestTicks = 0;
        int count = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (!f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                    && !f.EndsWith(".label.txt", StringComparison.OrdinalIgnoreCase))
                    continue;
                count++;
                try
                {
                    var t = File.GetLastWriteTimeUtc(f).Ticks;
                    if (t > newestTicks) newestTicks = t;
                }
                catch { }
            }
        }
        catch { }
        var langs = labelLanguages is { Count: > 0 }
            ? string.Join(",", labelLanguages.Select(l => l.ToLowerInvariant()).OrderBy(l => l))
            : "";
        return $"{count}:{newestTicks}:{langs}";
    }

    /// <summary>
    /// Merges CLI-specified extra packages paths with those from the environment.
    /// Returns null if the combined list is empty (to avoid allocations in the common case).
    /// </summary>
    internal static IReadOnlyList<string>? MergeExtraPaths(string[]? cliPaths, IReadOnlyList<string> envPaths)
    {
        var all = (cliPaths ?? Array.Empty<string>())
            .Concat(envPaths)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return all.Count > 0 ? all : null;
    }

    private static IEnumerable<string> EnumerateModelDirs(string packagesRoot, string? onlyModel)    {
        IEnumerable<string> SafeDirs(string d)
        {
            try { return Directory.EnumerateDirectories(d); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        // Same registry-driven marker list the extractor uses, so `index refresh` and
        // MetadataExtractor never disagree about what counts as a model directory.
        static bool HasAot(string dir)
        {
            foreach (var s in D365FO.Core.ObjectTypes.ObjectTypeRegistry.ModelMarkerFolders())
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

    /// <summary>
    /// Cheap model-size proxy for extract scheduling: XML count in the
    /// dominant artifact folders. Avoids a full recursive walk per model.
    /// </summary>
    private static int ApproximateModelSize(string modelDir)
    {
        int count = 0;
        foreach (var sub in new[] { "AxClass", "AxTable", "AxForm", "AxEdt" })
        {
            var dir = Path.Combine(modelDir, sub);
            if (!Directory.Exists(dir)) continue;
            try { count += Directory.EnumerateFiles(dir, "*.xml", SearchOption.TopDirectoryOnly).Count(); }
            catch { /* unreadable — treat as empty */ }
        }
        return count;
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

        [CommandOption("--parallelism <N>")]
        [System.ComponentModel.Description("Number of models to parse in parallel. Defaults to half the CPU core count.")]
        public int? Parallelism { get; init; }

        [CommandOption("--extra-packages <PATH>")]
        [System.ComponentModel.Description("Additional PackagesLocalDirectory root(s). Repeatable. Also reads D365FO_CUSTOM_PACKAGES_PATH.")]
        public string[]? ExtraPackagesPaths { get; init; }

        [CommandOption("--index-source")]
        [System.ComponentModel.Description("Also full-text index X++ method bodies into MethodSourceFts (opt-in; enlarges the DB). Speeds up `find refs` and enables source search.")]
        public bool IndexSource { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var since = settings.Since;
        IReadOnlyDictionary<string, string?>? fingerprints = null;
        if (!settings.Force)
        {
            // Per-model fingerprint skip (§1.1). Cheap even for tens of models
            // because the fingerprint is just (fileCount, newestMtimeTicks).
            try
            {
                var repo = RepoFactory.Create(settings.DatabasePath);
                fingerprints = repo.GetModelFingerprints();
            }
            catch
            {
                // Pre-v7 DB or opening failed — fall back to the --since path.
            }

            if (string.IsNullOrWhiteSpace(since))
            {
                var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
                if (File.Exists(cfg.DatabasePath))
                {
                    var dbMtime = File.GetLastWriteTimeUtc(cfg.DatabasePath);
                    since = (dbMtime - TimeSpan.FromMinutes(5)).ToString("O");
                }
            }
        }
        var cfg2 = D365FoSettings.FromEnvironment(settings.DatabasePath);
        var extraPaths = IndexExtractCommand.MergeExtraPaths(settings.ExtraPackagesPaths, cfg2.CustomPackagesPaths);
        return IndexExtractCommand.ExtractCore(
            OutputMode.Resolve(settings.Output),
            settings.PackagesPath,
            settings.DatabasePath,
            settings.OnlyModel,
            since,
            fingerprints,
            parallelism: settings.Parallelism,
            extraPackagesPaths: extraPaths,
            indexSource: settings.IndexSource);
    }
}

/// <summary>
/// Persisted extraction telemetry (ROADMAP §1.3). Reads from
/// <c>ExtractionRuns</c>, which <see cref="IndexExtractCommand"/> appends to
/// after every per-model extract.
/// </summary>
public sealed class IndexHistoryCommand : Command<IndexHistoryCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Filter to a single model name.")]
        public string? Model { get; init; }

        [CommandOption("-n|--limit <N>")]
        [System.ComponentModel.Description("Row cap (default 50).")]
        public int Limit { get; init; } = 50;
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create(settings.DatabasePath);
        var rows = repo.GetExtractionRuns(settings.Limit, settings.Model);
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            count = rows.Count,
            model = settings.Model,
            runs = rows.Select(r => new
            {
                runId = r.RunId,
                startedUtc = r.StartedUtc,
                model = r.Model,
                elapsedMs = r.ElapsedMs,
                tables = r.Tables,
                classes = r.Classes,
                edts = r.Edts,
                enums = r.Enums,
                labels = r.Labels,
                isCustom = r.IsCustom,
            }),
        }));
    }
}

/// <summary>
/// Runs VACUUM + ANALYZE on the SQLite index to reclaim space and refresh
/// query-planner statistics. Useful after large-scale re-extractions that
/// delete and re-insert many rows.
/// </summary>
public sealed class IndexOptimizeCommand : Command<IndexOptimizeCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var repo = RepoFactory.Create(settings.DatabasePath);
        var r = repo.Optimize();
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            sizeBeforeBytes = r.SizeBeforeBytes,
            sizeAfterBytes  = r.SizeAfterBytes,
            savedBytes      = r.SizeBeforeBytes - r.SizeAfterBytes,
            elapsedMs       = r.ElapsedMs,
        }), _ =>
        {
            long saved = r.SizeBeforeBytes - r.SizeAfterBytes;
            AnsiConsole.MarkupLine(
                $"[green]OK[/] VACUUM+ANALYZE complete in {r.ElapsedMs}ms " +
                $"(saved {saved / 1024.0:F1} KB)");
        });
    }
}

/// <summary>
/// Exports the index database as a GZip-compressed SQLite snapshot for
/// team sharing or CI artifact caching.
/// Usage: <c>d365fo index export --out index.gz</c>
/// </summary>
public sealed class IndexExportCommand : Command<IndexExportCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--out <PATH>")]
        [System.ComponentModel.Description("Destination .gz file path.")]
        public string? OutPath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
        if (!File.Exists(cfg.DatabasePath))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "INDEX_NOT_FOUND",
                $"Index not found at: {cfg.DatabasePath}",
                "Run 'd365fo index extract' first."));
        }

        var outPath = settings.OutPath
            ?? Path.ChangeExtension(cfg.DatabasePath, ".gz");
        outPath = Path.GetFullPath(outPath);
        var outDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // VACUUM INTO writes a clean, wal-checkpointed copy to a temp file so
        // the source DB stays open and consistent during the export.
        var tmpPath = outPath + ".tmp.sqlite";
        try
        {
            // Checkpoint + clean copy via VACUUM INTO (SQLite 3.27+), via Core.
            var repo = new D365FO.Core.Index.MetadataRepository(cfg.DatabasePath);
            repo.VacuumInto(tmpPath);

            // GZip compress the clean copy.
            using (var inStream  = File.OpenRead(tmpPath))
            using (var outStream = File.Create(outPath))
            using (var gz = new System.IO.Compression.GZipStream(
                outStream, System.IO.Compression.CompressionLevel.Optimal))
            {
                inStream.CopyTo(gz);
            }
        }
        finally
        {
            if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }
        }

        sw.Stop();
        var sizeBytes = new FileInfo(outPath).Length;
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            source      = cfg.DatabasePath,
            output      = outPath,
            sizeBytes,
            elapsedMs   = sw.ElapsedMilliseconds,
        }), _ => AnsiConsole.MarkupLine(
            $"[green]OK[/] exported {sizeBytes / 1024.0:F1} KB → {Markup.Escape(outPath)} ({sw.ElapsedMilliseconds}ms)"));
    }
}

/// <summary>
/// Imports a previously exported GZip-compressed index snapshot.
/// Usage: <c>d365fo index import --from index.gz</c>
/// </summary>
public sealed class IndexImportCommand : Command<IndexImportCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--from <PATH>")]
        [System.ComponentModel.Description("Source .gz file exported by 'd365fo index export'.")]
        public string? FromPath { get; init; }
    }

    // SQLite magic header: "SQLite format 3\0" (16 bytes).
    private static readonly byte[] SqliteMagic =
        System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        if (string.IsNullOrEmpty(settings.FromPath))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "MISSING_ARGUMENT",
                "Required option --from <PATH> was not provided."));
        }
        if (!File.Exists(settings.FromPath))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "FILE_NOT_FOUND",
                $"Import file not found: {settings.FromPath}"));
        }

        var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);
        var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.DatabasePath));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tmpPath = cfg.DatabasePath + ".import.tmp";
        try
        {
            using (var inStream  = File.OpenRead(settings.FromPath!))
            using (var gz        = new System.IO.Compression.GZipStream(inStream, System.IO.Compression.CompressionMode.Decompress))
            using (var outStream = File.Create(tmpPath))
            {
                gz.CopyTo(outStream);
            }

            // Validate the decompressed file is a real SQLite DB via magic bytes.
            var header = new byte[16];
            using (var fs = File.OpenRead(tmpPath))
            {
                if (fs.Read(header, 0, 16) < 16 || !header.SequenceEqual(SqliteMagic))
                    throw new InvalidDataException("Imported file does not appear to be a valid SQLite database.");
            }

            // Atomic replace: move existing to .bak, move import to final path.
            if (File.Exists(cfg.DatabasePath))
                File.Move(cfg.DatabasePath, cfg.DatabasePath + ".bak", overwrite: true);
            File.Move(tmpPath, cfg.DatabasePath, overwrite: false);
        }
        catch
        {
            if (File.Exists(tmpPath)) try { File.Delete(tmpPath); } catch { }
            throw;
        }

        sw.Stop();
        var sizeBytes = new FileInfo(cfg.DatabasePath).Length;
        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            source    = settings.FromPath,
            target    = cfg.DatabasePath,
            sizeBytes,
            elapsedMs = sw.ElapsedMilliseconds,
        }), _ => AnsiConsole.MarkupLine(
            $"[green]OK[/] imported {sizeBytes / 1024.0:F1} KB → {Markup.Escape(cfg.DatabasePath)} ({sw.ElapsedMilliseconds}ms)"));
    }
}
