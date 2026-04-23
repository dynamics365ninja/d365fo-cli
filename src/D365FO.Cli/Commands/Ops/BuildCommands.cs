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

        var payload = new
        {
            exitCode = exit,
            elapsedMs = (long)elapsed.TotalMilliseconds,
            errorCount = errors.Count,
            warningCount = warnings.Count,
            errors,
            warnings,
            tail = Tail(stdout, 20),
        };

        return RenderHelpers.Render(kind, exit == 0
            ? ToolResult<object>.Success(payload)
            : ToolResult<object>.Fail("BUILD_FAILED", $"MSBuild exited with {exit}.", Tail(stderr, 5)));
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

public sealed class TestRunCommand : Command<TestRunCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--runner <PATH>")]
        public string? RunnerPath { get; init; }

        [CommandOption("--suite <NAME>")]
        public string? Suite { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var guard = WindowsGuard.Check("d365fo test run");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var runner = settings.RunnerPath ?? "SysTestRunner.exe";
        var args = new List<string>();
        if (!string.IsNullOrEmpty(settings.Suite)) args.Add($"--suite {settings.Suite}");
        var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(runner, args);
        return RenderHelpers.Render(kind, exit == 0
            ? ToolResult<object>.Success(new { exitCode = exit, elapsedMs = (long)elapsed.TotalMilliseconds, tail = stdout.Split('\n').TakeLast(40).ToArray() })
            : ToolResult<object>.Fail("TESTS_FAILED", $"Runner exited with {exit}.", string.Join('\n', stderr.Split('\n').TakeLast(5))));
    }
}

public sealed class BpCheckCommand : Command<BpCheckCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--tool <PATH>")]
        public string? BpToolPath { get; init; }

        [CommandOption("--model <NAME>")]
        public string? Model { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var guard = WindowsGuard.Check("d365fo bp check");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var bp = settings.BpToolPath ?? "xppbp.exe";
        var args = new List<string>();
        if (!string.IsNullOrEmpty(settings.Model)) args.Add($"-model={settings.Model}");
        var (exit, stdout, stderr, elapsed) = ProcessRunner.Run(bp, args);
        return RenderHelpers.Render(kind, exit == 0
            ? ToolResult<object>.Success(new { exitCode = exit, elapsedMs = (long)elapsed.TotalMilliseconds, tail = stdout.Split('\n').TakeLast(40).ToArray() })
            : ToolResult<object>.Fail("BP_FAILED", $"Best practice check exited with {exit}.", string.Join('\n', stderr.Split('\n').TakeLast(5))));
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
