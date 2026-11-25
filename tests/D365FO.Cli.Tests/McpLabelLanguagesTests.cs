using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// MCP <c>labels create</c> takes the same comma-separated <c>lang</c> the CLI does. Unsplit, it
/// created a folder and a file literally named <c>en-us,cs</c> and reported success.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class McpLabelLanguagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-mcp-lang-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _resources;

    public McpLabelLanguagesTests()
    {
        var store = Path.Combine(_root, "packages");
        _resources = Path.Combine(store, "ConFleet", "ConFleet", "AxLabelFile", "LabelResources");
        Directory.CreateDirectory(Path.Combine(_resources, "en-US"));
        Set("D365FO_CUSTOM_PACKAGES_PATH", store);
        Set("D365FO_PACKAGES_PATH", store);
        Set("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
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

    private ToolHandlers Handlers() => new(new MetadataRepository(Path.Combine(_root, "index.sqlite")));

    private string[] Folders() => Directory.GetDirectories(_resources).Select(d => Path.GetFileName(d)!).Order().ToArray();

    [Fact]
    public void Several_languages_write_one_file_each_reusing_the_folder_casing_on_disk()
    {
        var result = Handlers().CreateLabel(null, "ConVehicle", "Vehicle", installTo: "ConFleet", lang: "en-us, cs");

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(["cs", "en-US"], Folders());
        Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(Path.Combine(_resources, "en-US", "ConFleet.en-US.label.txt")));
        Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(Path.Combine(_resources, "cs", "ConFleet.cs.label.txt")));
    }

    [Fact]
    public void A_key_that_exists_in_one_language_writes_none()
    {
        var handlers = Handlers();
        Assert.True(handlers.CreateLabel(null, "ConVehicle", "Vozidlo", installTo: "ConFleet", lang: "cs").Ok);

        var result = handlers.CreateLabel(null, "ConVehicle", "Vehicle", installTo: "ConFleet", lang: "en-us,cs");

        Assert.False(result.Ok);
        Assert.Equal("KEY_EXISTS", result.Error!.Code);
        Assert.Empty(Directory.GetFiles(Path.Combine(_resources, "en-US")));
    }

    [Fact]
    public void An_explicit_file_ignores_lang()
    {
        var file = Path.Combine(_resources, "en-US", "ConFleet.en-US.label.txt");

        var result = Handlers().CreateLabel(file, "ConVehicle", "Vehicle", lang: "en-us,cs");

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(["en-US"], Folders());
    }
}
