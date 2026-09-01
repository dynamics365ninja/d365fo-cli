using D365FO.Core;
using D365FO.Core.Ops;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

// Thin wrappers around the Windows-only D365FO developer tools. All of them refuse to run on
// non-Windows hosts and emit a structured UNSUPPORTED_PLATFORM error so that agents can branch
// cleanly without inspecting stderr text.
//
// The invocations themselves are D365FO.Core.Ops.SdlcRunner, shared with the MCP `sdlc` tool:
// while they lived here, "does it compile?" was a question only a caller with a shell could ask,
// which is the wrong half of the audience to leave it with.

/// <summary><c>d365fo build</c> — MSBuild with structured X++ compiler diagnostics.</summary>
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
        var guard = SdlcRunner.WindowsGuard("d365fo build");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var result = SdlcRunner.Build(
            settings.MsBuildPath, settings.ProjectPath, settings.Configuration, settings.XppcLogPath);

        // The build's own verdict rides in a warning so the diagnostics survive; the exit code
        // is what CI reads.
        var failed = result.Warnings?.Contains("build-failed") == true;
        var rc = RenderHelpers.Render(kind, result);
        return failed ? 1 : rc;
    }
}

/// <summary><c>d365fo sync</c> — database synchronisation through SyncEngine.</summary>
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
        var guard = SdlcRunner.WindowsGuard("d365fo sync");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        return RenderHelpers.Render(kind, SdlcRunner.Sync(settings.SyncToolPath, settings.Full));
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
        var guard = SdlcRunner.WindowsGuard("d365fo test run");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        var classes = settings.TestClasses
            .Concat(string.IsNullOrWhiteSpace(settings.Suite) ? [] : new[] { settings.Suite! })
            .ToList();

        var outcome = SdlcRunner.RunTests(
            settings.RunnerPath, classes, settings.Granularity, settings.ResultsPath, settings.Parallel);

        var rc = RenderHelpers.Render(kind, outcome.Result);
        // A parsed run that is not clean is a failed run, whatever the exit code said.
        return rc != 0 ? rc : outcome.Clean == false ? 2 : 0;
    }
}

/// <summary><c>d365fo bp check</c> — Microsoft Best Practices via xppbp.exe.</summary>
public sealed class BpCheckCommand : Command<BpCheckCommand.Settings>
{
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
        var guard = SdlcRunner.WindowsGuard("d365fo bp check");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        return RenderHelpers.Render(kind, SdlcRunner.BpCheck(
            settings.Model, settings.BpToolPath, settings.PackagesPath, settings.MetadataPath));
    }
}
