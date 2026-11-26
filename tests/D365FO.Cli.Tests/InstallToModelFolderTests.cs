using D365FO.Cli.Commands.Label;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>--install-to &lt;Model&gt;</c> writes into the folder that model has. A package need not be
/// named after its model, and assuming <c>&lt;root&gt;/&lt;model&gt;/&lt;model&gt;</c> created a second,
/// unbuilt folder and reported success.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class InstallToModelFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-pkgmodel-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _store;
    private readonly string _modelDir;

    public InstallToModelFolderTests()
    {
        _store = Path.Combine(_root, "packages");
        _modelDir = Path.Combine(_store, "ConPackage", "ConFleet");
        Directory.CreateDirectory(Path.Combine(_modelDir, "AxClass"));
        WriteDescriptor("ConPackage", "ConFleet");
        Set("D365FO_CUSTOM_PACKAGES_PATH", _store);
        Set("D365FO_PACKAGES_PATH", _store);
        Set("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
        Set("D365FO_BRIDGE_ENABLED", "0");
    }

    private void WriteDescriptor(string package, string model, string? references = null)
    {
        var dir = Path.Combine(_store, package, "Descriptor");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, model + ".xml"),
            $"<AxModelInfo><Name>{model}</Name><ModuleReferences xmlns:d2p1=\"http://schemas.microsoft.com/2003/10/Serialization/Arrays\">"
            + $"{references}</ModuleReferences></AxModelInfo>");
    }

    private void Set(string name, string value)
    {
        _saved[name] = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private ToolHandlers Handlers()
    {
        var repo = new MetadataRepository(Path.Combine(_root, "index.sqlite"));
        repo.EnsureSchema();
        return new ToolHandlers(repo);
    }

    private static int CliCreate(string model)
        => new LabelCreateCommand().Execute(null!, new()
        {
            Key = "ConVehicle", Value = "Vehicle", InstallTo = model, Output = "json",
        });

    private string LabelPath(string modelDir)
        => Path.Combine(modelDir, "AxLabelFile", "LabelResources", "en-us", "ConFleet.en-us.label.txt");

    [Fact]
    public void Cli_label_lands_in_the_model_inside_a_differently_named_package()
    {
        Assert.Equal(0, CliCreate("ConFleet"));

        Assert.True(File.Exists(LabelPath(_modelDir)));
        Assert.False(Directory.Exists(Path.Combine(_store, "ConFleet")));
    }

    [Theory]
    [InlineData("ConNobody")]   // no such model at all
    [InlineData("ConPackage")]  // a package name, not a model name
    public void Cli_label_for_a_name_that_is_no_model_writes_nothing(string name)
    {
        Assert.Equal(1, CliCreate(name));

        Assert.Empty(Directory.EnumerateFiles(_store, "*.label.txt", SearchOption.AllDirectories));
        Assert.False(Directory.Exists(Path.Combine(_store, name, name)));
    }

    [Fact]
    public void A_declared_model_with_no_object_folder_yet_is_still_a_target()
    {
        WriteDescriptor("ConOther", "ConEmpty");

        var result = Handlers().GenerateClass("ConEmptyHelper", null, false, "ConEmpty", null, false);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(File.Exists(Path.Combine(_store, "ConOther", "ConEmpty", "AxClass", "ConEmptyHelper.xml")));
    }

    [Fact]
    public void Mcp_label_lands_in_the_model_inside_a_differently_named_package()
    {
        var result = Handlers().CreateLabel(null, "ConVehicle", "Vehicle", installTo: "ConFleet");

        Assert.True(result.Ok, result.Error?.Message);
        Assert.True(File.Exists(LabelPath(_modelDir)));
        Assert.False(Directory.Exists(Path.Combine(_store, "ConFleet")));
    }

    [Fact]
    public void Mcp_install_lands_in_the_model_and_refuses_an_unknown_one()
    {
        var handlers = Handlers();

        var ok = handlers.GenerateClass("ConFleetHelper", null, false, "ConFleet", null, false);
        var unknown = handlers.GenerateClass("ConFleetHelper", null, false, "ConNobody", null, false);

        Assert.True(ok.Ok, ok.Error?.Message);
        Assert.True(File.Exists(Path.Combine(_modelDir, "AxClass", "ConFleetHelper.xml")));
        Assert.False(unknown.Ok);
        Assert.Equal("INSTALL_FAILED", unknown.Error!.Code);
        Assert.False(Directory.Exists(Path.Combine(_store, "ConNobody")));
    }

    [Fact]
    public void TestEssentials_is_read_from_the_models_own_package()
    {
        WriteDescriptor("ConPackage", "ConFleet", "<d2p1:string>TestEssentials</d2p1:string>");
        var handlers = Handlers();

        var result = handlers.PrepareTest("ConFleetService", null, null, "ConFleet");

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        Assert.Contains("references TestEssentials", json);
        Assert.DoesNotContain("does not reference TestEssentials", json);
    }
}
