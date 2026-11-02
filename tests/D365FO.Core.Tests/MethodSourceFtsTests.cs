using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Covers the opt-in v15 method-body full-text index (MethodSourceFts):
/// capture is off by default, on when requested, kept by a re-extract that
/// names no preference, and dropped only when asked to (v20).
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
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            // Windows can keep the handle alive for a moment after the pool is released
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
    public void Reextract_without_source_never_leaves_stale_rows_behind()
    {
        Ingest(indexSource: true);
        Assert.True(_repo.CountMethodSource() > 0);
        Assert.True(_repo.IsSourceIndexed("TestModel"));

        // A write that carries no bodies replaces the model's rows with none, and says so —
        // the FTS rows never outlive the extract that produced them.
        Ingest(indexSource: false);
        Assert.Equal(0, _repo.CountMethodSource());
        Assert.False(_repo.IsSourceIndexed("TestModel"));
    }

    // ─── v20: the per-model source flag survives a re-extract ─────────────────

    private ToolResult<object> Sync(bool? indexSource) =>
        IndexSync.Sync("TestModel", path: null, packagesOverride: SamplesDir,
            databasePath: _dbPath, indexSource: indexSource);

    [Fact]
    public void Refresh_without_a_source_flag_keeps_the_models_method_bodies_searchable()
    {
        Assert.True(Sync(indexSource: true).Ok);
        var indexed = _repo.CountMethodSource("TestModel");
        Assert.True(indexed > 0);

        // The documented post-write step is `index refresh --model X` / `index sync X` with no
        // flag. It used to delete the model's FTS rows, so find refs lost every hit in it.
        Assert.True(Sync(indexSource: null).Ok);

        Assert.Equal(indexed, _repo.CountMethodSource("TestModel"));
        Assert.True(_repo.IsSourceIndexed("TestModel"));
        Assert.Equal("fts", MethodSourceSearch.Find(_repo, "strFmt", kind: null, model: null, limit: 50).Via);
    }

    [Fact]
    public void Sync_by_a_differently_cased_model_name_keeps_the_indexed_model()
    {
        Assert.True(Sync(indexSource: true).Ok);
        var indexed = _repo.CountMethodSource("TestModel");

        Assert.True(IndexSync.Sync("testmodel", path: null, packagesOverride: SamplesDir,
            databasePath: _dbPath, indexSource: null).Ok);

        Assert.Equal(indexed, _repo.CountMethodSource("TestModel"));
        Assert.True(_repo.IsSourceIndexed("TestModel"));
        // One model row, under the on-disk spelling — not a second, unindexed "testmodel".
        Assert.Equal("TestModel", Assert.Single(_repo.ListModels()).Name);
    }

    [Fact]
    public void Refresh_without_a_source_flag_does_not_index_a_model_that_never_was()
    {
        Assert.True(Sync(indexSource: null).Ok);

        Assert.Equal(0, _repo.CountMethodSource());
        Assert.False(_repo.IsSourceIndexed("TestModel"));
    }

    [Fact]
    public void No_index_source_drops_the_models_method_bodies_and_the_choice_sticks()
    {
        Assert.True(Sync(indexSource: true).Ok);
        Assert.True(_repo.CountMethodSource("TestModel") > 0);

        Assert.True(Sync(indexSource: false).Ok);
        Assert.Equal(0, _repo.CountMethodSource("TestModel"));
        Assert.False(_repo.IsSourceIndexed("TestModel"));

        // A later refresh with no flag keeps the model out, as it was left.
        Assert.True(Sync(indexSource: null).Ok);
        Assert.Equal(0, _repo.CountMethodSource("TestModel"));
    }

    [Fact]
    public void Index_source_and_no_index_source_together_are_refused()
    {
        var refusal = IndexSync.ConflictingSourceFlags(indexSource: true, noIndexSource: true);

        Assert.NotNull(refusal);
        Assert.False(refusal!.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, refusal.Error!.Code);
        Assert.Contains("neither", refusal.Error.Hint, StringComparison.Ordinal);
        Assert.Null(IndexSync.ConflictingSourceFlags(indexSource: true, noIndexSource: false));
        Assert.Null(IndexSync.SourceChoice(indexSource: false, noIndexSource: false));
    }

    [Fact]
    public void Upgrading_a_v19_index_keeps_models_that_already_had_source_indexed()
    {
        Ingest(indexSource: true);
        var indexed = _repo.CountMethodSource("TestModel");

        // Turn the database back into what a v19 build left behind: FTS rows, no column.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_repo.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "ALTER TABLE Models DROP COLUMN SourceIndexed; PRAGMA user_version = 19;";
            cmd.ExecuteNonQuery();
        }

        var upgraded = new MetadataRepository(_dbPath);
        Assert.True(upgraded.EnsureSchema());

        // Read off the FTS rows by the migration, so the first refresh after the upgrade keeps them.
        Assert.True(upgraded.IsSourceIndexed("TestModel"));
        Assert.True(Sync(indexSource: null).Ok);
        Assert.Equal(indexed, upgraded.CountMethodSource("TestModel"));
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

    // ─── Form methods (form, data source, data source field, control) ────────

    private static string[] Members(IEnumerable<MethodSourceMatch> rows) =>
        rows.Select(r => r.MethodName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public void Form_method_bodies_are_indexed_under_the_member_that_owns_them()
    {
        Ingest(indexSource: true);

        // init exists on the form AND on its data source; the bare name would not say which.
        Assert.Equal(new[] { "RunServiceButton.clicked", "classDeclaration", "init" },
            Members(_repo.SearchMethodSource("\"FmVehicleService\"", kind: "Form")));
        Assert.Equal(new[] { "FmVehicle.VIN.modified", "vehicleChanged" },
            Members(_repo.SearchMethodSource("\"vehicleChanged\"", kind: "Form")));
        var dsInit = Assert.Single(_repo.SearchMethodSource("\"addSortField\"", kind: "Form"));
        Assert.Equal("FmVehicle.init", dsInit.MethodName);
        Assert.Equal("FmVehicleList", dsInit.ObjectName);
        Assert.Equal(SourceIndexLevel.WithForms, _repo.GetSourceIndexLevels()["TestModel"]);
    }

    [Fact]
    public void Find_answers_form_methods_from_fts_without_scanning_form_files()
    {
        Ingest(indexSource: true);

        var result = MethodSourceSearch.Find(_repo, "addSortField", kind: null, model: null, limit: 50);

        Assert.Equal("fts", result.Via);
        // Every form of the model is in the index, so no form file is opened to search it.
        Assert.Equal(0, result.FilesScanned);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("Form", hit.Kind);
        Assert.Equal("FmVehicleList", hit.Name);
        Assert.Equal("FmVehicle.init", hit.Method);
        // Sample lines come from the data source's init, not the form's init of the same name.
        Assert.Contains("addSortField", Assert.Single(hit.Matches).Text);
    }

    [Fact]
    public void Disk_scan_names_form_members_the_same_way_as_the_index()
    {
        Ingest(indexSource: true);
        var fromIndex = MethodSourceSearch.Find(_repo, "vehicleChanged", kind: "Form", model: null, limit: 50);
        Ingest(indexSource: false);
        var fromDisk = MethodSourceSearch.Find(_repo, "vehicleChanged", kind: "Form", model: null, limit: 50);

        Assert.Equal("fts", fromIndex.Via);
        Assert.Equal("scan", fromDisk.Via);
        Assert.Equal(
            fromIndex.Hits.Select(h => h.Method).OrderBy(m => m, StringComparer.Ordinal),
            fromDisk.Hits.Select(h => h.Method).OrderBy(m => m, StringComparer.Ordinal));
        Assert.All(fromDisk.Hits, h => Assert.NotEmpty(h.Matches));
    }

    [Fact]
    public void Forms_of_a_model_indexed_before_form_bodies_existed_are_still_scanned_from_disk()
    {
        Ingest(indexSource: true);
        // What an index built before form bodies joined MethodSourceFts holds: class and table
        // rows only, at level 1.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(_repo.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM MethodSourceFts WHERE Kind = 'Form'; UPDATE Models SET SourceIndexed = 1;";
            cmd.ExecuteNonQuery();
        }

        var result = MethodSourceSearch.Find(_repo, "addSortField", kind: null, model: null, limit: 50);

        Assert.Equal(1, result.FilesScanned);
        Assert.Equal("FmVehicle.init", Assert.Single(result.Hits).Method);
    }

    [Fact]
    public void A_form_members_own_method_name_is_what_implementations_compare()
    {
        var hit = new SourceRefHit("Form", "FmVehicleList", "TestModel", "FmVehicle.VIN.modified", null,
            Array.Empty<SourceRefMatchLine>());
        Assert.Equal("modified", MethodSourceSearch.MethodNameOf(hit));
        Assert.Equal("Cust.init", MethodSourceSearch.MethodNameOf(hit with { Kind = "Class", Method = "Cust.init" }));
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
