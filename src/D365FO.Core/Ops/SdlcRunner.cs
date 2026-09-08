using System.Diagnostics;
using System.Text.RegularExpressions;
using D365FO.Core.Eval;
using D365FO.Core.Validation;

namespace D365FO.Core.Ops;

/// <summary>
/// The four Windows-only D365FO developer tools — MSBuild, SyncEngine, SysTestConsole and
/// xppbp — invoked and their output turned into structured results.
/// </summary>
/// <remarks>
/// This was four command bodies in the CLI, which is why the build was something only a
/// developer with a shell could run: an agent on the MCP surface could scaffold an object and
/// then had no way to find out whether it compiles, which is the one question that matters after
/// a write. The logic is here so both surfaces invoke the same tools with the same arguments and
/// read the same verdicts — the arguments in particular are not obvious (SysTestConsole takes
/// slash-prefixed, colon-joined options; xppbp needs a fallback for the legacy
/// <c>-packagesroot:</c> flag), and a second copy of them would be a second thing to get wrong.
/// </remarks>
public static class SdlcRunner
{
    /// <summary>Refuse cleanly off Windows, so a caller can branch without reading stderr.</summary>
    public static ToolResult<object>? WindowsGuard(string toolName)
    {
        if (OperatingSystem.IsWindows()) return null;
        return ToolResult<object>.Fail(
            "UNSUPPORTED_PLATFORM",
            $"{toolName} requires Windows with a D365FO developer VM.",
            "Run this command on the D365FO VM. The CLI is cross-platform for metadata and scaffolding, but build/sync/test/bp invoke Windows-only executables.");
    }

    // ----------------------------------------------------------------- build

    /// <param name="msbuildPath">
    /// MSBuild executable. Omitted, it is resolved by <see cref="BuildTooling.ResolveMsBuild"/>:
    /// the Visual Studio MSBuild before the one on PATH, because on a developer VM PATH resolves
    /// <c>msbuild.exe</c> to the .NET Framework build, which cannot host the Dynamics build tasks.
    /// </param>
    /// <param name="projectPath">Project or solution to build; omitted builds whatever is in the working directory.</param>
    /// <param name="configuration">MSBuild configuration, <c>Debug</c> by default.</param>
    /// <param name="xppcLogPath">
    /// Additional <c>Dynamics.AX.&lt;Model&gt;.xppc.log</c> to parse. The X++ compiler reports
    /// through its own format, which MSBuild's stdout only partly carries.
    /// </param>
    public static ToolResult<object> Build(
        string? msbuildPath, string? projectPath, string configuration = "Debug", string? xppcLogPath = null)
    {
        var tool = BuildTooling.ResolveMsBuild(msbuildPath);
        if (tool.Path is null)
        {
            return ToolResult<object>.Fail("MSBUILD_NOT_FOUND",
                tool.Problem ?? "msbuild.exe could not be resolved.",
                "Run `d365fo doctor` to see every build tool path, then set D365FO_MSBUILD_PATH or pass --msbuild.");
        }

        var msbuild = tool.Path;
        var args = new List<string>();
        if (!string.IsNullOrEmpty(projectPath)) args.Add(projectPath!);
        args.Add($"/p:Configuration={configuration}");
        args.Add("/nologo");

        var (exit, stdout, stderr, elapsed) = Run(msbuild, args);
        // stderr is scanned too: a toolchain failure (MSB1009, a missing targets import) is
        // often all MSBuild writes, and reading only stdout reported it as zero errors.
        var output = string.IsNullOrEmpty(stderr) ? stdout : stdout + "\n" + stderr;
        var errors = ParseMsBuildDiagnostics(output, "error");
        var warnings = ParseMsBuildDiagnostics(output, "warning");

        // Structured xppc diagnostics: the X++ compiler reports through its own
        // "Compile Error: … dynamics://Model/Object/member: [(l,c)]: msg" format,
        // both inside MSBuild stdout and in the -log file. Parsing them gives the
        // agent {object, member, line, column, message, hint} instead of raw text.
        var xppcSource = stdout;
        if (!string.IsNullOrEmpty(xppcLogPath) && File.Exists(xppcLogPath))
        {
            try { xppcSource += "\n" + File.ReadAllText(xppcLogPath); }
            catch { /* unreadable log — stdout still parsed */ }
        }
        var xppc = XppcDiagnostics.Parse(xppcSource);
        var staleSymbols = XppcDiagnostics.IndicatesStaleSymbols(xppcSource);

        var payload = new
        {
            buildSucceeded = exit == 0,
            exitCode = exit,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            // Which MSBuild ran, and whether it is one that can build X++ at all. A build that
            // fails because PATH handed us the .NET Framework MSBuild reports MSB4062, which
            // reads like a compiler error; naming the executable here makes it one question.
            msbuild = new { path = tool.Path, source = tool.Source, problem = tool.Problem },
            errorCount = errors.Count,
            warningCount = warnings.Count,
            errors,
            warnings,
            xppcDiagnostics = xppc.Count == 0 ? null : xppc
                .Select(d => new
                {
                    severity = d.Severity,
                    kind = d.Kind,
                    model = d.Model,
                    @object = d.Object,
                    member = d.Member,
                    line = d.Line,
                    column = d.Column,
                    message = d.Message,
                    hint = d.Hint,
                })
                .ToList<object>(),
            staleSymbols = staleSymbols
                ? "xppc reports stale symbols from a previous incremental build — run a Full Build."
                : null,
            stderrTail = exit == 0 ? null : Tail(stderr, 5),
            tail = Tail(stdout, 20),
        };

        // A failed build keeps the full structured payload — the diagnostics are wanted exactly
        // when it fails — and says so in a warning rather than in an error envelope that would
        // throw the diagnostics away.
        var buildWarnings = new List<string>();
        if (exit != 0) buildWarnings.Add("build-failed");
        if (tool.Problem is not null) buildWarnings.Add($"msbuild-unfit: {tool.Problem}");

        return ToolResult<object>.Success(payload, buildWarnings.Count == 0 ? null : buildWarnings);
    }

