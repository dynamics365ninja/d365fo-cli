using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Covers the opt-in v15 method-body full-text index (MethodSourceFts):
/// capture is off by default, on when requested, and cleaned up when a model
/// is re-extracted without the flag.
/// </summary>
public class MethodSourceFtsTests : IDisposable
{
    private static readonly string SamplesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), $"d365fo-mfts-{Guid.NewGuid():N}.sqlite");

    private readonly MetadataRepository _repo;

    public MethodSourceFtsTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            // Windows can keep the handle alive for a moment after ClearAllPools
            // (observed on CI: IOException "being used by another process").
            // Temp-file cleanup is best-effort — retry briefly, then let the OS
            // temp sweeper have it rather than failing the test run.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(p)) File.Delete(p);
                    break;
                }
                catch (IOException) when (attempt < 5)
                {
                    Thread.Sleep(100);
                }
                catch (IOException)
                {
                    break;
                }
            }
        }
    }

    private void Ingest(bool indexSource)
    {
        var ex = new MetadataExtractor { CaptureMethodSource = indexSource };
        foreach (var batch in ex.ExtractAll(SamplesDir))
            _repo.ApplyExtract(batch, sourceFingerprint: null, indexMethodSource: indexSource);
    }

    [Fact]
    public void Default_extract_does_not_populate_method_source_fts()
    {
        Ingest(indexSource: false);
        Assert.Equal(0, _repo.CountMethodSource());
        Assert.Empty(_repo.SearchMethodSource("strFmt"));
    }

    [Fact]
    public void IndexSource_extract_makes_method_bodies_searchable()
    {
        Ingest(indexSource: true);

        Assert.True(_repo.CountMethodSource() > 0);

        // 'strFmt' appears only inside FmVehicleService.run's body.
        var hits = _repo.SearchMethodSource("strFmt");
        var hit = Assert.Single(hits);
        Assert.Equal("Class", hit.Kind);
        Assert.Equal("FmVehicleService", hit.ObjectName);
        Assert.Equal("run", hit.MethodName);
        Assert.Equal("TestModel", hit.Model);
        Assert.NotNull(hit.SourcePath);
    }

    [Fact]
    public void Reextract_without_flag_clears_stale_source_rows()
    {
        Ingest(indexSource: true);
        Assert.True(_repo.CountMethodSource() > 0);

        // Re-extracting the same model without --index-source must drop its FTS rows.
        Ingest(indexSource: false);
        Assert.Equal(0, _repo.CountMethodSource());
    }

    // ─── Shared search helper (backs CLI find refs + MCP find_references) ────

    [Fact]
    public void Find_uses_fts_and_returns_line_numbers_when_indexed()
    {
        Ingest(indexSource: true);

        var result = MethodSourceSearch.Find(_repo, "strFmt", kind: null, model: null, limit: 50);

        Assert.Equal("fts", result.Via);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("FmVehicleService", hit.Name);
        Assert.Equal("run", hit.Method);
        // 'strFmt' is on one line of run()'s body — the helper resolves the actual
        // line number by reading the source file the FTS row points at.
        var line = Assert.Single(hit.Matches);
        Assert.Contains("strFmt", line.Text);
    }

    [Fact]
    public void Find_falls_back_to_disk_scan_when_not_indexed()
    {
        Ingest(indexSource: false);

        var result = MethodSourceSearch.Find(_repo, "strFmt", kind: null, model: null, limit: 50);

        Assert.Equal("scan", result.Via);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("FmVehicleService", hit.Name);
        Assert.Equal("run", hit.Method);
    }

    [Fact]
    public void Find_honours_kind_filter_on_fts_path()
    {
        Ingest(indexSource: true);

        // strFmt lives in a class method; filtering to tables yields nothing.
        var asTable = MethodSourceSearch.Find(_repo, "strFmt", kind: "Table", model: null, limit: 50);
        Assert.Empty(asTable.Hits);

        var asClass = MethodSourceSearch.Find(_repo, "strFmt", kind: "Class", model: null, limit: 50);
        Assert.Single(asClass.Hits);
    }
}
