using System.Text.Json;
using D365FO.Cli.Commands.Generate;
using D365FO.Cli.Commands.Index;
using D365FO.Core;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// #232: <c>generate find-methods --apply-to</c> writes the standard <c>exist()</c> and leaves no
/// <c>&lt;Table&gt;.xml.bak</c> inside the model folder, where it would end up in source control.
/// The pre-image is in the journal, so <c>d365fo undo</c> is the way back.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class GenerateFindMethodsApplyTests : IDisposable
{
    private const string Model = "ConFind";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-findapply-" + Guid.NewGuid().ToString("N"));
    private readonly string _tablePath;
    private readonly Dictionary<string, string?> _saved = new();

    public GenerateFindMethodsApplyTests()
    {
        var store = Path.Combine(_root, "Metadata");
        var tableDir = Path.Combine(store, Model, Model, "AxTable");
        Directory.CreateDirectory(tableDir);
        _tablePath = Path.Combine(tableDir, "ConFindTable.xml");
        File.WriteAllText(_tablePath, """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConFindTable</Name>
              <Fields>
                <AxTableField i:type="AxTableFieldString">
                  <Name>VehicleId</Name>
                  <ExtendedDataType>Name</ExtendedDataType>
                </AxTableField>
              </Fields>
              <Indexes>
                <AxTableIndex>
                  <Name>VehicleIdx</Name>
                  <AlternateKey>Yes</AlternateKey>
                  <Fields>
                    <AxTableIndexField>
                      <DataField>VehicleId</DataField>
                    </AxTableIndexField>
                  </Fields>
                </AxTableIndex>
              </Indexes>
            </AxTable>
            """);

        var db = Path.Combine(_root, "index.sqlite");
        Set(D365FoSettings.ConfigDirKey, Path.Combine(_root, "config"));
        Set(D365FoProfiles.ProfileKey, null);
        Set("D365FO_PACKAGES_PATH", null);
        Set("D365FO_WORKSPACE_PATH", null);
        Set("D365FO_BRIDGE_ENABLED", null);
        Set("D365FO_CUSTOM_PACKAGES_PATH", store);
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

    [Fact]
    public void Apply_to_writes_exist_and_keeps_no_bak_beside_the_table()
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = new GenerateFindMethodsCommand().Execute(null!, new GenerateFindMethodsCommand.Settings
            {
                Table = "ConFindTable",
                ApplyTo = _tablePath,
                Output = "json",
            });
        }
        finally
        {
            Console.SetOut(saved);
        }

        using var json = JsonDocument.Parse(writer.ToString());
        Assert.True(exit == 0, json.RootElement.ToString());

        var written = File.ReadAllText(_tablePath);
        Assert.Contains("public static boolean exist(", written);
        Assert.Contains("ConFindTable conFindTable;", written);
        Assert.False(File.Exists(_tablePath + ".bak"), "a .bak was left inside the model folder");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_tablePath)!, "*.bak"));
    }
}
