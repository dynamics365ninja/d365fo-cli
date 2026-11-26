using D365FO.Cli.Commands.Label;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// A label correction writes the languages it was given a text for, and no other
/// (upstream 1.21.0: update replaced the other translations with the one it was handed).
/// </summary>
[Collection("EnvIndexDb")]
public sealed class LabelUpdateLanguagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-lbl-upd-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _en;
    private readonly string _cs;

    public LabelUpdateLanguagesTests()
    {
        var store = Path.Combine(_root, "packages");
        var resources = Path.Combine(store, "ConFleet", "ConFleet", "AxLabelFile", "LabelResources");
        _en = Seed(resources, "en-us", "Vehicle");
        _cs = Seed(resources, "cs", "Vozidlo");
        Set("D365FO_CUSTOM_PACKAGES_PATH", store);
        Set("D365FO_PACKAGES_PATH", store);
        Set("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
    }

    private static string Seed(string resources, string lang, string value)
    {
        var dir = Path.Combine(resources, lang);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"ConFleet.{lang}.label.txt");
        File.WriteAllLines(path, [$"ConVehicle={value}", " ;translator note"]);
        return path;
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

    private static int Update(LabelUpdateCommand.Settings settings)
        => new LabelUpdateCommand().Execute(null!, settings);

    [Fact]
    public void A_language_without_a_text_keeps_its_translation()
    {
        var exit = Update(new()
        {
            Key = "ConVehicle", InstallTo = "ConFleet", Lang = "en-us,cs",
            Text = ["en-us=Car"], Output = "json",
        });

        Assert.Equal(0, exit);
        Assert.Contains("ConVehicle=Car", File.ReadAllText(_en));
        Assert.Contains("ConVehicle=Vozidlo", File.ReadAllText(_cs));
        Assert.Contains(" ;translator note", File.ReadAllText(_cs));
    }

    [Fact]
    public void A_positional_value_is_refused_beside_several_languages()
    {
        var exit = Update(new()
        {
            Key = "ConVehicle", Value = "Car", InstallTo = "ConFleet", Lang = "en-us,cs",
            Text = ["en-us=Car"], Output = "json",
        });

        Assert.Equal(1, exit);
        Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(_en));
        Assert.Contains("ConVehicle=Vozidlo", File.ReadAllText(_cs));
    }

    [Fact]
    public void An_explicit_file_takes_the_text_of_its_own_language()
    {
        var exit = Update(new()
        {
            Key = "ConVehicle", File = _cs, Text = ["en-us=Car", "cs=Auto"], Output = "json",
        });

        Assert.Equal(0, exit);
        Assert.Contains("ConVehicle=Auto", File.ReadAllText(_cs));
    }

    [Fact]
    public void An_explicit_file_with_no_text_for_its_language_is_left_alone()
    {
        var exit = Update(new()
        {
            Key = "ConVehicle", File = _cs, Text = ["en-us=Car", "de=Auto"], Output = "json",
        });

        Assert.Equal(1, exit);
        Assert.Contains("ConVehicle=Vozidlo", File.ReadAllText(_cs));
    }

    [Fact]
    public void Mcp_update_refuses_one_value_for_several_languages()
    {
        var handlers = new ToolHandlers(new MetadataRepository(Path.Combine(_root, "index.sqlite")));

        var result = handlers.UpdateLabel(null, "ConVehicle", "Car", "ConFleet", "en-us,cs", null);

        Assert.False(result.Ok);
        Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(_en));
        Assert.Contains("ConVehicle=Vozidlo", File.ReadAllText(_cs));
    }
}
