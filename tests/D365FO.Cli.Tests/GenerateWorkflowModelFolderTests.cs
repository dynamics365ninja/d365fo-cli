using System.Text.Json;
using System.Xml.Linq;
using D365FO.Cli.Commands.Generate;
using D365FO.Core;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>generate workflow</c> against a model store that is not <c>PackagesLocalDirectory</c>,
/// which is the layout of a unified developer environment and of any repo that keeps metadata
/// and projects apart.
/// </summary>
/// <remarks>
/// Three failures seen on one such environment: <c>--install-to</c> could not find a model that
/// lives under <c>D365FO_CUSTOM_PACKAGES_PATH</c>, <c>--out</c> into the model put the document
/// class, the query, the approval and the CoC class all inside <c>AxWorkflowTemplate\</c>, and
/// every file had to be added to the Visual Studio project by hand.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class GenerateWorkflowModelFolderTests : IDisposable
{
    private const string Model = "ConTestModel";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-wfmodel-" + Guid.NewGuid().ToString("N"));
    private readonly string _store;
    private readonly string _modelDir;
    private readonly Dictionary<string, string?> _saved = new();

    public GenerateWorkflowModelFolderTests()
    {
        _store = Path.Combine(_root, "Metadata");
        _modelDir = Path.Combine(_store, Model, Model);
        Directory.CreateDirectory(Path.Combine(_modelDir, "AxClass"));

        // A config root of its own, so the machine's active profile (and its packages paths)
        // cannot answer for the model.
        Set(D365FoSettings.ConfigDirKey, Path.Combine(_root, "config"));
        Set(D365FoProfiles.ProfileKey, null);
        Set("D365FO_PACKAGES_PATH", null);
        Set("D365FO_WORKSPACE_PATH", null);
        Set("D365FO_BRIDGE_ENABLED", null);
        Set("D365FO_CUSTOM_PACKAGES_PATH", _store);
        D365FoProfiles.FlagProfile = null;
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved) Environment.SetEnvironmentVariable(key, value);
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Set(string key, string? value)
    {
        if (!_saved.ContainsKey(key)) _saved[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

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

    private void AssertEachPartInItsOwnFolder()
    {
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxWorkflowTemplate", "ConVehicleWorkflow.xml")));
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxClass", "ConVehicleWorkflowDocument.xml")));
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxClass", "FmVehicle_Workflow_Extension.xml")));
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxQuery", "ConVehicleWorkflowQuery.xml")));
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxWorkflowApproval", "ConVehicleApproval.xml")));
        Assert.Single(Directory.GetFiles(Path.Combine(_modelDir, "AxWorkflowTemplate")));
    }

    private static GenerateWorkflowCommand.Settings Workflow(string? installTo = null, string? output = null, string? project = null) => new()
    {
        Name = "ConVehicleWorkflow",
        TableName = "FmVehicle",
        Category = "FixedAssets",
        ApprovalName = "ConVehicleApproval",
        InstallTo = installTo,
        Out = output,
        Project = project,
        Output = "json",
    };

    [Fact]
    public void Install_to_finds_a_model_under_the_custom_packages_path_without_the_bridge()
    {
        var (exit, json) = Run(Workflow(installTo: Model));

        Assert.True(exit == 0, json.RootElement.ToString());
        AssertEachPartInItsOwnFolder();
    }

    [Fact]
    public void Out_inside_a_model_folder_writes_each_part_to_its_own_Ax_folder()
    {
        var (exit, json) = Run(Workflow(output: Path.Combine(_modelDir, "AxWorkflowTemplate", "ConVehicleWorkflow.xml")));

        Assert.True(exit == 0, json.RootElement.ToString());
        AssertEachPartInItsOwnFolder();
    }

    [Fact]
    public void Install_to_an_unknown_model_names_the_roots_it_searched()
    {
        var (exit, json) = Run(Workflow(installTo: "NoSuchModel"));

        Assert.NotEqual(0, exit);
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("INSTALL_FAILED", error.GetProperty("code").GetString());
        Assert.Contains(_store, error.GetProperty("message").GetString());
        Assert.Contains("D365FO_CUSTOM_PACKAGES_PATH", error.GetProperty("hint").GetString());
    }

    [Fact]
    public void Project_lists_every_part_the_workflow_wrote_in_the_visual_studio_item_shape()
    {
        var project = WriteProject(Model);

        var (exit, json) = Run(Workflow(installTo: Model, project: project));

        Assert.True(exit == 0, json.RootElement.ToString());
        var doc = XDocument.Load(project);
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var items = doc.Descendants(ns + "Content").ToDictionary(e => (string)e.Attribute("Include")!);

        foreach (var include in new[]
                 {
                     @"AxWorkflowTemplate\ConVehicleWorkflow", @"AxClass\ConVehicleWorkflowDocument",
                     @"AxClass\FmVehicle_Workflow_Extension", @"AxQuery\ConVehicleWorkflowQuery",
                     @"AxWorkflowApproval\ConVehicleApproval",
                 })
        {
            Assert.True(items.ContainsKey(include), $"{include} is not in the project");
        }

        var approval = items[@"AxWorkflowApproval\ConVehicleApproval"];
        Assert.Equal("Content", approval.Element(ns + "SubType")?.Value);
        Assert.Equal("ConVehicleApproval", approval.Element(ns + "Name")?.Value);
        Assert.Equal(@"Workflow Approvals\ConVehicleApproval", approval.Element(ns + "Link")?.Value);

        // The entry the project already had is untouched, and the payload says what was added.
        Assert.True(items.ContainsKey(@"AxClass\ExistingClass"));
        var reported = json.RootElement.GetProperty("data").GetProperty("project").GetProperty("items");
        Assert.Equal(5, reported.GetArrayLength());
    }

    [Fact]
    public void A_project_that_does_not_exist_is_refused_before_anything_is_written()
    {
        var (exit, json) = Run(Workflow(installTo: Model, project: Path.Combine(_root, "Missing.rnrproj")));

        Assert.NotEqual(0, exit);
        Assert.Equal("BAD_INPUT", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(Directory.Exists(Path.Combine(_modelDir, "AxWorkflowTemplate")));
    }

    [Fact]
    public void Project_does_not_list_an_object_written_outside_a_model_folder()
    {
        var project = WriteProject(Model);
        var staging = Path.Combine(_root, "staging");
        Directory.CreateDirectory(staging);

        var (exit, json) = Run(Workflow(output: Path.Combine(staging, "ConVehicleWorkflow.xml"), project: project));

        Assert.True(exit == 0, json.RootElement.ToString());
        // A flat staging folder keeps the old behaviour: every part beside the template.
        Assert.True(File.Exists(Path.Combine(staging, "ConVehicleWorkflowDocument.xml")));
        Assert.DoesNotContain("ConVehicleWorkflow", File.ReadAllText(project));
        var warnings = json.RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString() ?? "").ToList();
        Assert.Contains(warnings, w => w.StartsWith("--project:", StringComparison.Ordinal) && w.Contains("not in the model's"));
    }

    [Fact]
    public void Custom_service_out_inside_a_model_puts_the_class_and_the_group_in_their_own_folders()
    {
        // The same single-folder bug, in another multi-object generator.
        var saved = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = new GenerateCustomServiceCommand().Execute(null!, new GenerateCustomServiceCommand.Settings
            {
                Name = "ConVehicleQuery",
                Operations = ["getVehicle:str"],
                Out = Path.Combine(_modelDir, "AxService", "ConVehicleQuery.xml"),
                Output = "json",
            });
        }
        finally
        {
            Console.SetOut(saved);
        }

        Assert.True(exit == 0, writer.ToString());
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxService", "ConVehicleQuery.xml")));
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxClass", "ConVehicleQueryService.xml")));
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxServiceGroup", "ConVehicleQueryGroup.xml")));
    }

    private string WriteProject(string model)
    {
        var dir = Path.Combine(_root, "Projects", "ConTest");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "ConTest.rnrproj");
        File.WriteAllText(path,
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
            "<Project ToolsVersion=\"14.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
            "  <PropertyGroup>\r\n" +
            $"    <Model>{model}</Model>\r\n" +
            "  </PropertyGroup>\r\n" +
            "  <ItemGroup>\r\n" +
            "    <Folder Include=\"Classes\\\"/>\r\n" +
            "  </ItemGroup>\r\n" +
            "  <ItemGroup>\r\n" +
            "    <Content Include=\"AxClass\\ExistingClass\">\r\n" +
            "      <SubType>Content</SubType>\r\n" +
            "      <Name>ExistingClass</Name>\r\n" +
            "      <Link>Classes\\ExistingClass</Link>\r\n" +
            "    </Content>\r\n" +
            "  </ItemGroup>\r\n" +
            "  <Import Project=\"$(MSBuildBinPath)\\Microsoft.Common.targets\"/>\r\n" +
            "</Project>\r\n");
        return path;
    }
}
