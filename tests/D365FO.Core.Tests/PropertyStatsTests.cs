using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

public class PropertyStatsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"propstats-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public PropertyStatsTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
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

    private static ExtractBatch Batch(string model, bool isCustom, params ExtractedTable[] tables) => new(
        Model: model,
        Publisher: isCustom ? "Me" : "Microsoft",
        Layer: isCustom ? "usr" : "app",
        IsCustom: isCustom,
        Tables: tables,
        Classes: Array.Empty<ExtractedClass>(),
        Edts: Array.Empty<ExtractedEdt>(),
        Enums: Array.Empty<ExtractedEnum>(),
        MenuItems: Array.Empty<ExtractedMenuItem>(),
        CocExtensions: Array.Empty<ExtractedCoc>(),
        Labels: Array.Empty<ExtractedLabel>());

    private static ExtractedTable Table(string name, string? label, string? tableGroup, string? clusteredIndex, bool altKey) =>
        new(name, label, "x", new[] { new ExtractedTableField("F1", "ExtendedDataType", "Name", null, true) })
        {
            TableGroup = tableGroup,
            ClusteredIndex = clusteredIndex,
            Indexes = altKey
                ? new[] { new ExtractedTableIndex("PrimaryIdx", false, true, new[] { "F1" }) }
                : Array.Empty<ExtractedTableIndex>(),
        };

    [Fact]
    public void Standard_models_are_mined()
    {
        _repo.ApplyExtract(Batch("ApplicationSuite", isCustom: false,
            Table("T1", "@SYS1", "Main", "PrimaryIdx", altKey: true),
            Table("T2", "@SYS2", "Main", null, altKey: true),
            Table("T3", null, "Transaction", null, altKey: false),
            Table("T4", "@SYS4", null, null, altKey: true)));

        Assert.True(_repo.HasPropertyStats());

        var label = _repo.GetPropertyPresenceRatio("AxTable", "Label");
        Assert.Equal(4, label.Total);
        Assert.Equal(3, label.Present);

        var tg = _repo.GetPropertyPresenceRatio("AxTable", "TableGroup");
        Assert.Equal(4, tg.Total);
        Assert.Equal(3, tg.Present); // values count as present; '(absent)' does not

        var dist = _repo.GetPropertyValueDistribution("AxTable", "TableGroup");
        Assert.Equal("Main", dist[0].Value);
        Assert.Equal(2, dist[0].Count);

        var field = _repo.GetPropertyPresenceRatio("AxTableField", "ExtendedDataType");
        Assert.Equal(4, field.Total);
        Assert.Equal(4, field.Present);
    }

    [Fact]
    public void Custom_models_are_not_mined()
    {
        _repo.ApplyExtract(Batch("MyModel", isCustom: true,
            Table("C1", null, null, null, altKey: false)));
        Assert.False(_repo.HasPropertyStats());
    }

    [Fact]
    public void Reextract_replaces_model_stats_instead_of_double_counting()
    {
        var batch = Batch("ApplicationSuite", isCustom: false, Table("T1", "@SYS1", "Main", null, altKey: true));
        _repo.ApplyExtract(batch);
        _repo.ApplyExtract(batch);
        var label = _repo.GetPropertyPresenceRatio("AxTable", "Label");
        Assert.Equal(1, label.Total);
    }

    [Fact]
    public void Model_flipping_to_custom_clears_its_stats()
    {
        _repo.ApplyExtract(Batch("FlipModel", isCustom: false, Table("T1", "@SYS1", "Main", null, altKey: true)));
        Assert.True(_repo.HasPropertyStats());
        _repo.ApplyExtract(Batch("FlipModel", isCustom: true, Table("T1", "@SYS1", "Main", null, altKey: true)));
        Assert.False(_repo.HasPropertyStats());
    }
}
