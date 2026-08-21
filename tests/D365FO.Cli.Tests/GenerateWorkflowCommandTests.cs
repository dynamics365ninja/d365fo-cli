using System.Text.Json;
using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Guards the two references <c>generate workflow</c> used to leave dangling.
/// </summary>
/// <remarks>
/// The L3 build oracle caught both: a template with no <c>Category</c> is rejected
/// by the metadata provider outright, and the <c>WorkflowDocument</c>'s
/// <c>getQueryName()</c> named a query the command never created. The command used
/// to warn about each and ship anyway — a warning, for metadata that could not build.
/// </remarks>
// Captures Console.Out, which is process-global: it has to be serialised against the
// other console-capturing classes rather than left to xUnit's per-class parallelism.
// Without this it reads another class's output and fails on empty JSON.
[Collection("EnvIndexDb")]
public class GenerateWorkflowCommandTests
{
    private static (int Exit, JsonDocument Json) Run(GenerateWorkflowCommand.Settings settings)
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = new GenerateWorkflowCommand().Execute(null!, settings);
            return (exit, JsonDocument.Parse(writer.ToString()));
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    [Fact]
    public void A_workflow_without_a_category_is_refused_rather_than_written()
    {
        var dir = NewDir();
        try
        {
            var (exit, json) = Run(new GenerateWorkflowCommand.Settings
            {
                Name = "ConFmVehicleNoteWorkflow",
                TableName = "FmVehicle",
                Out = Path.Combine(dir, "ConFmVehicleNoteWorkflow.xml"),
                Output = "json",
            });

            Assert.NotEqual(0, exit);
            var root = json.RootElement;
            Assert.False(root.GetProperty("ok").GetBoolean());
            Assert.Contains("--category", root.GetProperty("error").GetProperty("message").GetString());
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_query_the_document_names_is_generated_alongside_it()
    {
        var dir = NewDir();
        try
        {
            var (exit, json) = Run(new GenerateWorkflowCommand.Settings
            {
                Name = "ConFmVehicleNoteWorkflow",
                TableName = "FmVehicle",
                Category = "FixedAssets",
                Out = Path.Combine(dir, "ConFmVehicleNoteWorkflow.xml"),
                Output = "json",
            });

            Assert.Equal(0, exit);
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());

            var queryPath = Path.Combine(dir, "ConFmVehicleNoteWorkflowQuery.xml");
            Assert.True(File.Exists(queryPath), "getQueryName() names this query — it has to exist.");

            var document = File.ReadAllText(Path.Combine(dir, "ConFmVehicleNoteWorkflowDocument.xml"));
            Assert.Contains("ConFmVehicleNoteWorkflowQuery", document);
            Assert.Contains("FmVehicle", File.ReadAllText(queryPath));

            // The CoC class name has to end with the literal "_Extension".
            Assert.True(File.Exists(Path.Combine(dir, "FmVehicle_Workflow_Extension.xml")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Opting_out_of_the_query_puts_it_back_on_the_pending_list()
    {
        var dir = NewDir();
        try
        {
            var (exit, json) = Run(new GenerateWorkflowCommand.Settings
            {
                Name = "ConFmVehicleNoteWorkflow",
                TableName = "FmVehicle",
                Category = "FixedAssets",
                QueryName = "SomeExistingQuery",
                NoQuery = true,
                Out = Path.Combine(dir, "ConFmVehicleNoteWorkflow.xml"),
                Output = "json",
            });

            Assert.Equal(0, exit);
            Assert.False(File.Exists(Path.Combine(dir, "SomeExistingQuery.xml")));

            var warnings = json.RootElement.GetProperty("warnings").EnumerateArray()
                .Select(w => w.GetString() ?? "").ToList();
            Assert.Contains(warnings, w => w.Contains("SomeExistingQuery"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d365fo-wfcmd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
