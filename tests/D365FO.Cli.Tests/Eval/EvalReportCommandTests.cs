using D365FO.Cli.Commands.Eval;
using Xunit;

namespace D365FO.Cli.Tests.Eval;

/// <summary>
/// Smoke tests only — these commands read the repo's real
/// <c>eval/corpus/runs/</c> directory (shared, gitignored, and mutated by
/// whatever else has run `eval run --write`/`eval score --write` locally),
/// so asserting exact counts here would be flaky by construction. The
/// aggregation logic itself is covered against synthetic records in
/// D365FO.Core.Tests/Eval/EvalCorpusStoreTests.cs.
/// </summary>
// Shares a collection with EvalRunCommandTests/EvalListCommandTests: all redirect the
// process-wide Console.Out, so running them in parallel would race on that global state.
[Collection("EnvIndexDb")]
public class EvalReportCommandTests
{
    private static int RunReport(out string stdout)
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = new EvalReportCommand().Execute(null!, new EvalReportCommand.Settings { Output = "json" });
            stdout = writer.ToString();
            return exit;
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static int RunClusters(out string stdout)
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = new EvalClustersCommand().Execute(null!, new EvalClustersCommand.Settings { Output = "json" });
            stdout = writer.ToString();
            return exit;
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    [Fact]
    public void Report_returns_a_well_formed_envelope()
    {
        var exit = RunReport(out var stdout);

        Assert.Equal(0, exit);
        Assert.Contains("\"ok\":true", stdout);
        Assert.Contains("\"totalRuns\"", stdout);
        Assert.Contains("\"goldenPassRate\"", stdout);
        Assert.Contains("\"byTier\"", stdout);
        Assert.Contains("\"classificationCounts\"", stdout);
    }

    [Fact]
    public void Clusters_returns_a_well_formed_envelope()
    {
        var exit = RunClusters(out var stdout);

        Assert.Equal(0, exit);
        Assert.Contains("\"ok\":true", stdout);
        Assert.Contains("\"clusters\"", stdout);
        Assert.Contains("\"count\"", stdout);
    }
}
