using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Ops;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// <c>d365fo oracle runtime</c> — is the SysTest runner wired to anything, and can it tell a
/// passing test from a failing one?
/// </summary>
/// <remarks>
/// <para>
/// Two questions that decide whether a test result means anything, and neither is answered by
/// running the tests. A runner whose config carries no database dies with <c>Login failed</c>,
/// which reads like a broken test model; the keys it needs are in the AOS <c>web.config</c>, so
/// the answer is derivable rather than guessable.
/// </para>
/// <para>
/// The second question is the one nobody asks: "all tests passed" is also what a runner prints
/// when it ran nothing. <c>--negative-control</c> emits a class whose three methods pass, fail
/// and throw on purpose — until the failure is reported AS a failure, a green suite is not
/// evidence.
/// </para>
/// </remarks>
public sealed class OracleRuntimeCommand : Command<OracleRuntimeCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("PackagesLocalDirectory; the runner lives under its bin. Defaults to D365FO_PACKAGES_PATH.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--web-config <PATH>")]
        [System.ComponentModel.Description("AOS web.config to take the database settings from. Found beside the packages root when omitted.")]
        public string? WebConfig { get; init; }

        [CommandOption("--configure")]
        [System.ComponentModel.Description("Write the MISSING database settings into SysTestConsole.exe.config, taken from the AOS web.config. Keeps a .bak beside it — this edits a Microsoft-installed file.")]
        public bool Configure { get; init; }

        [CommandOption("--negative-control")]
        [System.ComponentModel.Description("Emit the negative-control test class (a pass, a deliberate fail, a deliberate throw) instead of diagnosing.")]
        public bool NegativeControl { get; init; }

        [CommandOption("--out <PATH>")]
        [System.ComponentModel.Description("Write the negative control here instead of to stdout.")]
        public string? Out { get; init; }

        [CommandOption("--class <NAME>")]
        [System.ComponentModel.Description("Class name for the negative control (default D365FoCliNegativeControlTest).")]
        public string? ClassName { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (settings.NegativeControl)
        {
            var className = string.IsNullOrWhiteSpace(settings.ClassName)
                ? "D365FoCliNegativeControlTest"
                : settings.ClassName!;
            var source = RuntimeOracle.NegativeControlSource(className);

            string? written = null;
            if (!string.IsNullOrWhiteSpace(settings.Out))
            {
                var full = Path.GetFullPath(settings.Out!);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(full, source);
                written = full;
            }

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                className,
                path = written,
                source = written is null ? source : null,
                howToUse = $"Compile it into a test model, then `d365fo test run --test {className} --results <PATH>`. "
                         + "A run that reports all three as passed is not running them; one that reports the failure "
                         + "and the throw distinctly is one whose verdicts mean something.",
            }));
        }

        var packagesRoot = settings.PackagesPath ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH");
        if (string.IsNullOrWhiteSpace(packagesRoot))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.PackagesPathNotFound,
                "No packages path.",
                "Set D365FO_PACKAGES_PATH or pass --packages <PATH>."));

        var diagnosis = RuntimeOracle.Diagnose(packagesRoot!, settings.WebConfig);

        if (settings.Configure)
        {
            var guard = SdlcRunner.WindowsGuard("d365fo oracle runtime --configure");
            if (guard is not null) return RenderHelpers.Render(kind, guard);

            if (diagnosis.RunnerConfigPath is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("RUNNER_NOT_FOUND",
                    "SysTestConsole.exe.config is not where the runner should be.",
                    $"Looked under {Path.Combine(packagesRoot!, "bin")}. The runner ships with the platform; a "
                    + "development VM has it and a build agent may not."));

            if (diagnosis.WebConfigPath is null)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("WEB_CONFIG_NOT_FOUND",
                    "No AOS web.config to take the settings from.",
                    "Pass --web-config <PATH>. The settings are copied from the AOS this installation runs, never invented."));

            var result = RuntimeOracle.Configure(diagnosis.RunnerConfigPath, diagnosis.WebConfigPath);
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                configured = result.RunnerConfigPath,
                backup = result.Written.Count > 0 ? result.BackupPath : null,
                written = result.Written,
                unavailable = result.Unavailable,
                verdict = result.Written.Count == 0
                    ? "Nothing to do — the runner already carries every database setting."
                    : $"Copied {result.Written.Count} setting(s) from the AOS configuration.",
            }, result.Unavailable.Count > 0
                ? [$"{result.Unavailable.Count} setting(s) are missing from the web.config too and were not invented: "
                   + string.Join(", ", result.Unavailable)]
                : null));
        }

        var warnings = new List<string>();
        if (!diagnosis.RunnerPresent)
            warnings.Add("SysTestConsole.exe is not under the packages bin — there is no runtime oracle on this host.");
        else if (!diagnosis.Configured)
            warnings.Add($"The runner carries no value for: {string.Join(", ", diagnosis.Missing)}. It will fail with "
                       + "\"Login failed\", which reads like a broken test model rather than a runner pointed at nothing. "
                       + "Run with --configure to copy them from the AOS web.config.");

        var rc = RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            runner = diagnosis.RunnerPath,
            runnerConfig = diagnosis.RunnerConfigPath,
            webConfig = diagnosis.WebConfigPath,
            configured = diagnosis.Configured,
            settings = diagnosis.Settings,
            missing = diagnosis.Missing,
            note = "Configuration is necessary and not sufficient: a configured runner still has to be shown to "
                 + "FAIL a test that should fail. `--negative-control` emits the class that proves it.",
        }, warnings.Count > 0 ? warnings : null));

        return rc != 0 ? rc : diagnosis.Configured ? 0 : 2;
    }
}
