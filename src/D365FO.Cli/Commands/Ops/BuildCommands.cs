using System.Diagnostics;
using System.Text.RegularExpressions;
using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

/// <summary>
/// Thin wrappers around the Windows-only D365FO developer tools. All of them
/// refuse to run on non-Windows hosts and emit a structured UNSUPPORTED_PLATFORM
/// error so that agents can branch cleanly without inspecting stderr text.
/// </summary>
internal static class WindowsGuard
{
    public static ToolResult<object>? Check(string toolName)
    {
        if (OperatingSystem.IsWindows()) return null;
        return ToolResult<object>.Fail(
            "UNSUPPORTED_PLATFORM",
            $"{toolName} requires Windows with a D365FO developer VM.",
            "Run this command on the D365FO VM. The CLI is cross-platform for metadata and scaffolding, but build/sync/test/bp invoke Windows-only executables.");
    }
}

public sealed class BuildCommand : Command<BuildCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--msbuild <PATH>")]
        public string? MsBuildPath { get; init; }

        [CommandOption("--project <PATH>")]
        public string? ProjectPath { get; init; }

        [CommandOption("--config <NAME>")]
        public string Configuration { get; init; } = "Debug";

        [CommandOption("--xppc-log <PATH>")]
        [System.ComponentModel.Description("Additional xppc.exe log file to parse for structured X++ compiler diagnostics (Dynamics.AX.<Model>.xppc.log).")]
        public string? XppcLogPath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var guard = WindowsGuard.Check("d365fo build");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var msbuild = settings.MsBuildPath ?? "msbuild.exe";
        var args = new List<string>();
        if (!string.IsNullOrEmpty(settings.ProjectPath)) args.Add(settings.ProjectPath!);
        args.Add($"/p:Configuration={settings.Configuration}");
        args.Add("/nologo");

        var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(msbuild, args);
        var errors = ParseMsBuildDiagnostics(stdout, "error");
        var warnings = ParseMsBuildDiagnostics(stdout, "warning");

        // Structured xppc diagnostics: the X++ compiler reports through its own
        // "Compile Error: … dynamics://Model/Object/member: [(l,c)]: msg" format,
        // both inside MSBuild stdout and in the -log file. Parsing them gives the
        // agent {object, member, line, column, message, hint} instead of raw text.
        var xppcSource = stdout;
        if (!string.IsNullOrEmpty(settings.XppcLogPath) && File.Exists(settings.XppcLogPath))
        {
            try { xppcSource += "\n" + File.ReadAllText(settings.XppcLogPath); }
            catch { /* unreadable log — stdout still parsed */ }
        }
        var xppc = D365FO.Core.Validation.XppcDiagnostics.Parse(xppcSource);
        var staleSymbols = D365FO.Core.Validation.XppcDiagnostics.IndicatesStaleSymbols(xppcSource);

        var payload = new
        {
            buildSucceeded = exit == 0,
            exitCode = exit,
            elapsedMs = (long)elapsed.TotalMilliseconds,
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

        // Failure keeps the full structured payload (the agent needs the
        // diagnostics exactly when the build fails); the exit code still
        // signals the failure for CI.
        var rc = RenderHelpers.Render(kind, ToolResult<object>.Success(payload,
            warnings: exit == 0 ? null : new[] { "build-failed" }));
        return exit == 0 ? rc : 1;
    }

    private static readonly Regex DiagRx = new(@"(?<file>[^:()]+)\((?<line>\d+),(?<col>\d+)\):\s+(?<kind>error|warning)\s+(?<code>\S+):\s+(?<msg>.+)", RegexOptions.Compiled);

    private static List<object> ParseMsBuildDiagnostics(string output, string kind)
    {
        var list = new List<object>();
        foreach (Match m in DiagRx.Matches(output))
        {
            if (!string.Equals(m.Groups["kind"].Value, kind, StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(new
            {
                file = m.Groups["file"].Value.Trim(),
                line = int.Parse(m.Groups["line"].Value),
                column = int.Parse(m.Groups["col"].Value),
                code = m.Groups["code"].Value,
                message = m.Groups["msg"].Value.Trim(),
            });
        }
        return list;
    }

    private static string Tail(string text, int lines)
    {
        var split = text.Split('\n');
        return string.Join('\n', split.TakeLast(lines));
    }
}

public sealed class SyncCommand : Command<SyncCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--tool <PATH>")]
        public string? SyncToolPath { get; init; }

        [CommandOption("--full")]
        public bool Full { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var guard = WindowsGuard.Check("d365fo sync");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var sync = settings.SyncToolPath ?? "SyncEngine.exe";
        var args = new List<string> { "-syncmode=" + (settings.Full ? "fullall" : "partiallist") };
        var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(sync, args);
        return RenderHelpers.Render(kind, exit == 0
            ? ToolResult<object>.Success(new { exitCode = exit, elapsedMs = (long)elapsed.TotalMilliseconds, tail = stdout.Split('\n').TakeLast(20).ToArray() })
            : ToolResult<object>.Fail("SYNC_FAILED", $"SyncEngine exited with {exit}.", string.Join('\n', stderr.Split('\n').TakeLast(5))));
    }
}

