using D365FO.Core.Extract;
using D365FO.Core.Index;
using System.Text.Json;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// End-to-end pipeline tests driven by a checked-in "mini AOT" fixture.
/// Covers: XML parse → SQLite ingest → repository queries.
/// </summary>
public class MiniAotEndToEndTests : IDisposable
{
    private static readonly string SamplesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"d365fo-miniaot-{Guid.NewGuid():N}.sqlite");

    private readonly MetadataRepository _repo;

    public MiniAotEndToEndTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();

        var ex = new MetadataExtractor();
        foreach (var batch in ex.ExtractAll(SamplesDir))
            _repo.ApplyExtract(batch);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ─── Fixture sanity ────────────────────────────────────────────────────

    [Fact]
    public void Samples_fixture_exists()
    {
        Assert.True(Directory.Exists(SamplesDir),
            $"Fixture directory missing: {SamplesDir}");
    }

    // ─── Extract: model-level assertions ───────────────────────────────────

    [Fact]
    public void ExtractAll_finds_one_model_with_correct_publisher()
    {
        var ex = new MetadataExtractor();
        var batches = ex.ExtractAll(SamplesDir).ToList();

        var batch = Assert.Single(batches);
        Assert.Equal("TestModel", batch.Model);
        Assert.Equal("Contoso", batch.Publisher);
        Assert.Equal("usr", batch.Layer);
    }

    [Fact]
    public void ExtractAll_has_ApplicationSuite_dependency()
    {
        var ex = new MetadataExtractor();
        var batch = ex.ExtractAll(SamplesDir).Single();

        Assert.Contains("ApplicationSuite", batch.Dependencies);
    }

    // ─── Extract: table assertions ─────────────────────────────────────────

    [Fact]
    public void ExtractAll_parses_FmVehicle_fields()
    {
        var ex = new MetadataExtractor();
        var batch = ex.ExtractAll(SamplesDir).Single();

        var table = batch.Tables.Single(t => t.Name == "FmVehicle");
        Assert.Equal(3, table.Fields.Count);

        var vin = table.Fields.Single(f => f.Name == "VIN");
        Assert.True(vin.Mandatory);
        Assert.Equal("VinEdt", vin.EdtName);

        Assert.Contains(table.Fields, f => f.Name == "Make");
        Assert.Contains(table.Fields, f => f.Name == "Year");
    }

    // ─── Extract: class assertions ─────────────────────────────────────────

    [Fact]
    public void ExtractAll_parses_FmVehicleService_methods()
    {
        var ex = new MetadataExtractor();
        var batch = ex.ExtractAll(SamplesDir).Single();

        var cls = Assert.Single(batch.Classes);
        Assert.Equal("FmVehicleService", cls.Name);
        Assert.Equal(4, cls.Methods.Count);

        Assert.Contains(cls.Methods, m => m.Name == "new");
        Assert.Contains(cls.Methods, m => m.Name == "run");
        Assert.Contains(cls.Methods, m => m.Name == "construct");

        // The delegate L2-event-handler-basic subscribes to. delegateStr() is
        // compile-time checked, so without it that case's golden cannot compile
        // however correct the scaffolder is.
        Assert.Contains(cls.Methods, m => m.Name == "OnInitialized");
    }

    // ─── Repository: count assertions ──────────────────────────────────────

    [Fact]
    public void Repository_counts_match_fixture()
    {
        var counts = _repo.CountAll();
        // FmVehicle (3 fields) + FmVehicleLine (3 fields). The second table exists
        // so eval cases can exercise header/lines and join-shaped generators
        // (query --join, form --pattern DetailsTransaction) against a real
        // relation instead of a phantom target.
        Assert.Equal(2, counts.Tables);
        Assert.Equal(6, counts.Fields);
        Assert.Equal(1, counts.Classes);
    }

    /// <summary>
    /// The fixture grew past "one table and one class" (plan item 4.5) so that
    /// reference resolution in evals has something real to resolve against: before
    /// this, <c>FmVehicle.VIN</c> named an EDT the index did not contain, so every
    /// generated artifact touching it scored a reference violation that said
    /// nothing about the artifact. All three files were produced by the CLI's own
    /// generators, not hand-written — the same rule the corpus gives agents — and
    /// the whole fixture module compiles clean under <c>eval verify-build</c>, which
    /// is what makes it usable as the L3 oracle's reference material.
    /// </summary>
    [Fact]
    public void ExtractAll_indexes_the_edt_enum_and_query_the_fixture_tables_reference()
    {
        var ex = new MetadataExtractor();
        var batch = ex.ExtractAll(SamplesDir).Single();

        var edt = Assert.Single(batch.Edts);
        Assert.Equal("VinEdt", edt.Name);
        Assert.Equal(
            edt.Name,
            batch.Tables.Single(t => t.Name == "FmVehicle").Fields.Single(f => f.Name == "VIN").EdtName);

        var @enum = Assert.Single(batch.Enums);
        Assert.Equal("FmVehicleStatus", @enum.Name);

        var query = Assert.Single(batch.Queries);
        Assert.Equal("FmVehicleQuery", query.Name);
    }

    [Fact]
    public void ExtractAll_parses_FmVehicleLine_relation_to_FmVehicle()
    {
        var ex = new MetadataExtractor();
        var batch = ex.ExtractAll(SamplesDir).Single();

        var line = batch.Tables.Single(t => t.Name == "FmVehicleLine");
        var rel = Assert.Single(line.Relations);
        Assert.Equal("FmVehicle", rel.RelatedTable);
    }

    // ─── Repository: GetTableDetails snapshot ──────────────────────────────

    [Fact]
    public void GetTableDetails_FmVehicle_returns_correct_shape()
    {
        var details = _repo.GetTableDetails("FmVehicle");
        Assert.NotNull(details);
        Assert.Equal("FmVehicle", details!.Table.Name);
        Assert.Equal("TestModel", details.Table.Model);
        Assert.Equal("@Fleet:Vehicle", details.Table.Label);
        Assert.Equal(3, details.Fields.Count);

        var vin = details.Fields.Single(f => f.Name == "VIN");
        Assert.True(vin.Mandatory);
        Assert.Equal("VinEdt", vin.EdtName);
    }

    [Fact]
    public void GetTableDetails_FmVehicle_has_alternateKey_index()
    {
        var indexes = _repo.GetTableIndexes("FmVehicle");
        Assert.NotEmpty(indexes);
        var vinIdx = indexes.Single(i => i.Name == "VINIdx");
        Assert.True(vinIdx.AlternateKey);
    }

    // ─── Repository: GetClassDetails snapshot ──────────────────────────────

    [Fact]
    public void GetClassDetails_FmVehicleService_returns_correct_shape()
    {
        var details = _repo.GetClassDetails("FmVehicleService");
        Assert.NotNull(details);
        Assert.Equal("FmVehicleService", details!.Class.Name);
        Assert.Equal("TestModel", details.Class.Model);
        Assert.Equal(4, details.Methods.Count);

        Assert.Contains(details.Methods, m => m.Name == "construct");
    }

    // ─── Repository: serialization round-trip ──────────────────────────────

    [Fact]
    public void GetTableDetails_serializes_to_valid_json()
    {
        var details = _repo.GetTableDetails("FmVehicle");
        Assert.NotNull(details);
        var json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = false });
        Assert.StartsWith("{", json);
        Assert.Contains("FmVehicle", json);
        Assert.Contains("VIN", json);
    }

    [Fact]
    public void GetClassDetails_serializes_to_valid_json()
    {
        var details = _repo.GetClassDetails("FmVehicleService");
        Assert.NotNull(details);
        var json = JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = false });
        Assert.StartsWith("{", json);
        Assert.Contains("FmVehicleService", json);
        Assert.Contains("run", json);
    }

    // ─── Repository: idempotency ────────────────────────────────────────────

    [Fact]
    public void ApplyExtract_twice_is_idempotent()
    {
        var ex = new MetadataExtractor();
        var batches = ex.ExtractAll(SamplesDir).ToList();

        foreach (var b in batches)
            _repo.ApplyExtract(b);

        var counts = _repo.CountAll();
        Assert.Equal(2, counts.Tables);
        Assert.Equal(6, counts.Fields);
        Assert.Equal(1, counts.Classes);
    }
}
