using System.Text.Json;
using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// SysTestConsole exits non-zero when a test fails. The result document it wrote says which, so
/// that run is a completed one with failures, not a runner error with a few lines of stderr.
/// </summary>
public sealed class SysTestRunOutcomeTests : IDisposable
{
    private const string FailedRun = """
        <test-results>
          <test-suite>
            <results>
              <test-case name="ConFleetTest.testPasses" executed="True" success="True" />
              <test-case name="ConFleetTest.testFails" executed="True" success="False">
                <failure><message>Expected 2, got 3</message></failure>
              </test-case>
            </results>
          </test-suite>
        </test-results>
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"d365fo-systest-{Guid.NewGuid():N}");
    private string Results => Path.Combine(_dir, "results.xml");

    public SysTestRunOutcomeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp */ }
    }

    /// <summary>A runner that optionally writes the result document, then exits with a code.</summary>
    private string Runner(int exitCode, bool writesResults)
    {
        var source = Path.Combine(_dir, "prepared.xml");
        File.WriteAllText(source, FailedRun);
        if (OperatingSystem.IsWindows())
        {
            var cmd = Path.Combine(_dir, "runner.cmd");
            File.WriteAllText(cmd, "@echo off\r\n"
                + (writesResults ? $"copy /y \"{source}\" \"{Results}\" >nul\r\n" : "")
                + $"exit /b {exitCode}\r\n");
            return cmd;
        }
        var sh = Path.Combine(_dir, "runner.sh");
        File.WriteAllText(sh, "#!/bin/sh\n"
            + (writesResults ? $"cp \"{source}\" \"{Results}\"\n" : "")
            + $"exit {exitCode}\n");
        File.SetUnixFileMode(sh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return sh;
    }

    private SdlcRunner.TestRunOutcome Run(int exitCode, bool writesResults)
        => SdlcRunner.RunTests(Runner(exitCode, writesResults), ["ConFleetTest"], null, Results, parallel: false);

    private void LeaveAnOldResultBehind()
    {
        File.WriteAllText(Results, FailedRun);
        File.SetLastWriteTimeUtc(Results, DateTime.UtcNow.AddHours(-1));
    }

    [Fact]
    public void A_failing_run_reports_the_failed_tests()
    {
        var outcome = Run(exitCode: 1, writesResults: true);

        Assert.True(outcome.Result.Ok, outcome.Result.Error?.Message);
        Assert.False(outcome.Clean);
        var json = JsonSerializer.Serialize(outcome.Result);
        Assert.Contains("ConFleetTest.testFails", json);
        Assert.Contains("Expected 2, got 3", json);
    }

    [Fact]
    public void A_runner_that_wrote_nothing_is_an_error_even_beside_an_old_result()
    {
        LeaveAnOldResultBehind();

        var outcome = Run(exitCode: 1, writesResults: false);

        Assert.False(outcome.Result.Ok);
        Assert.Equal("TESTS_FAILED", outcome.Result.Error!.Code);
    }

    [Fact]
    public void An_old_result_is_not_reported_as_this_runs()
    {
        LeaveAnOldResultBehind();

        var outcome = Run(exitCode: 0, writesResults: false);

        Assert.True(outcome.Result.Ok);
        Assert.Null(outcome.Clean);
        Assert.DoesNotContain("testFails", JsonSerializer.Serialize(outcome.Result));
    }
}