/// <summary>
/// Runs SysTest tests through the platform's own console runner.
/// </summary>
/// <remarks>
/// <para>
/// Issue #160. This command used to default to <c>SysTestRunner.exe</c> and pass
/// <c>--suite &lt;name&gt;</c>. Neither exists. The binary shipped in
/// <c>PackagesLocalDirectory\bin</c> is <b>SysTestConsole.exe</b>, and its options are
/// slash-prefixed and colon-joined — <c>/test:Class1,Class2</c>, <c>/xml:&lt;file&gt;</c>,
/// <c>/granularity:</c>, <c>/parallel</c>. Ground-truthed by running the real binary's usage
/// text on a D365FO host. The old defaults could never have worked, which is why nothing
/// noticed: the command was never run against a real installation.
/// </para>
/// <para>
/// <c>--results</c> asks the runner for its XML result document, which is the input an L4
/// oracle needs. Without it the only output is a tail of console text, which is a log, not a
/// result.
/// </para>
/// </remarks>
public sealed class TestRunCommand : Command<TestRunCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--runner <PATH>")]
        [System.ComponentModel.Description("Path to SysTestConsole.exe. Defaults to the one on PATH / in the packages bin folder.")]
        public string? RunnerPath { get; init; }

        [CommandOption("--test <CLASS>")]
        [System.ComponentModel.Description("Repeatable: test class to run. Maps to the runner's /test: option.")]
        public string[] TestClasses { get; init; } = Array.Empty<string>();

        [CommandOption("--suite <NAME>")]
        [System.ComponentModel.Description("Deprecated alias for --test; the runner has no notion of a suite.")]
        public string? Suite { get; init; }

        [CommandOption("--granularity <LEVEL>")]
        [System.ComponentModel.Description("Default | UnitTest | ScenarioTest.")]
        public string? Granularity { get; init; }

        [CommandOption("--results <PATH>")]
        [System.ComponentModel.Description("Write the runner's XML result document here (/xml:). Required for any structured verdict.")]
        public string? ResultsPath { get; init; }

        [CommandOption("--parallel")]
        [System.ComponentModel.Description("Run the tests through the batch framework in parallel.")]
        public bool Parallel { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var guard = WindowsGuard.Check("d365fo test run");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var runner = settings.RunnerPath ?? "SysTestConsole.exe";

        var classes = settings.TestClasses
            .Concat(string.IsNullOrWhiteSpace(settings.Suite) ? [] : new[] { settings.Suite! })
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .ToList();

        var args = new List<string>();
        if (classes.Count > 0) args.Add("/test:" + string.Join(',', classes));
        if (!string.IsNullOrWhiteSpace(settings.Granularity)) args.Add("/granularity:" + settings.Granularity);
        if (!string.IsNullOrWhiteSpace(settings.ResultsPath)) args.Add("/xml:" + settings.ResultsPath);
        if (settings.Parallel) args.Add("/parallel");

        var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(runner, args);

        var resultsWritten = !string.IsNullOrWhiteSpace(settings.ResultsPath)
                             && System.IO.File.Exists(settings.ResultsPath!);

        if (exit != 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "TESTS_FAILED", $"Runner exited with {exit}.", string.Join('\n', stderr.Split('\n').TakeLast(5))));

        // The verdict comes from the runner's own result document, not from its exit code: a
        // run that dies half way still exits 0 with its remaining cases marked pending.
        var results = D365FO.Core.Eval.SysTestResults.TryParseFile(settings.ResultsPath);

        var warnings = new List<string>();
        if (!resultsWritten && !string.IsNullOrWhiteSpace(settings.ResultsPath))
            warnings.Add($"The runner exited cleanly but wrote no result document at {settings.ResultsPath}.");
        else if (resultsWritten && results is null)
            warnings.Add($"{settings.ResultsPath} is not a SysTest result document (expected a <test-results> root).");

        var payload = ToolResult<object>.Success(new
        {
            exitCode = exit,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            testClasses = classes,
            resultsPath = resultsWritten ? settings.ResultsPath : null,
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

        var rc = RenderHelpers.Render(kind, payload);
        // A parsed run that is not clean is a failed run, whatever the exit code said.
        return rc != 0 ? rc : results is { Clean: false } ? 2 : 0;
    }
}

