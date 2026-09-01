using D365FO.Core;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

public class EdtSuggesterTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"edt-sugg-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public EdtSuggesterTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        Seed();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    private void Seed()
    {
        var batch = new ExtractBatch(
            Model: "ApplicationSuite",
            Publisher: "Microsoft",
            Layer: "app",
            IsCustom: false,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: Array.Empty<ExtractedClass>(),
            Edts: new[]
            {
                new ExtractedEdt("CustAccount", null, "String", null, 20),
                new ExtractedEdt("CustomerAccount", "CustAccount", "String", null, 20),
                new ExtractedEdt("AccountNum", null, "String", null, 10),
                new ExtractedEdt("OrderAmount", null, "Real", null, null),
                new ExtractedEdt("TransDate", null, "Date", null, null),
            },
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>());
        _repo.ApplyExtract(batch);
    }

    /// <summary>
    /// Fills the fuzzy-search window with 101 custom-model EDTs whose names all
    /// contain <paramref name="root"/>, so that a standard EDT sharing that root
    /// falls outside <c>SearchEdts</c>'s limit. Opt-in per test: seeding this
    /// into the shared fixture would silently change what every other test
    /// exercises.
    /// </summary>
    private void SeedCrowdedFuzzyWindow(string root) =>
        _repo.ApplyExtract(new ExtractBatch(
            Model: $"{root}CrowdingModel",
            Publisher: "Test",
            Layer: "cus",
            IsCustom: true,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: Array.Empty<ExtractedClass>(),
            Edts: Enumerable.Range(0, 101)
                .Select(i => new ExtractedEdt($"{root}Filler{i:D3}", null, "String", null, 20))
                .ToArray(),
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>()));

    private void SeedEdt(string model, bool isCustom, string edtName, string? extends = null) =>
        _repo.ApplyExtract(new ExtractBatch(
            Model: model,
            Publisher: isCustom ? "Test" : "Microsoft",
            Layer: isCustom ? "cus" : "sys",
            IsCustom: isCustom,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: Array.Empty<ExtractedClass>(),
            Edts: new[] { new ExtractedEdt(edtName, extends, "String", null, 20) },
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>()));

    [Fact]
    public void Exact_name_gets_top_confidence()
    {
        var suggestions = EdtSuggester.Suggest(_repo, "CustAccount");
        Assert.NotEmpty(suggestions);
        Assert.Equal("CustAccount", suggestions[0].Edt.Name);
        Assert.Equal(1.0, suggestions[0].Confidence);
    }

    [Fact]
    public void Exact_name_is_not_lost_when_fuzzy_candidates_are_truncated()
    {
        SeedCrowdedFuzzyWindow("Item");
        SeedEdt("Foundation", isCustom: false, "ItemId", extends: "ItemIdBase");

        Assert.DoesNotContain(_repo.SearchEdts("Item", 100),
            edt => string.Equals(edt.Name, "ItemId", StringComparison.OrdinalIgnoreCase));

        var suggestions = EdtSuggester.Suggest(_repo, "itemid", limit: 5);

        Assert.NotEmpty(suggestions);
        Assert.Equal("ItemId", suggestions[0].Edt.Name);
        Assert.Equal(1.0, suggestions[0].Confidence);
        Assert.Equal("exact match", suggestions[0].Reason);
    }

    [Fact]
    public void Whole_name_match_is_recovered_when_the_root_sweep_is_truncated()
    {
        // "LineNum" strips to the root "Line", and the root sweep is the broader
        // of the two — so the only way a name carrying the whole field name gets
        // lost is the budget cutting it off behind a crowd of custom names.
        SeedCrowdedFuzzyWindow("Line");
        SeedEdt("Foundation", isCustom: false, "InventLineNum");

        Assert.DoesNotContain(_repo.SearchEdts("Line", 100),
            edt => string.Equals(edt.Name, "InventLineNum", StringComparison.OrdinalIgnoreCase));

        var suggestions = EdtSuggester.Suggest(_repo, "LineNum", limit: 5);

        Assert.NotEmpty(suggestions);
        Assert.Equal("InventLineNum", suggestions[0].Edt.Name);
        Assert.Equal("whole-name token match", suggestions[0].Reason);
    }

    [Fact]
    public void Exact_candidates_preserve_model_identity()
    {
        // Crowd the window too, otherwise the fuzzy sweep returns both rows on
        // its own and the test passes without exercising the exact-name lookup.
        SeedCrowdedFuzzyWindow("SharedReference");
        SeedEdt("SyntheticBaseModel", isCustom: false, "SharedReferenceId");
        SeedEdt("SyntheticExtensionModel", isCustom: true, "SharedReferenceId");

        Assert.DoesNotContain(_repo.SearchEdts("SharedReference", 100),
            edt => string.Equals(edt.Name, "SharedReferenceId", StringComparison.OrdinalIgnoreCase));

        var exactModels = EdtSuggester.Suggest(_repo, "sharedreferenceid", limit: 5)
            .Where(suggestion => suggestion.Confidence == 1.0)
            .Select(suggestion => suggestion.Edt.Model)
            .ToList();

        // Order between same-name ties is not a documented contract, so assert
        // the set: both qualified candidates survive for downstream disambiguation.
        Assert.Equal(2, exactModels.Count);
        Assert.Contains("SyntheticBaseModel", exactModels);
        Assert.Contains("SyntheticExtensionModel", exactModels);
    }

    [Fact]
    public void Stripped_suffix_matches_root()
    {
        var suggestions = EdtSuggester.Suggest(_repo, "CustAccountId");
        Assert.Contains(suggestions, s => s.Edt.Name == "CustAccount" && s.Confidence >= 0.80);
    }

    [Fact]
    public void Returns_empty_for_unknown()
    {
        var suggestions = EdtSuggester.Suggest(_repo, "TotallyUnrelatedThing");
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Returns_empty_for_blank()
    {
        var suggestions = EdtSuggester.Suggest(_repo, "");
        Assert.Empty(suggestions);
    }

    [Fact]
    public void Ranks_by_confidence_desc()
    {
        var suggestions = EdtSuggester.Suggest(_repo, "Account", limit: 5);
        for (int i = 1; i < suggestions.Count; i++)
            Assert.True(suggestions[i - 1].Confidence >= suggestions[i].Confidence);
    }
}

public class EnsureSchemaReturnsAppliedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"schema-applied-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    [Fact]
    public void First_call_applies_schema_second_is_noop()
    {
        var repo = new MetadataRepository(_dbPath);
        Assert.True(repo.EnsureSchema(), "first invocation should apply schema");
        Assert.False(repo.EnsureSchema(), "second invocation should be a no-op");
    }
}