    // ------------------------------------------------------------------ sync

    public static ToolResult<object> Sync(string? syncToolPath, bool full)
    {
        var sync = string.IsNullOrWhiteSpace(syncToolPath) ? "SyncEngine.exe" : syncToolPath!;
        var args = new List<string> { "-syncmode=" + (full ? "fullall" : "partiallist") };
        var (exit, stdout, stderr, elapsed) = Run(sync, args);
        return exit == 0
            ? ToolResult<object>.Success(new
            {
                exitCode = exit,
                elapsedMs = (long)elapsed.TotalMilliseconds,
                tail = stdout.Split('\n').TakeLast(20).ToArray(),
            })
            : ToolResult<object>.Fail("SYNC_FAILED", $"SyncEngine exited with {exit}.",
                string.Join('\n', stderr.Split('\n').TakeLast(5)));
    }

    // ------------------------------------------------------------ SysTest run

    /// <summary>The verdict, and whether it was clean — callers map "not clean" to an exit code.</summary>
    public sealed record TestRunOutcome(ToolResult<object> Result, bool? Clean);

    public static TestRunOutcome RunTests(
        string? runnerPath, IReadOnlyList<string>? testClasses, string? granularity,
        string? resultsPath, bool parallel)
    {
        var runner = string.IsNullOrWhiteSpace(runnerPath) ? "SysTestConsole.exe" : runnerPath!;

        var classes = (testClasses ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        var args = new List<string>();
        if (classes.Count > 0) args.Add("/test:" + string.Join(',', classes));
        if (!string.IsNullOrWhiteSpace(granularity)) args.Add("/granularity:" + granularity);
        if (!string.IsNullOrWhiteSpace(resultsPath)) args.Add("/xml:" + resultsPath);
        if (parallel) args.Add("/parallel");

        var (exit, stdout, stderr, elapsed) = Run(runner, args);

        var resultsWritten = !string.IsNullOrWhiteSpace(resultsPath) && File.Exists(resultsPath!);

        if (exit != 0)
        {
            return new TestRunOutcome(
                ToolResult<object>.Fail("TESTS_FAILED", $"Runner exited with {exit}.",
                    string.Join('\n', stderr.Split('\n').TakeLast(5))),
                Clean: null);
        }

        // The verdict comes from the runner's own result document, not from its exit code: a
        // run that dies half way still exits 0 with its remaining cases marked pending.
        var results = SysTestResults.TryParseFile(resultsPath);

        var warnings = new List<string>();
        if (!resultsWritten && !string.IsNullOrWhiteSpace(resultsPath))
            warnings.Add($"The runner exited cleanly but wrote no result document at {resultsPath}.");
        else if (resultsWritten && results is null)
            warnings.Add($"{resultsPath} is not a SysTest result document (expected a <test-results> root).");

        var payload = ToolResult<object>.Success(new
        {
            exitCode = exit,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            testClasses = classes,
            resultsPath = resultsWritten ? resultsPath : null,
            results = results is null ? null : new
            {
                clean = results.Clean,
                passed = results.Passed,
                failed = results.Failed,
                skipped = results.Skipped,
                pending = results.Pending,
                failures = results.Failures.Select(f => new { name = f.Name, message = f.FailureMessage }).ToList(),
            },
            tail = stdout.Split('\n').TakeLast(40).ToArray(),
        }, warnings.Count > 0 ? warnings : null);

        return new TestRunOutcome(payload, results?.Clean);
    }

    // -------------------------------------------------------------- BP check

    // xppbp help-text fragments that indicate the tool printed usage instead of results.
    private static readonly Regex HelpTextPattern = new(
        @"^usage:|BPCheck Tool|^xppbp\.exe|unrecognized|missing required|X\+\+ Best Practice Options",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public static ToolResult<object> BpCheck(
        string? model, string? bpToolPath, string? packagesPath, string? metadataPath)
    {
        if (string.IsNullOrEmpty(model))
            return ToolResult<object>.Fail("MISSING_ARGUMENT", "A model name is required for bp check.",
                "Example: d365fo bp check --model MyCustomModel");

        // Resolve paths. In UDE environments:
        //   packagesRoot = FrameworkDirectory (where xppbp.exe lives, under Bin/)
        //   metadataPath = ModelStoreFolder   (where custom source XML lives)
        // In traditional environments both roles are served by packagesRoot.
        var packagesRoot = packagesPath
            ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH")
            ?? DefaultPackagesRoot();
        var metadata = metadataPath ?? packagesRoot;

        var bp = bpToolPath ?? Path.Combine(packagesRoot, "Bin", "xppbp.exe");

        if (!File.Exists(bp))
            return ToolResult<object>.Fail("XPPBP_NOT_FOUND", $"xppbp.exe not found at: {bp}",
                "Set D365FO_PACKAGES_PATH (or --packages) to the FrameworkDirectory that contains Bin\\xppbp.exe.");

        // Modern -metadata: flag, falling back to the legacy -packagesroot: when unrecognised.
        List<string> BuildArgs(string metadataFlag) =>
        [
            $"{metadataFlag}{metadata}",
            $"-module:{model}",
            $"-model:{model}",
            "-all",
        ];

        var (exit, stdout, stderr, elapsed) = Run(bp, BuildArgs("-metadata:"));
        var combined = string.Join("\n", stdout, stderr).Trim();

        if (HelpTextPattern.IsMatch(combined) || string.IsNullOrWhiteSpace(combined))
            (exit, stdout, stderr, elapsed) = Run(bp, BuildArgs("-packagesroot:"));

        var tail = stdout.Split('\n').TakeLast(40).ToArray();
        return exit == 0
            ? ToolResult<object>.Success(new
            {
                exitCode = exit,
                elapsedMs = (long)elapsed.TotalMilliseconds,
                packagesRoot,
                metadataPath = metadata,
                model,
                tail,
            })
            : ToolResult<object>.Fail("BP_FAILED", $"Best practice check exited with {exit}.",
                string.Join('\n', stderr.Split('\n').TakeLast(5)));
    }

    // Well-known D365FO PackagesLocalDirectory locations, used only as a last-resort fallback
    // when neither an explicit path nor D365FO_PACKAGES_PATH is set. K:\ is the cloud-hosted
    // layout; C:\ is the standard local VHD layout.
    private static readonly string[] DefaultPackageRoots =
    [
        @"K:\AosService\PackagesLocalDirectory",
        @"C:\AosService\PackagesLocalDirectory",
    ];

    private static string DefaultPackagesRoot()
    {
        foreach (var root in DefaultPackageRoots)
            if (Directory.Exists(root)) return root;
        return DefaultPackageRoots[0];
    }

    // --------------------------------------------------------------- process

    private static readonly Regex DiagRx = new(
        @"(?<file>[^:()]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<kind>error|warning)\s+(?<code>\S+):\s+(?<msg>.+)",
        RegexOptions.Compiled);

    // MSBuild's other diagnostic shape, with no file position: "MSBUILD : error MSB1009: …",
    // "MSBUILD : error MSB4062: …". These are exactly the toolchain failures — a missing
    // project, an unloadable build task — and matching only the positional form above dropped
    // them, so a build that never reached the compiler reported zero errors.
    private static readonly Regex GlobalDiagRx = new(
        @"^\s*(?<src>[^\r\n:]*?)\s*:\s*(?<kind>error|warning)\s+(?<code>MSB\d+):\s+(?<msg>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    internal static List<object> ParseMsBuildDiagnostics(string output, string kind)
    {
        var list = new List<object>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match m in DiagRx.Matches(output))
        {
            if (!string.Equals(m.Groups["kind"].Value, kind, StringComparison.OrdinalIgnoreCase)) continue;
            var code = m.Groups["code"].Value;
            var message = m.Groups["msg"].Value.Trim();
            seen.Add(code + "|" + message);
            list.Add(new
            {
                file = m.Groups["file"].Value.Trim(),
                line = int.Parse(m.Groups["line"].Value),
                column = int.Parse(m.Groups["col"].Value),
                code,
                message,
                hint = HintFor(m.Value.Trim()),
            });
        }

        foreach (Match m in GlobalDiagRx.Matches(output))
        {
            if (!string.Equals(m.Groups["kind"].Value, kind, StringComparison.OrdinalIgnoreCase)) continue;
            var code = m.Groups["code"].Value;
            var message = m.Groups["msg"].Value.Trim();
            // The same MSB error is often echoed both with and without a file position.
            if (!seen.Add(code + "|" + message)) continue;
            list.Add(new
            {
                file = m.Groups["src"].Value.Trim(),
                line = (int?)null,
                column = (int?)null,
                code,
                message,
                hint = HintFor(m.Value.Trim()),
            });
        }

        return list;
    }

    /// <summary>
    /// The scored fix-hint rules, applied to MSBuild's own messages. An MSB4062 is an
    /// environment failure that reads like a compiler error; without this the caller saw the
    /// raw text and had to guess what it meant.
    /// </summary>
    private static object? HintFor(string line)
    {
        var hint = XppcFixHints.Best(line);
        return hint is null ? null : new { rule = hint.RuleId, hint = hint.Hint, knowledge = hint.Knowledge };
    }

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n').TakeLast(lines));

    public static (int Exit, string StdOut, string StdErr, TimeSpan Elapsed) Run(
        string fileName, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var sw = Stopwatch.StartNew();
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to launch {fileName}");
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        p.WaitForExit();
        sw.Stop();
        return (p.ExitCode, so, se, sw.Elapsed);
    }
}
