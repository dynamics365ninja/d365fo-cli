using System.Text.Json;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Two 1.16.0 upstream fixes the parity audit found unported (c43bf51, 0b363e5): a multi-word
/// name query answered nothing, and an extension's name was invisible to the collision check.
/// </summary>
public class NameSearchAndExtensionSymbolTests : IDisposable
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
        repo.ApplyExtract(ExtractBatch.Empty("ProcessGuide") with
        {
            Classes = new[]
            {
                new ExtractedClass("InventProcessGuideAdjustInController", "ProcessGuideController", false, false, "/a", Array.Empty<ExtractedMethod>()),
                new ExtractedClass("InventProcessGuideAdjustOutController", "ProcessGuideController", false, false, "/b", Array.Empty<ExtractedMethod>()),
                new ExtractedClass("WhsAdjustInHelper", null, false, false, "/c", Array.Empty<ExtractedMethod>()),
            },
            Tables = new[]
            {
                new ExtractedTable("CustTrans", null, "/t1", Array.Empty<ExtractedTableField>()),
                new ExtractedTable("CustTable", null, "/t2", Array.Empty<ExtractedTableField>()),
            },
            Enums = new[] { new ExtractedEnum("NumberSeqModule", null, Array.Empty<ExtractedEnumValue>()) },
            Extensions = new[]
            {
                new ExtractedObjectExtension("Enum", "NumberSeqModule", "NumberSeqModule.Kitting", "/e1"),
                new ExtractedObjectExtension("Enum", "NumberSeqModule", "NumberSeqModule.CredMan", "/e2"),
            },
            DataEntities = new[]
            {
                new ExtractedDataEntity("CustCustomerV3Entity", "CustomerV3", "CustomersV3", null, null, null, "/d1", Array.Empty<ExtractedDataEntityField>()),
                new ExtractedDataEntity("VendVendorV2Entity", "CustomerLikeVendor", "Vendors", null, null, null, "/d2", Array.Empty<ExtractedDataEntityField>()),
            },
        });
        return repo;
    }

    [Fact]
    public void Multi_word_query_finds_the_name_carrying_every_token_in_any_order()
    {
        var repo = Seed();

        // The exact-name search always found it; the two-word query returned nothing, and an
        // agent read that empty answer as evidence. Confirmed on a live index before the fix.
        Assert.Single(repo.SearchClasses("InventProcessGuideAdjustInController"));
        Assert.Equal("InventProcessGuideAdjustInController", Assert.Single(repo.SearchClasses("ProcessGuide AdjustIn")).Name);
        Assert.Equal("InventProcessGuideAdjustInController", Assert.Single(repo.SearchClasses("AdjustIn ProcessGuide")).Name);

        // Every token, not any token: a name carrying one of the two words does not qualify.
        Assert.DoesNotContain(repo.SearchClasses("ProcessGuide AdjustIn"), c => c.Name == "WhsAdjustInHelper");

        // Whitespace around a single word is not part of the name being looked for.
        Assert.Equal(2, repo.SearchClasses("  AdjustIn ").Count);

        Assert.Equal("CustTrans", Assert.Single(repo.SearchTables("Cust Trans")).Name);
    }

    [Fact]
    public void Multi_word_query_over_several_columns_needs_every_token_in_one_column()
    {
        var repo = Seed();

        // Name and public name are alternatives for the whole query, not a pool the tokens may
        // be spread across: "Customer Vendor" must not match a row whose name holds one word
        // and whose public entity name holds the other.
        Assert.Equal("CustCustomerV3Entity", Assert.Single(repo.SearchDataEntities("Customer V3")).Name);
        Assert.Contains(repo.SearchDataEntities("CustomerLike Vendor"), e => e.Name == "VendVendorV2Entity");
        Assert.Empty(repo.SearchDataEntities("VendVendor CustomerLike Cust"));
    }

    [Fact]
    public void SymbolKinds_reports_an_extension_under_its_own_name()
    {
        var repo = Seed();

        Assert.Equal(new[] { "enum" }, repo.SymbolKinds("NumberSeqModule"));
        Assert.Equal(new[] { "enum-extension" }, repo.SymbolKinds("numberseqmodule.kitting"));
        Assert.Empty(repo.SymbolKinds("NumberSeqModule.ConDemoRent"));
    }

    [Fact]
    public void Prepare_create_calls_a_taken_extension_name_a_collision_and_its_dot_legal()
    {
        var repo = Seed();
        var handlers = new ToolHandlers(repo);

        // Taken: the installation ships NumberSeqModule.Kitting. This used to answer
        // "No collision — does not exist in the index", right before a write.
        var taken = JsonSerializer.Serialize(handlers.PrepareCreate("NumberSeqModule.Kitting", "enum", null, null, null).Data);
        using (var doc = JsonDocument.Parse(taken))
        {
            var root = doc.RootElement;
            var collision = Assert.Single(root.GetProperty("collisions").EnumerateArray());
            Assert.Equal("NumberSeqModule.Kitting", collision.GetProperty("name").GetString());
            Assert.Equal("enum-extension", collision.GetProperty("existsAs")[0].GetString());
            Assert.Contains("already exists", root.GetProperty("collisionVerdict").GetString());
            Assert.DoesNotContain(root.GetProperty("namingViolations").EnumerateArray(),
                v => v.GetProperty("code").GetString() == "INVALID_CHARS");
        }

        // Free: a new suffix on the same base is no collision, and the dot every shipped
        // extension carries is not an invalid character — the same answer lists the
        // siblings spelled exactly that way.
        var free = JsonSerializer.Serialize(handlers.PrepareCreate("NumberSeqModule.ConDemoRent", "enum", null, null, null).Data);
        using (var doc = JsonDocument.Parse(free))
        {
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Null, root.GetProperty("collisions").ValueKind);
            Assert.StartsWith("No collision", root.GetProperty("collisionVerdict").GetString());
            Assert.DoesNotContain(root.GetProperty("namingViolations").EnumerateArray(),
                v => v.GetProperty("code").GetString() == "INVALID_CHARS");
            Assert.Equal(2, root.GetProperty("extensionBase").GetProperty("existingExtensions").GetInt32());
        }
    }
}
