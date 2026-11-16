using System.Text.Json;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>find menu-items</c> and <c>get entity &lt;PublicCollectionName&gt;</c> through the real
/// command parser — the two shell spellings agents were missing.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class EntityAndMenuItemLookupCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-lookups-{Guid.NewGuid():N}");
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");

    public EntityAndMenuItemLookupCommandTests()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "index.sqlite");
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", db);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");

        var repo = new MetadataRepository(db);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("ContosoExt") with
        {
            IsCustom = true,
            Forms = new[] { new ExtractedForm("ContosoRentEquipmentTable", "/f.xml", Array.Empty<ExtractedFormDataSource>()) },
            MenuItems = new[]
            {
                new ExtractedMenuItem("ContosoRentEquipmentTable", "Display", "ContosoRentEquipmentTable", null, "@ContosoExt:Equipment"),
                new ExtractedMenuItem("ContosoRentEquipmentTableNew", "Action", "ContosoRentEquipmentTable", "Form", null),
            },
            DataEntities = new[]
            {
                new ExtractedDataEntity("ContosoRentEquipmentEntity", "RentEquipment", "RentEquipments", null, null, null, "/e.xml", Array.Empty<ExtractedDataEntityField>()),
            },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (int Exit, JsonElement Envelope) Run(params string[] args)
    {
        var app = CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
        }));
        var originalOut = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = app.Run([.. args, "--output", "json"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    [Fact]
    public void Find_menu_items_lists_every_menu_item_that_opens_the_form()
    {
        var (exit, envelope) = Run("find", "menu-items", "ContosoRentEquipmentTable");

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal(2, data.GetProperty("count").GetInt32());
        var display = data.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("kind").GetString() == "Display");
        Assert.Equal("Form", display.GetProperty("objectType").GetString());
        Assert.True(display.GetProperty("objectTypeIsDefault").GetBoolean());
        Assert.Equal("@ContosoExt:Equipment", display.GetProperty("label").GetString());
        Assert.Equal("ContosoExt", display.GetProperty("model").GetString());
    }

    [Fact]
    public void Get_entity_by_public_collection_name_reports_matchedOn()
    {
        var (exit, envelope) = Run("get", "entity", "RentEquipments");

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal("publicCollectionName", data.GetProperty("matchedOn").GetString());
        Assert.Equal("ContosoRentEquipmentEntity", data.GetProperty("entity").GetProperty("name").GetString());
    }

    [Fact]
    public void Get_menu_item_not_found_points_at_find_menu_items()
    {
        var (_, envelope) = Run("get", "menu-item", "ContosoNoSuchItem");

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Contains("find menu-items", envelope.GetProperty("error").GetProperty("hint").GetString());
    }
}
