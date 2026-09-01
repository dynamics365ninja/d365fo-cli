using D365FO.Core.Index;
using Microsoft.Data.Sqlite;
using System.Linq;
using Xunit;

namespace D365FO.Core.Tests;

public class MetadataRepositoryTests : IDisposable
{
    private readonly string _dbPath;

    public MetadataRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-test-{Guid.NewGuid():N}.sqlite");
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void EnsureSchema_creates_tables()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        // Idempotent:
        repo.EnsureSchema();
        Assert.True(File.Exists(_dbPath));
    }

    [Fact]
    public void SearchClasses_returns_match_with_bool_coercion()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Models(Name,IsCustom) VALUES('AppFound',0);
            INSERT INTO Classes(Name,ModelId,ExtendsName,IsAbstract,IsFinal,SourcePath)
              VALUES('CustTable_Extension',1,'CustTable',0,1,'/x');";
        cmd.ExecuteNonQuery();

        var hits = repo.SearchClasses("Cust");
        var one = Assert.Single(hits);
        Assert.Equal("CustTable_Extension", one.Name);
        Assert.False(one.IsAbstract);
        Assert.True(one.IsFinal);
        Assert.Equal("AppFound", one.Model);
    }

    [Fact]
    public void Negative_limit_does_not_disable_the_row_cap()
    {
        // SQLite reads a NEGATIVE LIMIT as "no limit", so an unchecked `--limit -1`
        // turns a bounded search into a full-index dump.
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Models(Name,IsCustom) VALUES('AppFound',0);";
        for (var i = 0; i < 60; i++)
            cmd.CommandText += $"INSERT INTO Classes(Name,ModelId,IsAbstract,IsFinal,SourcePath) VALUES('CustCls{i:D3}',1,0,0,'/x');";
        cmd.ExecuteNonQuery();

        // -1 and 0 both fall back to the method's documented default (50), not "everything".
        Assert.Equal(50, repo.SearchClasses("CustCls", limit: -1).Count);
        Assert.Equal(50, repo.SearchClasses("CustCls", limit: 0).Count);
        Assert.Equal(5, repo.SearchClasses("CustCls", limit: 5).Count);
    }

    [Fact]
    public void Limit_is_capped_at_the_hard_ceiling()
    {
        Assert.Equal(MetadataRepository.MaxRowLimit, MetadataRepository.ClampLimit(int.MaxValue));
        Assert.Equal(MetadataRepository.MaxRowLimit, MetadataRepository.ClampLimit(5000));
        Assert.Equal(50, MetadataRepository.ClampLimit(-1));
        Assert.Equal(100, MetadataRepository.ClampLimit(0, fallback: 100));
        Assert.Equal(7, MetadataRepository.ClampLimit(7));
    }

    [Fact]
    public void Search_ranks_custom_models_above_Microsoft_models()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        // The custom class sorts LAST by name, so name-only ordering would push it
        // past the limit — exactly the "buried under Microsoft objects" failure.
        cmd.CommandText = @"
            INSERT INTO Models(Name,IsCustom) VALUES('ApplicationSuite',0);
            INSERT INTO Models(Name,IsCustom) VALUES('Contoso',1);
            INSERT INTO Classes(Name,ModelId,IsAbstract,IsFinal,SourcePath) VALUES('CustAaa',1,0,1,'/x');
            INSERT INTO Classes(Name,ModelId,IsAbstract,IsFinal,SourcePath) VALUES('CustBbb',1,0,1,'/x');
            INSERT INTO Classes(Name,ModelId,IsAbstract,IsFinal,SourcePath) VALUES('CustZzz',2,0,1,'/x');
            INSERT INTO Tables(Name,ModelId,SourcePath) VALUES('CustAaaTable',1,'/x');
            INSERT INTO Tables(Name,ModelId,SourcePath) VALUES('CustZzzTable',2,'/x');";
        cmd.ExecuteNonQuery();

        var classes = repo.SearchClasses("Cust");
        Assert.Equal(new[] { "CustZzz", "CustAaa", "CustBbb" }, classes.Select(c => c.Name).ToArray());

        // With a tight limit the custom hit must survive rather than be truncated.
        var topClass = Assert.Single(repo.SearchClasses("Cust", limit: 1));
        Assert.Equal("Contoso", topClass.Model);

        var tables = repo.SearchTables("Cust");
        Assert.Equal(new[] { "CustZzzTable", "CustAaaTable" }, tables.Select(t => t.Name).ToArray());
    }

    [Fact]
    public void GetTable_missing_returns_null()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        Assert.Null(repo.GetTableDetails("DoesNotExist"));
    }

    [Fact]
    public void FindExtensions_accepts_full_extension_name_for_base_target()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO Models(Name,IsCustom) VALUES('Contoso',1);
            INSERT INTO ObjectExtensions(Kind,TargetName,ExtensionName,ModelId,SourcePath)
              VALUES('Table','CustTable','CustTable.Extension',1,'/x');";
        cmd.ExecuteNonQuery();

        // Both the base table name and the full extension name resolve to the
        // same indexed row (a dot marks an extension suffix; AOT names have none).
        var byBase = repo.FindExtensions("CustTable", "Table");
        var byFull = repo.FindExtensions("CustTable.Extension", "Table");
        Assert.Single(byBase);
        Assert.Single(byFull);
        Assert.Equal("CustTable.Extension", byFull[0].ExtensionName);
    }

    [Fact]
    public void RecordExtractionRun_roundtrips_via_GetExtractionRuns()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.RecordExtractionRun("Contoso", DateTime.UtcNow, 1234, 10, 20, 3, 4, 50, true);
        repo.RecordExtractionRun("ApplicationSuite", DateTime.UtcNow, 42_000, 500, 1000, 0, 0, 100, false);

        var rows = repo.GetExtractionRuns(10);
        Assert.Equal(2, rows.Count);
        // Newest first (by RunId DESC).
        Assert.Equal("ApplicationSuite", rows[0].Model);
        Assert.Equal(42_000, rows[0].ElapsedMs);
        Assert.False(rows[0].IsCustom);

        var filtered = repo.GetExtractionRuns(10, "Contoso");
        Assert.Single(filtered);
        Assert.True(filtered[0].IsCustom);
    }

    [Fact]
    public void ApplyExtract_stamps_fingerprint_and_LastExtractedUtc()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var batch = ExtractBatch.Empty("Contoso") with { Publisher = "Contoso", Layer = "usr", IsCustom = true };
        repo.ApplyExtract(batch, sourceFingerprint: "42:1234567890");

        var fps = repo.GetModelFingerprints();
        Assert.Equal("42:1234567890", fps["Contoso"]);

        // Re-apply without fingerprint should NOT wipe it (COALESCE in UPDATE).
        repo.ApplyExtract(ExtractBatch.Empty("Contoso") with { IsCustom = true }, sourceFingerprint: null);
        Assert.Equal("42:1234567890", repo.GetModelFingerprints()["Contoso"]);
    }

    [Fact]
    public void GetDependencyGraph_deduplicates_edges()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        // Insert two models and manually add duplicate dependency rows via raw SQL.
        repo.UpsertModel("Fleet", null, null, true);
        repo.UpsertModel("ApplicationSuite", null, null, false);

        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // Deliberately insert the same dependency edge twice to simulate re-extraction.
        cmd.CommandText = @"
            INSERT INTO ModelDependencies(ModelId, Target)
                SELECT ModelId, 'ApplicationSuite' FROM Models WHERE Name='Fleet';
            INSERT INTO ModelDependencies(ModelId, Target)
                SELECT ModelId, 'ApplicationSuite' FROM Models WHERE Name='Fleet';";
        cmd.ExecuteNonQuery();

        var graph = repo.GetDependencyGraph();
        var fleetDeps = graph["Fleet"];
        // Must deduplicate: only one entry for ApplicationSuite despite two DB rows.
        Assert.Single(fleetDeps, d => string.Equals(d, "ApplicationSuite", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GetClassDetails_returns_class_and_methods()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        var batch = ExtractBatch.Empty("Fleet") with
        {
            Classes = new[]
            {
                new ExtractedClass("FleetService", null, false, true, "/x.xml",
                    new[]
                    {
                        new ExtractedMethod("run", "public void run()", "void", false),
                        new ExtractedMethod("init", "public void init()", "void", false),
                    })
            }
        };
        repo.ApplyExtract(batch);

        var detail = repo.GetClassDetails("FleetService");
        Assert.NotNull(detail);
        Assert.Equal("FleetService", detail!.Class.Name);
        Assert.Equal(2, detail.Methods.Count);
        Assert.Contains(detail.Methods, m => m.Name == "run");
        Assert.Contains(detail.Methods, m => m.Name == "init");
        Assert.Empty(detail.InheritedMethods);
    }

    [Fact]
    public void GetClassDetails_walks_the_extends_chain_for_inherited_methods()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        // GrandBase.helper / GrandBase.run  →  Base.run (override) →  Leaf.init
        var batch = ExtractBatch.Empty("Fleet") with
        {
            Classes = new[]
            {
                new ExtractedClass("GrandBase", null, false, false, "/g.xml",
                    new[]
                    {
                        new ExtractedMethod("helper", "public void helper()", "void", false),
                        new ExtractedMethod("run", "public void run()", "void", false),
                    }),
                new ExtractedClass("Base", "GrandBase", false, false, "/b.xml",
                    new[]
                    {
                        new ExtractedMethod("run", "public void run()", "void", false),
                        new ExtractedMethod("setup", "public void setup()", "void", false),
                    }),
                new ExtractedClass("Leaf", "Base", false, true, "/l.xml",
                    new[] { new ExtractedMethod("init", "public void init()", "void", false) }),
            }
        };
        repo.ApplyExtract(batch);

        var leaf = repo.GetClassDetails("Leaf");
        Assert.NotNull(leaf);
        Assert.Equal(new[] { "init" }, leaf!.Methods.Select(m => m.Name).ToArray());
        Assert.All(leaf.Methods, m => Assert.Null(m.DeclaringClass));

        // Nearest base first; "run" resolves to Base's override, not GrandBase's.
        Assert.Equal(new[] { "run", "setup", "helper" },
            leaf.InheritedMethods.Select(m => m.Name).ToArray());
        Assert.Equal(new[] { "Base", "Base", "GrandBase" },
            leaf.InheritedMethods.Select(m => m.DeclaringClass).ToArray());

        // An override is attributed to the class that declares it, never duplicated
        // into the inherited list.
        var mid = repo.GetClassDetails("Base");
        Assert.Equal(new[] { "run", "setup" }, mid!.Methods.Select(m => m.Name).OrderBy(n => n).ToArray());
        var inheritedOnBase = Assert.Single(mid.InheritedMethods);
        Assert.Equal("helper", inheritedOnBase.Name);
        Assert.Equal("GrandBase", inheritedOnBase.DeclaringClass);
    }

    [Fact]
    public void GetSecurityCoverage_reports_row_level_state_instead_of_omitting_it()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        // Before any policy is indexed, "no policy found" is not evidence of absence.
        Assert.Equal("Unknown", repo.GetSecurityCoverage("CustTable", "Table").RowLevel.State);

        repo.ApplyExtract(ExtractBatch.Empty("Fleet") with
        {
            SecurityPolicies = new[]
            {
                new ExtractedSecurityPolicy("FmVehiclePolicy", "FmVehicle", "FmVehicleQuery",
                    "Update", "RoleName", true, false, "/p.xml"),
            }
        });

        var constrained = repo.GetSecurityCoverage("FmVehicle", "Table").RowLevel;
        Assert.Equal("Constrained", constrained.State);
        Assert.Equal("FmVehiclePolicy", Assert.Single(constrained.Policies).Name);

        // Policies exist in the index and none names this table — now the empty
        // answer is authoritative.
        var unconstrained = repo.GetSecurityCoverage("CustTable", "Table").RowLevel;
        Assert.Equal("NotConstrained", unconstrained.State);
        Assert.Empty(unconstrained.Policies);

        // XDS constrains tables only.
        Assert.Equal("NotApplicable", repo.GetSecurityCoverage("FmVehicleForm", "Menuitem").RowLevel.State);
    }

    [Fact]
    public void GetClassDetails_returns_no_inherited_methods_when_base_is_outside_the_index()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var batch = ExtractBatch.Empty("Fleet") with
        {
            Classes = new[]
            {
                new ExtractedClass("FleetBatch", "RunBaseBatch", false, true, "/x.xml",
                    new[] { new ExtractedMethod("run", "public void run()", "void", false) }),
            }
        };
        repo.ApplyExtract(batch);

        var detail = repo.GetClassDetails("FleetBatch");
        Assert.Equal("RunBaseBatch", detail!.Class.Extends);
        Assert.Empty(detail.InheritedMethods);
    }

    [Theory]
    [InlineData("CustAccount")]
    [InlineData("custaccount")]
    [InlineData("CUSTACCOUNT")]
    [InlineData("cUsTaCcOuNt")]
    public void GetEdt_matches_regardless_of_case(string queried)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("ApplicationSuite") with
        {
            Edts = new[] { new ExtractedEdt("CustAccount", null, "String", null, 20) },
        });

        var edt = repo.GetEdt(queried);

        Assert.NotNull(edt);
        Assert.Equal("CustAccount", edt!.Name);
    }

    [Fact]
    public void GetEdt_prefers_the_custom_model_when_the_name_exists_in_several()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("ApplicationSuite") with
        {
            IsCustom = false,
            Edts = new[] { new ExtractedEdt("SharedId", null, "String", null, 10) },
        });
        repo.ApplyExtract(ExtractBatch.Empty("Contoso") with
        {
            IsCustom = true,
            Edts = new[] { new ExtractedEdt("SharedId", null, "String", null, 20) },
        });

        // Without an ORDER BY the LIMIT 1 took whichever row SQLite reached
        // first, so the answer depended on insertion order.
        Assert.Equal("Contoso", repo.GetEdt("sharedid")!.Model);
    }

    [Fact]
    public void GetEdtResolved_fills_inherited_StringSize_from_the_extends_chain()
    {
        // The AOT stores an EDT exactly as declared; a derived string EDT with no size
        // of its own inherits it (ItemId is really 20). Only the unset case is filled.
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Foundation") with
        {
            Edts = new[]
            {
                new ExtractedEdt("SysGroup", null, "String", null, 20),
                new ExtractedEdt("ItemIdBase", "SysGroup", "String", null, null),
                new ExtractedEdt("ItemId", "ItemIdBase", "String", null, null),
                new ExtractedEdt("ItemFreeTxt", "SysGroup", "String", null, 1000),
            },
        });

        var resolved = repo.GetEdtResolved("ItemId");
        Assert.NotNull(resolved);
        Assert.Equal(20, resolved!.Value.Edt.StringSize);
        Assert.Equal("SysGroup", resolved.Value.StringSizeInheritedFrom);

        // A declared size is authoritative — no walk, no inheritance marker.
        var declared = repo.GetEdtResolved("ItemFreeTxt");
        Assert.Equal(1000, declared!.Value.Edt.StringSize);
        Assert.Null(declared.Value.StringSizeInheritedFrom);
    }

    [Fact]
    public void GetEdtResolved_survives_a_cycle_and_a_missing_ancestor()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Foundation") with
        {
            Edts = new[]
            {
                new ExtractedEdt("LoopA", "LoopB", "String", null, null),
                new ExtractedEdt("LoopB", "LoopA", "String", null, null),
                new ExtractedEdt("Orphan", "NotIndexedAnywhere", "String", null, null),
            },
        });

        Assert.Null(repo.GetEdtResolved("LoopA")!.Value.Edt.StringSize);
        Assert.Null(repo.GetEdtResolved("Orphan")!.Value.Edt.StringSize);
        Assert.Null(repo.GetEdtResolved("NoSuchEdt"));
    }
}
