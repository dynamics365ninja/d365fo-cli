using D365FO.Cli.Commands.Eval;
using Xunit;

namespace D365FO.Cli.Tests.Eval;

/// <summary>
/// The real regression gate for the eval loop: replays every authored case's
/// <c>canonical_args</c> for real and asserts it still matches its captured
/// golden. This is what proves the harness and the committed goldens are
/// actually correct, not just plausible.
/// </summary>
// EvalRunCommand replays via a genuine child process (see its RunReplay doc
// comment — an earlier in-process CliApp design corrupted Spectre's
// AnsiConsole singleton for the rest of the test process), so it no longer
// mutates shared process state itself. Still shares the collection with
// LabelBatchCreateTests/IndexExtractOutputModeTests/etc. as a low-cost
// precaution against any other cross-test global-state interaction.
[Collection("EnvIndexDb")]
public class EvalRunCommandTests
{
    private static int Run(string caseId, out string stdout)
    {
        var settings = new EvalRunCommand.Settings { CaseId = caseId, Output = "json" };
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = new EvalRunCommand().Execute(null!, settings);
            stdout = writer.ToString();
            return exit;
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    [Theory]
    [InlineData("L0-edt-basic")]
    [InlineData("L0-enum-basic")]
    [InlineData("L1-table-basic")]
    [InlineData("L1-class-basic")]
    [InlineData("L2-coc-extension")]
    [InlineData("L2-table-extension")]
    public void Every_authored_case_replays_clean_against_its_golden(string caseId)
    {
        var exit = Run(caseId, out var stdout);

        Assert.Equal(0, exit);
        Assert.Contains("\"ok\":true", stdout);
        Assert.Contains("\"goldenMatch\":true", stdout);
        Assert.DoesNotContain("\"xppClean\":false", stdout);
        Assert.DoesNotContain("\"referencesClean\":false", stdout);
    }

    [Fact]
    public void Unknown_case_id_fails_with_a_specific_error_code()
    {
        var exit = Run("L9-does-not-exist", out var stdout);

        Assert.NotEqual(0, exit);
        Assert.Contains("EVAL_CASE_NOT_FOUND", stdout);
    }
}
