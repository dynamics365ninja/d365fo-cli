using System.Text.RegularExpressions;
using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Validation;
using D365FO.Cli.Commands.Ops;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// The L3 build oracle (plan item 4.2): provision every reviewed golden into a
/// throwaway model, compile it, attribute the compiler's diagnostics back to the
/// case that produced each object, and persist the verdicts to
/// <c>eval/golden-build-verification.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// L0–L2 replay proves a golden is <em>structurally</em> what we reviewed. It
/// cannot prove the X++ inside it compiles — <c>XppValidator</c> is a regex BP
/// linter, not a compiler. This command is the only thing in the loop that can,
/// and it therefore only runs where a real compiler exists: Windows, with a D365FO
/// installation. Everywhere else it refuses rather than reporting a verdict it did
/// not collect.
/// </para>
/// <para>
/// <see cref="DefaultCompilerArgs"/> is read off <c>xppc.exe -?</c> on a real
/// installation (X++ Compiler 7.0.7996.33) and was verified end to end against it;
/// <c>--compiler-args</c> overrides it wholesale should a platform update move the
/// surface. So that an argument mistake can never masquerade as a compile failure,
/// output that looks like the tool's own usage text is reported as
/// <c>EVAL_BUILD_INVOCATION</c> and <em>no</em> verdicts are written: every case
/// stays unevaluated rather than being marked broken by a bad command line.
/// </para>
/// </remarks>
public sealed class EvalVerifyBuildCommand : Command<EvalVerifyBuildCommand.Settings>
{
    /// <summary>
    /// Placeholders: <c>{metadata}</c> the provisioned metadata store (the work dir,
    /// holding the goldens model and the fixture reference models), <c>{packages}</c>
    /// the installation to resolve references against, <c>{model}</c> the throwaway
    /// model name, <c>{output}</c> a temp bin directory, <c>{log}</c> the log file
    /// <see cref="XppcDiagnostics"/> parses.
    /// </summary>
    public const string DefaultCompilerArgs =
        "-metadata={metadata} -modelmodule={model} -output={output} -referencefolder={packages} -refPath={output} -log={log}";

    /// <summary>xppc printing its own usage means the arguments were rejected, not that the code is broken.</summary>
    private static readonly Regex UsageTextPattern =
        new(@"(?im)^\s*(usage\s*:|unknown\s+(option|argument|switch)|unrecognized\s+(option|argument))", RegexOptions.Compiled);

    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--case <CASE_ID>")]
        [System.ComponentModel.Description("Verify a single case instead of the whole catalog.")]
        public string? CaseId { get; init; }

        [CommandOption("--provision-only")]
        [System.ComponentModel.Description("Materialise the goldens into a model layout and stop. Runs on any OS; no compiler needed.")]
        public bool ProvisionOnly { get; init; }

        [CommandOption("--work-dir <PATH>")]
        [System.ComponentModel.Description("Where to provision the throwaway model (default: a temp directory, deleted afterwards).")]
        public string? WorkDir { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Throwaway model name (default: D365FoCliEvalGoldens).")]
        public string? Model { get; init; }

        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("PackagesLocalDirectory to resolve references against (default: D365FO_PACKAGES_PATH).")]
        public string? PackagesPath { get; init; }

        [CommandOption("--compiler <PATH>")]
        [System.ComponentModel.Description("xppc.exe (default: <packages>\\Bin\\xppc.exe).")]
        public string? CompilerPath { get; init; }

        [CommandOption("--compiler-args <TEMPLATE>")]
        [System.ComponentModel.Description("Override the argument template. Placeholders: {metadata} {packages} {model} {output} {log}.")]
        public string? CompilerArgs { get; init; }

        [CommandOption("--no-fixture")]
        [System.ComponentModel.Description("Do not provision tests/Samples/MiniAot beside the goldens (they will then reference objects that do not exist).")]
        public bool NoFixture { get; init; }