public sealed class BpCheckCommand : Command<BpCheckCommand.Settings>
{
    // xppbp help-text fragments that indicate the tool printed usage instead of results.
    private static readonly Regex HelpTextPattern = new(
        @"^usage:|BPCheck Tool|^xppbp\.exe|unrecognized|missing required|X\+\+ Best Practice Options",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--tool <PATH>")]
        public string? BpToolPath { get; init; }

        [CommandOption("--model <NAME>")]
        public string? Model { get; init; }

        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("PackagesLocalDirectory (or FrameworkDirectory on UDE). Defaults to D365FO_PACKAGES_PATH.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--metadata <PATH>")]
        [System.ComponentModel.Description("Custom model metadata root (ModelStoreFolder on UDE). Defaults to --packages when not set.")]
        public string? MetadataPath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var guard = WindowsGuard.Check("d365fo bp check");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var modelName = settings.Model;
        if (string.IsNullOrEmpty(modelName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "MISSING_ARGUMENT", "--model <NAME> is required for bp check.",
                "Example: d365fo bp check --model MyCustomModel"));

        // Resolve paths. In UDE environments:
        //   packagesRoot = FrameworkDirectory (where xppbp.exe lives, under Bin/)
        //   metadataPath = ModelStoreFolder   (where custom source XML lives)
        // In traditional environments both roles are served by packagesRoot.
        var packagesRoot = settings.PackagesPath
            ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH")
            ?? DefaultPackagesRoot();
        var metadataPath = settings.MetadataPath ?? packagesRoot;

        var bp = settings.BpToolPath
            ?? System.IO.Path.Combine(packagesRoot, "Bin", "xppbp.exe");

        if (!System.IO.File.Exists(bp))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                "XPPBP_NOT_FOUND",
                $"xppbp.exe not found at: {bp}",
                "Set D365FO_PACKAGES_PATH (or --packages) to the FrameworkDirectory that contains Bin\\xppbp.exe."));

        // Build argument list using modern -metadata: flag.
        // Falls back to legacy -packagesroot: when the modern flag is not recognised.
        List<string> BuildArgs(string metadataFlag) => new()
        {
            $"{metadataFlag}{metadataPath}",
            $"-module:{modelName}",
            $"-model:{modelName}",
            "-all",
        };

        var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(bp, BuildArgs("-metadata:"));
        var combined = string.Join("\n", stdout, stderr).Trim();

        // If the modern flag is not supported, fall back to the legacy -packagesroot: flag.
        if (HelpTextPattern.IsMatch(combined) || string.IsNullOrWhiteSpace(combined))
        {
            (exit, stdout, stderr, elapsed) = ProcessRunner.Run(bp, BuildArgs("-packagesroot:"));
        }

        var tail = stdout.Split('\n').TakeLast(40).ToArray();
        return RenderHelpers.Render(kind, exit == 0
            ? ToolResult<object>.Success(new
            {
                exitCode = exit,
                elapsedMs = (long)elapsed.TotalMilliseconds,
                packagesRoot,
                metadataPath,
                model = modelName,
                tail,
            })
            : ToolResult<object>.Fail("BP_FAILED",
                $"Best practice check exited with {exit}.",
                string.Join('\n', stderr.Split('\n').TakeLast(5))));
    }

    // Well-known D365FO PackagesLocalDirectory locations, used only as a
    // last-resort fallback when neither --packages nor D365FO_PACKAGES_PATH
    // (env or settings.json) is set. K:\ is the cloud-hosted layout; C:\ is the
    // standard local VHD layout.
    private static readonly string[] DefaultPackageRoots =
    {
        @"K:\AosService\PackagesLocalDirectory",
        @"C:\AosService\PackagesLocalDirectory",
    };

    private static string DefaultPackagesRoot()
    {
        foreach (var root in DefaultPackageRoots)
        {
            if (Directory.Exists(root)) return root;
        }
        return DefaultPackageRoots[0];
    }
}

internal static class ProcessRunner
{
    public static (int Exit, string StdOut, string StdErr, TimeSpan Elapsed) Run(string fileName, IEnumerable<string> args)
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
