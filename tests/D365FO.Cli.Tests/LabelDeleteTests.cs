using D365FO.Cli.Commands.Label;
using D365FO.Core.Index;
using D365FO.Core.Journal;
using D365FO.Core.Labels;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>labels delete</c> removes a label as a whole: its comment with it, from every language when
/// the model is named, and not while the model still uses it.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class LabelDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-lbl-del-{Guid.NewGuid():N}");
    private readonly Dictionary<string, string?> _saved = new();
    private readonly string _store;
    private readonly string _modelDir;
    private readonly string _en;
    private readonly string _cs;

    public LabelDeleteTests()
    {
        _store = Path.Combine(_root, "packages");
        _modelDir = Path.Combine(_store, "ConFleet", "ConFleet");
        Directory.CreateDirectory(Path.Combine(_modelDir, "AxClass"));
        _en = Seed(_modelDir, "ConFleet", "en-us");
        _cs = Seed(_modelDir, "ConFleet", "cs");
        Set("D365FO_CUSTOM_PACKAGES_PATH", _store);
        Set("D365FO_PACKAGES_PATH", _store);
        Set("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
        Set("D365FO_CUSTOM_MODELS", "Con*");
        Set("D365FO_BRIDGE_ENABLED", "0");
    }

    private static string Seed(string modelDir, string id, string lang)
    {
        var dir = Path.Combine(modelDir, "AxLabelFile", "LabelResources", lang);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{id}.{lang}.label.txt");
        File.WriteAllLines(path,
        [
            "ConDriver=Driver", " ;driver note",
            "ConVehicle=Vehicle", " ;vehicle note",
            "ConWheel=Wheel",
        ]);
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

    private static int Delete(LabelDeleteCommand.Settings settings)
        => new LabelDeleteCommand().Execute(null!, settings);

    private void UseInCode(string token)
        => File.WriteAllText(Path.Combine(_modelDir, "AxClass", "ConUser.xml"),
            $"<AxClass><Name>ConUser</Name><SourceCode><Declaration><![CDATA[info(\"{token}\");]]></Declaration></SourceCode></AxClass>");

    private static void AssertUntouched(string path)
        => Assert.Contains("ConVehicle=Vehicle", File.ReadAllText(path));

    [Fact]
    public void The_comment_goes_with_its_label()
    {
        Assert.Equal(0, Delete(new() { Key = "ConVehicle", File = _en, Output = "json" }));

        Assert.Equal(
            ["ConDriver=Driver", " ;driver note", "ConWheel=Wheel"],
            File.ReadAllLines(_en));
        AssertUntouched(_cs);
    }

    [Fact]
    public void Undo_brings_back_the_comment_too()
    {
        Assert.Equal(0, Delete(new() { Key = "ConVehicle", File = _en, Output = "json" }));

        var journal = ModificationJournal.ForIndex();
        var undone = UndoEngine.Undo(journal, 1, dryRun: false);

        Assert.True(undone.AllOk);
        Assert.Equal(
            ["ConDriver=Driver", " ;driver note", "ConVehicle=Vehicle", " ;vehicle note", "ConWheel=Wheel"],
            File.ReadAllLines(_en));
    }

    [Fact]
    public void Naming_the_model_removes_the_label_from_every_language()
    {
        Assert.Equal(0, Delete(new() { Key = "ConVehicle", InstallTo = "ConFleet", Output = "json" }));

        Assert.DoesNotContain("ConVehicle", File.ReadAllText(_en));
        Assert.DoesNotContain("ConVehicle", File.ReadAllText(_cs));
    }

    [Fact]
    public void A_delete_from_several_languages_names_the_undo_that_restores_them_all()
    {
        var result = LabelDeletion.Run(new LabelDeleteRequest("ConVehicle", [_en, _cs]), repo: null, "labels delete");

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Contains("d365fo undo --steps 2", System.Text.Json.JsonSerializer.Serialize(result.Data));

        Assert.True(UndoEngine.Undo(ModificationJournal.ForIndex(), 2, dryRun: false).AllOk);
        AssertUntouched(_en);
        AssertUntouched(_cs);
    }

    [Fact]
    public void A_label_the_model_still_uses_is_refused_unless_forced()
    {
        UseInCode("@ConFleet:ConVehicle");

        Assert.Equal(1, Delete(new() { Key = "ConVehicle", InstallTo = "ConFleet", Output = "json" }));
        AssertUntouched(_en);
        AssertUntouched(_cs);

        Assert.Equal(0, Delete(new() { Key = "ConVehicle", InstallTo = "ConFleet", Force = true, Output = "json" }));
        Assert.DoesNotContain("ConVehicle", File.ReadAllText(_en));
    }

    [Fact]
    public void A_longer_key_is_not_a_reference_to_a_shorter_one()
    {
        UseInCode("@ConFleet:ConVehicleName");

        Assert.Equal(0, Delete(new() { Key = "ConVehicle", InstallTo = "ConFleet", Output = "json" }));
    }

    [Fact]
    public void Dry_run_writes_nothing()
    {
        Assert.Equal(0, Delete(new() { Key = "ConVehicle", InstallTo = "ConFleet", DryRun = true, Output = "json" }));

        AssertUntouched(_en);
        AssertUntouched(_cs);
    }

    [Fact]
    public void An_extension_label_file_is_refused()
    {
        var file = Seed(_modelDir, "SysBase_Extension", "en-us");

        Assert.Equal(1, Delete(new() { Key = "ConVehicle", File = file, Output = "json" }));

        AssertUntouched(file);
    }

    [Theory]
    [InlineData("Vendor", null)]
    [InlineData("ConShipped", "Microsoft Corporation")]
    public void A_model_that_is_not_yours_is_refused(string model, string? publisher)
    {
        var modelDir = Path.Combine(_store, model, model);
        var file = Seed(modelDir, model, "en-us");
        if (publisher is not null)
        {
            var descriptor = Path.Combine(_store, model, "Descriptor");
            Directory.CreateDirectory(descriptor);
            File.WriteAllText(Path.Combine(descriptor, model + ".xml"),
                $"<AxModelInfo><Name>{model}</Name><Publisher>{publisher}</Publisher></AxModelInfo>");
        }

        Assert.Equal(1, Delete(new() { Key = "ConVehicle", File = file, Force = true, Output = "json" }));

        AssertUntouched(file);
    }

    [Fact]
    public void Mcp_delete_applies_the_same_checks()
    {
        UseInCode("@ConFleet:ConVehicle");
        var handlers = new ToolHandlers(new MetadataRepository(Path.Combine(_root, "index.sqlite")));

        var refused = handlers.DeleteLabel(null, "ConVehicle", "ConFleet", null, null, dryRun: false, force: false);

        Assert.False(refused.Ok);
        Assert.Equal("LABEL_IN_USE", refused.Error!.Code);
        AssertUntouched(_en);

        var forced = handlers.DeleteLabel(null, "ConVehicle", "ConFleet", null, null, dryRun: false, force: true);

        Assert.True(forced.Ok, forced.Error?.Message);
        Assert.DoesNotContain("ConVehicle", File.ReadAllText(_cs));
    }
}
