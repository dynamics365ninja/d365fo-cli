using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #249 item 8: where an object is referenced in metadata, answered from the index with no
/// build. Two models, so the model filter and the cross-model rows are both exercised.
/// </summary>
public sealed class StructuralWhereUsedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"whereused-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public StructuralWhereUsedTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        _repo.ApplyExtract(ExtractBatch.Empty("ApplicationSuite") with
        {
            Tables = new[]
            {
                new ExtractedTable("CustTable", null, null, new[] { new ExtractedTableField("AccountNum", "ExtendedDataType", "CustAccount", null, true) }),
                new ExtractedTable("SalesTable", null, null, new[] { new ExtractedTableField("CustAccount", "ExtendedDataType", "CustAccount", null, true) })
                {
                    Relations = new[] { new ExtractedTableRelation("CustTable", "CustTable", "ZeroMore", null) },
                },
            },
            Edts = new[] { new ExtractedEdt("CustAccount", "CustVendAC", "String", null, 20) { ReferenceTable = "CustTable" } },
            Forms = new[] { new ExtractedForm("CustTable", null, new[] { new ExtractedFormDataSource("CustTable", "CustTable") }) },
            Queries = new[] { new ExtractedQuery("CustTableQuery", null, new[] { new ExtractedQueryDataSource("Cust", "CustTable", null, null) }) },
            MenuItems = new[] { new ExtractedMenuItem("CustTable", "Display", "CustTable", "Form", null) },
            Privileges = new[]
            {
                new ExtractedSecurityPrivilege("CustTableView", null, new[] { new ExtractedSecurityEntryPoint("CustTable", "MenuItemDisplay", null, "Read") }),
            },
            Duties = new[] { new ExtractedSecurityDuty("CustTableInquire", null, new[] { "CustTableView" }) },
        });
        _repo.ApplyExtract(ExtractBatch.Empty("ContosoExt") with
        {
            IsCustom = true,
            Forms = new[] { new ExtractedForm("ContosoCustOverview", null, new[] { new ExtractedFormDataSource("Customer", "CustTable") }) },
            Classes = new[] { new ExtractedClass("ContosoCustTable_Extension", null, false, true, null, Array.Empty<ExtractedMethod>()) },
            CocExtensions = new[] { new ExtractedCoc("CustTable", "insert", "ContosoCustTable_Extension") },
            Extensions = new[] { new ExtractedObjectExtension("Table", "CustTable", "CustTable.ContosoExt", null) },
            EventSubscribers = new[] { new ExtractedEventSubscriber("ContosoCustHandler", "onInserted", "Table", "CustTable", null, "Inserted") },
        });
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
            if (File.Exists(_dbPath + ext)) File.Delete(_dbPath + ext);
    }

    private JsonElement Run(string name, string? kind = null, string? model = null, int limit = 200)
    {
        var result = StructuralWhereUsed.Run(_repo, name, kind, model, limit);
        Assert.True(result.Ok, result.Error?.Message);
        return JsonSerializer.SerializeToElement(result.Data);
    }

    private static List<string> Rows(JsonElement data) =>
        data.GetProperty("items").EnumerateArray()
            .Select(i => $"{i.GetProperty("kind").GetString()} {i.GetProperty("name").GetString()} "
                         + $"{(i.TryGetProperty("element", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : "-")} "
                         + i.GetProperty("via").GetString())
            .ToList();

    [Fact]
    public void A_table_is_found_in_data_sources_relations_edts_menu_items_extensions_coc_and_handlers()
    {
        var rows = Rows(Run("CustTable"));

        Assert.Contains("Form CustTable DataSources/CustTable Table", rows);
        Assert.Contains("Form ContosoCustOverview DataSources/Customer Table", rows);
        Assert.Contains("Query CustTableQuery DataSources/Cust Table", rows);
        Assert.Contains("Table SalesTable Relations/CustTable RelatedTable", rows);
        Assert.Contains("Edt CustAccount - ReferenceTable", rows);
        Assert.Contains("MenuItemDisplay CustTable - Object", rows);
        Assert.Contains("SecurityPrivilege CustTableView EntryPoints/MenuItemDisplay/CustTable EntryPoint (Read)", rows);
        Assert.Contains("TableExtension CustTable.ContosoExt - Extends", rows);
        Assert.Contains("Class ContosoCustTable_Extension Methods/insert ChainOfCommand", rows);
        Assert.Contains("Class ContosoCustHandler Methods/onInserted TableEventHandler (Inserted)", rows);
    }

    [Fact]
    public void An_edt_is_found_in_the_fields_that_use_it_and_a_privilege_in_the_duties_that_grant_it()
    {
        Assert.Equal(new[] { "Table CustTable Fields/AccountNum ExtendedDataType", "Table SalesTable Fields/CustAccount ExtendedDataType" },
            Rows(Run("custaccount")));
        Assert.Equal(new[] { "SecurityDuty CustTableInquire Privileges/CustTableView Privilege" }, Rows(Run("CustTableView")));
    }

    [Fact]
    public void Kind_and_model_filter_the_referencing_side()
    {
        Assert.Equal(new[] { "Form ContosoCustOverview DataSources/Customer Table", "Form CustTable DataSources/CustTable Table" },
            Rows(Run("CustTable", kind: "form")));
        Assert.All(Run("CustTable", model: "contosoext").GetProperty("items").EnumerateArray(),
            i => Assert.Equal("ContosoExt", i.GetProperty("model").GetString()));
        Assert.Equal(2, Run("CustTable", kind: "Security").GetProperty("count").GetInt32() + Run("CustTable", kind: "MenuItem").GetProperty("count").GetInt32());
    }

    [Fact]
    public void Counts_stay_complete_when_the_list_is_capped()
    {
        var data = Run("CustTable", limit: 2);

        Assert.Equal(2, data.GetProperty("items").GetArrayLength());
        Assert.True(data.GetProperty("truncated").GetBoolean());
        var total = data.GetProperty("count").GetInt32();
        Assert.Equal(total, data.GetProperty("byVia").EnumerateObject().Sum(p => p.Value.GetInt32()));
        Assert.Equal(total, data.GetProperty("byKind").EnumerateObject().Sum(p => p.Value.GetInt32()));
        Assert.Equal(1, data.GetProperty("byVia").GetProperty("EntryPoint").GetInt32());
    }

    [Fact]
    public void An_unreferenced_name_answers_empty_with_a_hint()
    {
        var data = Run("NoSuchObject");
        Assert.Equal(0, data.GetProperty("count").GetInt32());
        Assert.Contains("find refs", data.GetProperty("hint").GetString());
    }

    [Theory]
    [InlineData("Bogus", null)]
    [InlineData(null, "NoSuchModel")]
    public void An_unknown_kind_or_model_is_refused(string? kind, string? model)
    {
        Assert.False(StructuralWhereUsed.Run(_repo, "CustTable", kind, model, 200).Ok);
    }

    [Fact]
    public void Find_references_with_structural_answers_the_same_and_refuses_the_code_scan_options()
    {
        var handlers = new ToolHandlers(_repo);

        var viaTool = JsonSerializer.SerializeToElement(handlers.FindReferences("CustTable", null, null, structural: true).Data);
        Assert.Equal("structural", viaTool.GetProperty("mode").GetString());
        Assert.Equal(Run("CustTable").GetProperty("count").GetInt32(), viaTool.GetProperty("count").GetInt32());

        Assert.Equal(D365FoErrorCodes.BadInput, handlers.FindReferences("CustTable", null, null, xref: true, structural: true).Error?.Code);
        Assert.Equal(D365FoErrorCodes.BadInput, handlers.FindReferences("CustTable", null, null, lines: 3, structural: true).Error?.Code);
    }
}
