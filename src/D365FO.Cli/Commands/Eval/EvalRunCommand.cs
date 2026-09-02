using System.Diagnostics;
using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// Deterministic, agent-free replay of a case's <c>canonical_args</c>:
/// builds a disposable temp index (+ fixture data when the case needs it),
/// runs the exact args through a fresh <c>d365fo</c> child process, then
/// scores the result against the golden. This is the fast CI gate — the
/// eval-runner agent exists to test the harder question (does an agent,
/// given only the case's natural-language <c>instruction</c>, arrive at the
/// same result on its own).
/// </summary>
public sealed class EvalRunCommand : Command<EvalRunCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "[CASE_ID]")]
        public string? CaseId { get; init; }

        [CommandOption("--all")]
        [System.ComponentModel.Description("Replay every case that has canonical_args (cases without them are reported as skipped). Exits non-zero if any case fails.")]
        public bool All { get; init; }

        [CommandOption("--write")]
        [System.ComponentModel.Description("Append a corpus record to eval/corpus/runs/.")]
        public bool Write { get; init; }

        [CommandOption("--note <TEXT>")]
        public string? Note { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        var (cases, catalogErrors) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));

        if (settings.All)
        {
            if (!string.IsNullOrWhiteSpace(settings.CaseId))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    D365FoErrorCodes.BadInput, "Pass either <CASE_ID> or --all, not both."));
            }
            return RunAll(kind, root!, cases, catalogErrors, settings);
        }

        if (string.IsNullOrWhiteSpace(settings.CaseId))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput, "A <CASE_ID> is required (or pass --all to replay the whole catalog)."));
        }

        var @case = EvalCaseCatalog.Find(cases, settings.CaseId!);
        if (@case is null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_CASE_NOT_FOUND",
                $"No eval case '{settings.CaseId}' found." +
                (catalogErrors.Count > 0 ? $" Catalog errors: {string.Join("; ", catalogErrors)}" : "")));
        }

        if (@case.CanonicalArgs is null || @case.CanonicalArgs.Count == 0)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_NO_CANONICAL_ARGS",
                $"Case '{@case.Id}' has no canonical_args.",
                "It can only be exercised by the eval-runner agent driving its natural-language `instruction`, then scored with `d365fo eval score`."));
        }

        string dllPath;
        try
        {
            dllPath = LocateCliDll();
        }
        catch (FileNotFoundException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("EVAL_CLI_NOT_FOUND", ex.Message));
        }

        var (score, replayFailure) = Replay(@case, root!, dllPath, settings.Write, settings.Note);
        if (replayFailure is not null)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(replayFailure.Code, replayFailure.Message));

        return RenderHelpers.Render(kind, ToolResult<object>.Success(Payload(@case, score!, settings.Write)));
    }

    /// <summary>
    /// Replays every case that has <c>canonical_args</c>. Cases without them are agent-only
    /// (scored via <c>eval score</c>) and are reported as skipped rather than failed.
    /// Returns a non-zero exit code when any case fails, which is what makes this usable
    /// as a CI gate.
    /// </summary>
    private static int RunAll(
        OutputMode.Kind kind, string root,
        IReadOnlyList<EvalCase> cases, IReadOnlyList<string> catalogErrors,
        Settings settings)
    {
        string dllPath;
        try
        {
            dllPath = LocateCliDll();
        }
        catch (FileNotFoundException ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("EVAL_CLI_NOT_FOUND", ex.Message));
        }

        var results = new List<object>();
        var failed = new List<string>();
        var skipped = new List<string>();
        var passed = 0;

        foreach (var @case in cases.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (@case.CanonicalArgs is null || @case.CanonicalArgs.Count == 0)
            {
                skipped.Add(@case.Id);
                continue;
            }

            var (score, replayFailure) = Replay(@case, root, dllPath, settings.Write, settings.Note);
            if (replayFailure is not null)
            {
                failed.Add(@case.Id);
                results.Add(new { caseId = @case.Id, tier = @case.Tier, ok = false, error = replayFailure.Code, detail = replayFailure.Message });
                continue;
            }

            // Golden mismatch is the regression signal: goldens are captured from reviewed
            // output, so a diff means behaviour changed. Validator errors are reported per
            // case but do not fail the gate — some cases legitimately reference objects the
            // mini fixture AOT does not contain, and the corpus tracks those counts as their
            // own axis (see EvalReport).
            var ok = score!.GoldenMatch;
            if (ok) passed++; else failed.Add(@case.Id);
            results.Add(Payload(@case, score, settings.Write, ok));
        }

        var summary = new
        {
            total = results.Count,
            passed,
            failed = failed.Count,
            skippedAgentOnly = skipped,
            failedCases = failed,
            catalogErrors,
            recorded = settings.Write,
            cases = results,
        };

        return failed.Count == 0 && catalogErrors.Count == 0
            ? RenderHelpers.Render(kind, ToolResult<object>.Success(summary))
            : RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_REGRESSION",
                failed.Count > 0
                    ? $"{failed.Count} of {results.Count} replayed cases failed: {string.Join(", ", failed)}."
                    : $"Catalog errors: {string.Join("; ", catalogErrors)}",
                D365Json.Serialize(summary, indented: true)));
    }

    private sealed record ReplayFailure(string Code, string Message);

    /// <summary>
    /// One case end to end: disposable temp index (+ fixture data when the case needs it),
    /// canonical args through a fresh child process, score against the golden, optional
    /// corpus record.
    /// </summary>
    private static (EvalScoreCard? Score, ReplayFailure? Failure) Replay(
        EvalCase @case, string root, string dllPath, bool write, string? note)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"d365fo-eval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        var outPath = Path.Combine(workDir, "actual.xml");
        var dbPath = Path.Combine(workDir, "index.sqlite");

        try
        {
            var repo = new MetadataRepository(dbPath);
            repo.EnsureSchema();

            if (@case.RequiresFixtureIndex)
            {
                var fixtureDir = EvalPaths.FixtureDir(root);
                if (!Directory.Exists(fixtureDir))
                    return (null, new ReplayFailure("EVAL_FIXTURE_MISSING", $"Fixture directory not found: {fixtureDir}"));

                var extractor = new MetadataExtractor();
                foreach (var batch in extractor.ExtractAll(fixtureDir))
                    repo.ApplyExtract(batch);
            }

            // A case that augments an existing table starts FROM an artefact instead of producing
            // one: the seed is copied in as the run's output file and merged into in place, so
            // everything downstream (scoring, capture) sees the same actual.xml it always did.
            string[] args;
            if (@case.ApplyToSeed is { Length: > 0 } seedName)
            {
                var seed = Path.Combine(EvalPaths.SeedsDir(root), seedName);
                if (!File.Exists(seed))
                    return (null, new ReplayFailure("EVAL_SEED_MISSING", $"Seed artefact not found: {seed}"));
                File.Copy(seed, outPath);
                args = @case.CanonicalArgs!.Concat(new[] { "--apply-to", outPath, "--output", "json" }).ToArray();
            }
            else
            {
                args = @case.CanonicalArgs!.Concat(new[] { "--out", outPath, "--overwrite", "--output", "json" }).ToArray();
            }

            var (exitCode, replayError) = RunReplay(dllPath, dbPath, args, root);

            if (replayError is not null)
                return (null, new ReplayFailure("EVAL_GENERATE_FAILED", $"Replay of `d365fo {string.Join(' ', args)}` threw: {replayError}"));
            if (exitCode != 0 || !File.Exists(outPath))
                return (null, new ReplayFailure("EVAL_GENERATE_FAILED", $"`d365fo {string.Join(' ', args)}` exited {exitCode} or produced no output file at {outPath}."));

            var score = EvalScorer.Score(@case, outPath, EvalPaths.GoldensDir(root), repo, producedDir: workDir);

            if (write)
            {
                // Replay is agent-free, so the scorecard alone identifies the class
                // (MODEL_ERROR is impossible without a model in the loop). It is still
                // only a hypothesis — the eval-improver confirms it by reproducing the
                // defect as a failing test before touching source.
                var (classification, triageNote) = EvalTriage.Hypothesize(@case, score, "replay");
                var record = new EvalCorpusRecord(
                    RunId: BuildRunId(@case.Id),
                    CaseId: @case.Id,
                    Tier: @case.Tier,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Source: "replay",
                    Score: score,
                    Classification: classification,
                    Note: string.IsNullOrWhiteSpace(note) ? triageNote : note);
                EvalCorpusStore.Append(EvalPaths.CorpusRunsDir(root), record);
            }

            return (score, null);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

    private static object Payload(EvalCase @case, EvalScoreCard score, bool recorded, bool? ok = null) => new
    {
        caseId = @case.Id,
        tier = @case.Tier,
        ok,
        classification = EvalTriage.Hypothesize(@case, score, "replay").Classification,
        xppClean = score.XppClean,
        xppErrors = score.XppErrors,
        referencesClean = score.ReferencesClean,
        referenceErrors = score.ReferenceErrors,
        goldenMatch = score.GoldenMatch,
        goldenDiff = new
        {
            missing = score.GoldenDiff.Missing,
            extra = score.GoldenDiff.Extra,
            changed = score.GoldenDiff.Changed.Select(c => new { path = c.Path, expected = c.Expected, actual = c.Actual }),
        },
        recorded,
    };

    private static string BuildRunId(string caseId)
        => $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}__{caseId}__{Guid.NewGuid():N}";

    /// <summary>
    /// <c>d365fo.dll</c> (the D365FO.Cli project's <c>AssemblyName</c> is
    /// <c>d365fo</c>, not the project name) next to the currently executing
    /// assembly — present whether this is running as the real <c>d365fo</c>
    /// apphost (it IS the running assembly) or in-process inside
    /// <c>D365FO.Cli.Tests</c> (copied there as a build output of the project
    /// reference).
    /// </summary>
    private static string LocateCliDll()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "d365fo.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not locate d365fo.dll next to the running assembly ({AppContext.BaseDirectory}).");
        return path;
    }

    /// <summary>
    /// Runs <paramref name="args"/> as a genuinely separate <c>dotnet
    /// D365FO.Cli.dll ...</c> child process — not in-process against a second
    /// <see cref="CliApp"/> instance. Deliberate: an earlier in-process design
    /// (build a fresh <see cref="CliApp"/> and call <c>RunAsync</c> directly)
    /// was found to permanently corrupt Spectre.Console's process-wide
    /// <c>AnsiConsole</c> singleton for the rest of the process — any later
    /// Table-mode render in the same process (another eval run, another CLI
    /// command, another test) would silently produce empty output. A real
    /// child process makes that entire class of shared-static-state hazard
    /// moot, and is arguably the more honest replay anyway: byte-for-byte the
    /// same invocation a human or agent would actually run. <c>D365FO_INDEX_DB</c>
    /// is passed via the child's environment rather than the parent's, so nothing
    /// here needs to save/restore process-wide state either.
    /// </summary>
    private static (int ExitCode, string? Error) RunReplay(string dllPath, string dbPath, string[] args, string root)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // Canonical args may name files the repository ships (`--source eval/seeds/…`),
                // so the child resolves relative paths against the repository root rather than
                // wherever the runner happened to be started from.
                WorkingDirectory = root,
            };
            psi.ArgumentList.Add(dllPath);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["D365FO_INDEX_DB"] = dbPath;

            using var process = Process.Start(psi);
            if (process is null)
                return (-1, "Process.Start returned null.");

            process.WaitForExit();
            return (process.ExitCode, null);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
