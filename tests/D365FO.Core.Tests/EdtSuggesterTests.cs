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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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

    [Fact]
    public void Exact_name_gets_top_confidence()
    {
        var suggestions = EdtSuggester.Suggest(_repo, "CustAccount");
        Assert.NotEmpty(suggestions);
        Assert.Equal("CustAccount", suggestions[0].Edt.Name);
        Assert.Equal(1.0, suggestions[0].Confidence);
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
