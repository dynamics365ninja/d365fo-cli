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
        [CommandArgument(0, "<CASE_ID>")]
        public string CaseId { get; init; } = "";

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
        var @case = EvalCaseCatalog.Find(cases, settings.CaseId);
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
                var fixtureDir = EvalPaths.FixtureDir(root!);
                if (!Directory.Exists(fixtureDir))
                {
                    return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                        "EVAL_FIXTURE_MISSING", $"Fixture directory not found: {fixtureDir}"));
                }
                var extractor = new MetadataExtractor();
                foreach (var batch in extractor.ExtractAll(fixtureDir))
                    repo.ApplyExtract(batch);
            }

            var args = @case.CanonicalArgs.Concat(new[] { "--out", outPath, "--overwrite", "--output", "json" }).ToArray();
            var (exitCode, replayError) = RunReplay(dllPath, dbPath, args);

            if (replayError is not null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "EVAL_GENERATE_FAILED", $"Replay of `d365fo {string.Join(' ', args)}` threw: {replayError}"));
            }
            if (exitCode != 0 || !File.Exists(outPath))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "EVAL_GENERATE_FAILED", $"`d365fo {string.Join(' ', args)}` exited {exitCode} or produced no output file at {outPath}."));
            }

            var score = EvalScorer.Score(@case, outPath, EvalPaths.GoldensDir(root!), repo);

            if (settings.Write)
            {
                var record = new EvalCorpusRecord(
                    RunId: BuildRunId(@case.Id),
                    CaseId: @case.Id,
                    Tier: @case.Tier,
                    TimestampUtc: DateTimeOffset.UtcNow,
                    Source: "replay",
                    Score: score,
                    Classification: null,
                    Note: settings.Note);
                EvalCorpusStore.Append(EvalPaths.CorpusRunsDir(root!), record);
            }

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                caseId = @case.Id,
                tier = @case.Tier,
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
                recorded = settings.Write,
            }));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort temp cleanup */ }
        }
    }

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
    private static (int ExitCode, string? Error) RunReplay(string dllPath, string dbPath, string[] args)
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
