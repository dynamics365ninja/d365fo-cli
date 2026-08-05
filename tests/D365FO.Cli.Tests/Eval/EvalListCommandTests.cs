using D365FO.Cli.Commands.Eval;
using Xunit;

namespace D365FO.Cli.Tests.Eval;

// Shares a collection with EvalRunCommandTests/LabelBatchCreateTests/ScaffoldingSnapshotTests:
// all of these redirect the process-wide Console.Out and/or D365FO_INDEX_DB, so running them
// in parallel across collections would race on that global state.
[Collection("EnvIndexDb")]
public class EvalListCommandTests
{
    private static int Run(out string stdout)
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = new EvalListCommand().Execute(null!, new EvalListCommand.Settings { Output = "json" });
            stdout = writer.ToString();
            return exit;
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    [Fact]
    public void Lists_every_authored_case()
    {
        var exit = Run(out var stdout);

        Assert.Equal(0, exit);
        Assert.Contains("\"ok\":true", stdout);
        Assert.Contains("\"count\":51", stdout);
        Assert.Contains("L0-edt-basic", stdout);
        Assert.Contains("L2-coc-extension", stdout);
        Assert.Contains("L1-form-workspace", stdout);
    }
}
