using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

public class IndexStalenessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"stale-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public IndexStalenessTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "index.sqlite");
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void ExtractModel(string name)
    {
        _repo.ApplyExtract(new ExtractBatch(
            Model: name, Publisher: "Me", Layer: "usr", IsCustom: true,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: Array.Empty<ExtractedClass>(),
            Edts: Array.Empty<ExtractedEdt>(),
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>()));
    }

    [Fact]
    public void Never_extracted_index_is_stale()
    {
        var result = IndexStaleness.Check(_repo, new[] { _root });
        Assert.True(result.IsStale);
        Assert.Contains("never been extracted", result.Detail);
    }

    [Fact]
    public void Fresh_index_is_not_stale()
    {
        // XML written BEFORE the extract → not stale (within tolerance).
        var modelDir = Path.Combine(_root, "MyModel", "MyModel", "AxTable");
        Directory.CreateDirectory(modelDir);
        var xml = Path.Combine(modelDir, "MyTable.xml");
        File.WriteAllText(xml, "<AxTable/>");
        File.SetLastWriteTimeUtc(xml, DateTime.UtcNow.AddMinutes(-10));

        ExtractModel("MyModel");
        var result = IndexStaleness.Check(_repo, new[] { _root });
        Assert.False(result.IsStale);
    }

    [Fact]
    public void Touched_file_after_extract_makes_index_stale()
    {
        var modelDir = Path.Combine(_root, "MyModel", "MyModel", "AxTable");
        Directory.CreateDirectory(modelDir);
        var xml = Path.Combine(modelDir, "MyTable.xml");
        File.WriteAllText(xml, "<AxTable/>");

        ExtractModel("MyModel");

        // Simulate an edit AFTER the extract, beyond the 60 s tolerance.
        File.SetLastWriteTimeUtc(xml, DateTime.UtcNow.AddMinutes(5));

        var result = IndexStaleness.Check(_repo, new[] { _root });
        Assert.True(result.IsStale);
        Assert.Contains("index refresh", result.Detail);
        Assert.Equal(xml, result.NewestFile);
    }

    [Fact]
    public void Scan_returns_null_for_missing_directory()
    {
        Assert.Null(IndexStaleness.FindNewestMetadataMtime(Path.Combine(_root, "does-not-exist")));
    }
}
