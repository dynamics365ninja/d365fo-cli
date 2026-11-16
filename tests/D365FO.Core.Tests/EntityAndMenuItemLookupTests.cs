using System.Text.Json;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Two offline lookups agents needed and did not find, so they asked a live ERP server instead
/// and most of those calls timed out: an entity by the OData entity set an integration error
/// names, and the menu items that open a given form.
/// </summary>
public class EntityAndMenuItemLookupTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-test-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private MetadataRepository Seed()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("ApplicationSuite") with
        {
            Forms = new[] { new ExtractedForm("CustTable", "/f1", Array.Empty<ExtractedFormDataSource>()) },
            MenuItems = new[]
            {
                // AOT XML omits ObjectType when it is the default, Form: the extractor stores null.
                new ExtractedMenuItem("CustTable", "Display", "CustTable", null, "@SYS11307"),
                new ExtractedMenuItem("CustTableListPage", "Display", "CustTableListPage", null, null),
                new ExtractedMenuItem("CustTableEdit", "Action", "CustTableEditController", "Class", null),
                new ExtractedMenuItem("CustTableReport", "Output", "custtable", "SSRSReport", "@SYS301"),
            },
            DataEntities = new[]
            {
                new ExtractedDataEntity("CustCustomerV3Entity", "CustomerV3", "CustomersV3", null, null, null, "/d1", Array.Empty<ExtractedDataEntityField>()),
                // Publishes, as its public names, the AOT name of another entity: the AOT name must still win.
                new ExtractedDataEntity("ContosoShadowEntity", "CustCustomerV3Entity", "CustCustomerV3Entity", null, null, null, "/d2", Array.Empty<ExtractedDataEntityField>()),
            },
        });
        repo.ApplyExtract(ExtractBatch.Empty("ContosoExt") with
        {
            IsCustom = true,
            MenuItems = new[] { new ExtractedMenuItem("ContosoCustTable", "Display", "CustTable", "Form", "@ContosoExt:Customers") },
        });
        return repo;
    }

    // ---- get entity ----

    [Fact]
    public void Get_entity_resolves_the_public_collection_name_an_odata_url_names()
    {
        var e = Seed().GetDataEntity("CustomersV3");

        Assert.NotNull(e);
        Assert.Equal("CustCustomerV3Entity", e!.Entity.Name);
        Assert.Equal("publicCollectionName", e.MatchedOn);
    }

    [Fact]
    public void Get_entity_says_which_name_matched()
    {
        var repo = Seed();

        Assert.Equal("publicEntityName", repo.GetDataEntity("CustomerV3")!.MatchedOn);
        Assert.Equal("name", repo.GetDataEntity("ContosoShadowEntity")!.MatchedOn);
        Assert.Null(repo.GetDataEntity("NoSuchCollection"));
    }

    [Fact]
    public void Get_entity_prefers_the_aot_name_over_another_entitys_public_name()
    {
        var e = Seed().GetDataEntity("CustCustomerV3Entity");

        Assert.Equal("CustCustomerV3Entity", e!.Entity.Name);
        Assert.Equal("name", e.MatchedOn);
    }

    [Fact]
    public void Get_object_info_entity_reports_matchedOn_over_mcp()
    {
        var handlers = new ToolHandlers(Seed());

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(handlers.GetDataEntity("CustomersV3").Data,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal("publicCollectionName", doc.RootElement.GetProperty("matchedOn").GetString());
        Assert.Equal("CustCustomerV3Entity", doc.RootElement.GetProperty("entity").GetProperty("name").GetString());
    }

    // ---- menu items by object ----

    [Fact]
    public void Find_menu_items_matches_the_object_exactly_not_a_name_substring()
    {
        var items = Seed().FindMenuItemsByObject("CustTable");

        // CustTableListPage opens another form and CustTableEdit a class whose name only starts
        // with CustTable: `search any --kind menuitem` returned both, this must not.
        Assert.Equal(new[] { "ContosoCustTable", "CustTable", "CustTableReport" }, items.Select(i => i.Name));
    }

    [Fact]
    public void Find_menu_items_returns_every_kind_and_ignores_case()
    {
        var output = Assert.Single(Seed().FindMenuItemsByObject("CUSTTABLE"), i => i.Kind == "Output");

        Assert.Equal("CustTableReport", output.Name);
        Assert.Equal("SSRSReport", output.ObjectType);
        Assert.Equal("@SYS301", output.Label);
        Assert.Equal("ApplicationSuite", output.Model);
    }

    [Fact]
    public void Find_menu_items_lists_custom_models_first()
    {
        var first = Seed().FindMenuItemsByObject("CustTable").First();

        Assert.Equal("ContosoCustTable", first.Name);
        Assert.Equal("ContosoExt", first.Model);
    }

    [Fact]
    public void A_menu_item_without_an_ObjectType_element_opens_a_form_and_says_so()
    {
        var repo = Seed();

        var implicitForm = repo.GetMenuItem("CustTable")!;
        Assert.Equal("Form", implicitForm.ObjectType);
        Assert.True(implicitForm.ObjectTypeIsDefault);

        var explicitClass = repo.GetMenuItem("CustTableEdit")!;
        Assert.Equal("Class", explicitClass.ObjectType);
        Assert.False(explicitClass.ObjectTypeIsDefault);
    }

    [Fact]
    public void Search_menu_items_over_mcp_answers_which_menu_items_open_a_form()
    {
        var handlers = new ToolHandlers(Seed());
        var search = ToolCatalog.All.Single(d => d.Name == "search");

        var result = Assert.IsType<ToolResult<object>>(
            search.Invoke(handlers, JsonDocument.Parse("""{"type":"menu-items","query":"CustTable"}""").RootElement));

        Assert.True(result.Ok);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(3, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Contains("form", doc.RootElement.GetProperty("objectIndexedAs").EnumerateArray().Select(k => k.GetString()));
    }

    [Fact]
    public void An_empty_answer_for_an_unknown_object_says_the_object_is_unknown()
    {
        var result = MenuItemLookup.ForObject(Seed(), "CustTabel");

        Assert.True(result.Ok);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Contains("search any CustTabel", doc.RootElement.GetProperty("hint").GetString());
    }

    [Fact]
    public void Find_menu_items_refuses_an_empty_object_name_with_a_fix()
    {
        var result = MenuItemLookup.ForObject(Seed(), "  ");

        Assert.False(result.Ok);
        Assert.Equal("BAD_INPUT", result.Error!.Code);
        Assert.False(string.IsNullOrEmpty(result.Error.Hint));
    }
}
