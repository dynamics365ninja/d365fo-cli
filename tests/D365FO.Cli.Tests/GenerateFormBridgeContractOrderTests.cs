using System.Text.Json;
using System.Xml.Linq;
using D365FO.Cli.Commands.Generate;
using D365FO.Cli.Commands.Index;
using D365FO.Core;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>generate form --install-to</c> with the bridge on must hand the Metadata API XML in
/// contract member order, as every other generate command does.
/// </summary>
/// <remarks>
/// The provider reads with <c>DataContractSerializer</c>, which skips an element that arrives
/// out of contract order without an error. Forms are rendered from string templates and went
/// to the bridge un-canonicalised, so a <c>SimpleListDetails</c> form was saved with its tab
/// pages emptied — the field groups and their fields gone, <c>ColumnsMode</c> lost — and the
/// model then failed to build with FormPatternValidation errors. The disk path canonicalised
/// in <c>ScaffoldFileWriter.Write(string)</c> and was unaffected.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class GenerateFormBridgeContractOrderTests : IDisposable
{
    private const string Model = "ConFormOrder";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-formorder-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _saved = new();

    public GenerateFormBridgeContractOrderTests()
    {
        var store = Path.Combine(_root, "Metadata");
        var tableDir = Path.Combine(store, Model, Model, "AxTable");
        Directory.CreateDirectory(tableDir);
        File.WriteAllText(Path.Combine(tableDir, "ConFormOrderTable.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConFormOrderTable</Name>
              <Fields>
                <AxTableField i:type="AxTableFieldString">
                  <Name>VehicleId</Name>
                  <ExtendedDataType>Name</ExtendedDataType>
                </AxTableField>
                <AxTableField i:type="AxTableFieldString">
                  <Name>Description</Name>
                  <ExtendedDataType>Name</ExtendedDataType>
                </AxTableField>
              </Fields>
            </AxTable>
            """);

        var db = Path.Combine(_root, "index.sqlite");
        Set(D365FoSettings.ConfigDirKey, Path.Combine(_root, "config"));
        Set(D365FoProfiles.ProfileKey, null);
        Set("D365FO_PACKAGES_PATH", null);
        Set("D365FO_WORKSPACE_PATH", null);
        Set("D365FO_BRIDGE_ENABLED", null);
        Set("D365FO_CUSTOM_PACKAGES_PATH", store);
        Set("D365FO_CUSTOM_MODELS", Model);
        Set("D365FO_INDEX_DB", db);
        D365FoProfiles.FlagProfile = null;

        var original = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            Assert.Equal(0, IndexExtractCommand.ExtractCore(OutputMode.Kind.Json, store, db, onlyModel: Model, sinceIso: null));
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    public void Dispose()
    {
        foreach (var (key, value) in _saved) Environment.SetEnvironmentVariable(key, value);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Set(string key, string? value)
    {
        if (!_saved.ContainsKey(key)) _saved[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    [Theory]
    [InlineData("SimpleListDetails")]
    [InlineData("SimpleList")]
    [InlineData("DetailsMaster")]
    [InlineData("ListPage")]
    [InlineData("Lookup")]
    [InlineData("Dialog")]
    [InlineData("TableOfContents")]
    [InlineData("Workspace")]
    public void Xml_handed_to_the_bridge_is_in_contract_order(string pattern)
    {
        var formName = "ConFormOrder" + pattern;
        string? sent = null;
        var real = GenerateInstaller.SaveViaBridge;
        // Other test classes may emit in parallel: answer only for this test's form.
        GenerateInstaller.SaveViaBridge = (kind, name, model, xml) =>
        {
            if (name != formName) return real(kind, name, model, xml);
            sent = xml;
            return (true, null);
        };

        var saved = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = new GenerateFormCommand().Execute(null!, new GenerateFormCommand.Settings
            {
                FormName = formName,
                Pattern = pattern,
                Table = "ConFormOrderTable",
                // A workspace takes no fields: its lists live in separate part forms.
                Fields = pattern == "Workspace" ? [] : ["VehicleId", "Description"],
                InstallTo = Model,
                Output = "json",
            });
        }
        finally
        {
            Console.SetOut(saved);
            GenerateInstaller.SaveViaBridge = real;
        }

        using var json = JsonDocument.Parse(writer.ToString());
        Assert.True(exit == 0, json.RootElement.ToString());
        Assert.NotNull(sent);

        var doc = XDocument.Parse(sent!);
        var before = doc.ToString(SaveOptions.DisableFormatting);
        ContractOrderCanonicalizer.Apply(doc);
        Assert.Equal(before, doc.ToString(SaveOptions.DisableFormatting));
    }
}