        [CommandOption("--write")]
        [System.ComponentModel.Description("Persist eval/golden-build-verification.json and append corpus records carrying the build dimension.")]
        public bool Write { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var (root, failure) = EvalPathsResolver.Resolve(kind);
        if (failure is int f) return f;

        var (allCases, catalogErrors) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));
        if (catalogErrors.Count > 0)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "EVAL_CATALOG_INVALID", $"Catalog errors: {string.Join("; ", catalogErrors)}"));
        }

        var cases = allCases;
        if (!string.IsNullOrWhiteSpace(settings.CaseId))
        {
            var one = EvalCaseCatalog.Find(allCases, settings.CaseId!);
            if (one is null)
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "EVAL_CASE_NOT_FOUND", $"No eval case '{settings.CaseId}' found."));
            }
            cases = [one];
        }

        var modelName = string.IsNullOrWhiteSpace(settings.Model) ? "D365FoCliEvalGoldens" : settings.Model!;
        var ephemeral = string.IsNullOrWhiteSpace(settings.WorkDir);
        var workDir = ephemeral
            ? Path.Combine(Path.GetTempPath(), $"d365fo-l3-{Guid.NewGuid():N}")
            : Path.GetFullPath(settings.WorkDir!);

        try
        {
            Directory.CreateDirectory(workDir);

            var model = L3ModelProvisioner.Provision(cases, EvalPaths.GoldensDir(root!), workDir, modelName);

            // The fixture's objects go into the same module as the goldens: the goldens
            // bind to its tables, and one module has no cross-module visibility question
            // to get wrong.
            var fixtureFiles = settings.NoFixture
                ? []
                : L3ModelProvisioner.ProvisionFixtureInto(
                    EvalPaths.FixtureDir(root!), Path.Combine(model.ModelRoot, modelName));

            if (settings.ProvisionOnly)
            {
                // Deliberately keeps the directory: the point of --provision-only is to
                // hand a real model layout to a compiler (or a human) afterwards.
                ephemeral = false;
                return RenderHelpers.Render(kind, ToolResult<object>.Success(new
                {
                    provisionOnly = true,
                    modelRoot = model.ModelRoot,
                    fixtureFiles = fixtureFiles.Count,
                    artifacts = model.Artifacts.Count,
                    cases = model.Artifacts.Select(a => a.CaseId).Distinct().Count(),
                    skipped = model.Skipped,
                    files = model.Artifacts.Select(a => new { caseId = a.CaseId, path = a.RelativePath, root = a.RootElement }),
                }));
            }

            var guard = WindowsGuard.Check("d365fo eval verify-build");
            if (guard is not null) return RenderHelpers.Render(kind, guard);

            var packagesRoot = settings.PackagesPath
                ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH");
            if (string.IsNullOrWhiteSpace(packagesRoot) || !Directory.Exists(packagesRoot))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "PACKAGES_NOT_FOUND",
                    $"No PackagesLocalDirectory at: {packagesRoot ?? "(unset)"}",
                    "Set D365FO_PACKAGES_PATH (or pass --packages) to the installation the goldens should compile against."));
            }

            var compiler = settings.CompilerPath ?? Path.Combine(packagesRoot, "Bin", "xppc.exe");
            if (!File.Exists(compiler))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "XPPC_NOT_FOUND",
                    $"xppc.exe not found at: {compiler}",
                    "Pass --compiler, or point --packages at the FrameworkDirectory that contains Bin\\xppc.exe."));
            }

            var outputDir = Path.Combine(workDir, "bin");
            var logPath = Path.Combine(workDir, $"Dynamics.AX.{modelName}.xppc.log");
            Directory.CreateDirectory(outputDir);

            var template = string.IsNullOrWhiteSpace(settings.CompilerArgs) ? DefaultCompilerArgs : settings.CompilerArgs!;

            // {metadata} is the work dir, not the model root: it is the metadata *store*
            // (one directory per package) the compiler enumerates.
            var args = Expand(template, workDir, packagesRoot!, modelName, outputDir, logPath);

            var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(compiler, args);
            var log = stdout + "\n" + stderr;
            if (File.Exists(logPath))
            {
                try { log += "\n" + File.ReadAllText(logPath); }
                catch (IOException) { /* the compiler may still hold the handle; stdout is enough */ }
            }

            var diagnostics = XppcDiagnostics.Parse(log);

            if (diagnostics.Count == 0 && UsageTextPattern.IsMatch(log))
            {
                return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "EVAL_BUILD_INVOCATION",
                    "The compiler rejected the argument list, so no case was actually compiled — no verdicts were written.",
                    $"Args: {string.Join(' ', args)}\n\n{Tail(log, 20)}"));
            }

            var (verdicts, unattributed) = BuildVerdictAttribution.Attribute(model, cases, diagnostics);
            var verification = GoldenBuildVerification.Build(
                host: Environment.MachineName,
                packagesRoot: packagesRoot!,
                compiler: compiler,
                compilerArgs: string.Join(' ', args),
                modelName: modelName,
                capturedUtc: DateTimeOffset.UtcNow,
                cases: verdicts);

            string? persisted = null;
            if (settings.Write)
            {
                persisted = Path.Combine(root!, "eval", "golden-build-verification.json");
                verification.Save(persisted);
                RecordCorpus(root!, cases, verdicts);
            }

            var payload = new
            {
                compiler,
                args = string.Join(' ', args),
                exitCode = exit,
                elapsedMs = (long)elapsed.TotalMilliseconds,
                modelRoot = model.ModelRoot,
                total = verification.Total,
                clean = verification.Clean,
                failed = verification.Failed,
                skipped = verification.Skipped,
                staleSymbols = XppcDiagnostics.IndicatesStaleSymbols(log),
                unattributedDiagnostics = unattributed.Select(d => new { severity = d.Severity, obj = d.Object, message = d.Message }),
                provisionSkips = model.Skipped,
                persisted,
                cases = verdicts.Select(v => new
                {
                    caseId = v.CaseId,
                    verdict = v.Verdict,
                    errors = v.Errors,
                    warnings = v.Warnings,
                    ruleIds = v.RuleIds,
                    messages = v.Messages,
                    skipReason = v.SkipReason,
                }),
            };

            return verification.Failed == 0
                ? RenderHelpers.Render(kind, ToolResult<object>.Success(payload))
                : RenderHelpers.Render(kind, ToolResult<object>.Fail(
                    "EVAL_BUILD_FAILED",
                    $"{verification.Failed} of {verification.Total} goldens do not compile.",
                    D365Json.Serialize(payload, indented: true)));
        }
        finally
        {
            if (ephemeral)
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best-effort temp cleanup */ }
            }
        }
    }

    /// <summary>
    /// Corpus records for the build dimension only: the offline dimensions are not
    /// re-scored here, so they stay null rather than being copied from an earlier,
    /// possibly stale replay.
    /// </summary>
    private static void RecordCorpus(string root, IReadOnlyList<EvalCase> cases, IReadOnlyList<GoldenBuildCaseVerdict> verdicts)
    {
        var byId = cases.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var v in verdicts)
        {
            if (v.Verdict == BuildVerdict.Skipped) continue;
            if (!byId.TryGetValue(v.CaseId, out var @case)) continue;

            var clean = v.Verdict == BuildVerdict.Clean;
            var score = new EvalScoreCard(
                XppClean: null, XppErrors: 0,
                ReferencesClean: null, ReferenceErrors: 0,
                GoldenMatch: true,
                GoldenDiff: XmlGoldenDiff.Empty,
                BuildClean: clean,
                BuildErrors: v.Errors);

            EvalCorpusStore.Append(EvalPaths.CorpusRunsDir(root), new EvalCorpusRecord(
                RunId: $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}__{@case.Id}__{Guid.NewGuid():N}",
                CaseId: @case.Id,
                Tier: @case.Tier,
                TimestampUtc: DateTimeOffset.UtcNow,
                Source: "build-oracle",
                Score: score,
                // The golden was reviewed and the replay matches it, so code that does not
                // compile is the scaffolder emitting uncompilable X++ — a tool defect.
                Classification: clean ? null : EvalTriage.ToolDefect,
                Note: clean
                    ? null
                    : $"xppc reported {v.Errors} error(s) against this golden: {string.Join("; ", v.Messages.Take(3))}"));
        }
    }

    /// <summary>
    /// Splits the template into argv <em>before</em> substituting, so a packages path
    /// containing a space stays one argument. <c>ProcessRunner</c> passes each element
    /// through <c>ArgumentList</c>, which quotes them itself.
    /// </summary>
    internal static string[] Expand(string template, string metadata, string packages, string model, string output, string log) =>
        template
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token
                .Replace("{metadata}", metadata)
                .Replace("{packages}", packages)
                .Replace("{model}", model)
                .Replace("{output}", output)
                .Replace("{log}", log))
            .ToArray();

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Replace("\r\n", "\n").Split('\n').TakeLast(lines));
}
