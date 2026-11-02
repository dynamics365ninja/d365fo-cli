using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Models whose folder left the configured roots: which ones count as departed, which never do,
/// and what pruning removes.
/// </summary>
public class IndexPruneTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"prune-{Guid.NewGuid():N}");
    private readonly string _rootA;
    private readonly string _oldClone;
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;

    public IndexPruneTests()
    {
        _rootA = Path.Combine(_root, "Metadata");
        _oldClone = Path.Combine(_root, "OldClone", "metadata");
        Directory.CreateDirectory(_rootA);
        _dbPath = Path.Combine(_root, "index.sqlite");
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void A_model_indexed_from_a_folder_outside_every_root_is_reported_and_pruned()
    {
        // The repo-move case: the test model was indexed from a clone nobody configures any more.
        IndexModel(_rootA, "Keep", "KeepClass");
        IndexModel(_oldClone, "GoneTests", "GoneTestClass");

        var report = IndexPrune.Find(_repo, new[] { _rootA });

        var departed = Assert.Single(report.Departed);
        Assert.Equal("GoneTests", departed.Model);
        Assert.Equal(IndexPrune.Reasons.OutsideRoots, departed.Reason);

        Assert.Equal(new[] { "GoneTests" }, IndexPrune.Prune(_repo, report));
        Assert.DoesNotContain(_repo.ListModels(), m => m.Name == "GoneTests");
        Assert.Contains(_repo.ListModels(), m => m.Name == "Keep");
        Assert.Equal(new[] { "KeepClass" }, ClassNames());
    }

    [Fact]
    public void A_model_under_a_root_that_is_missing_right_now_is_never_pruned()
    {
        // An unmounted drive must not cost a standard model its rows.
        var unmounted = Path.Combine(_root, "Unmounted");
        IndexModel(unmounted, "ApplicationSuite", "SalesTable", isCustom: false);
        Directory.Delete(unmounted, recursive: true);

        var report = IndexPrune.Find(_repo, new[] { _rootA, unmounted });

        Assert.Empty(report.Departed);
        Assert.Contains(report.MissingRoots, r => r.EndsWith("Unmounted", StringComparison.Ordinal));
        Assert.Empty(IndexPrune.Prune(_repo, report));
        Assert.Contains(_repo.ListModels(), m => m.Name == "ApplicationSuite");
    }

    [Fact]
    public void With_no_root_reachable_nothing_is_judged_departed()
    {
        // With nothing to compare against, every model would look departed.
        IndexModel(_rootA, "Keep", "KeepClass");

        var report = IndexPrune.Find(_repo, new[] { Path.Combine(_root, "NoSuchRoot") });

        Assert.True(report.NoRootReachable);
        Assert.Empty(report.Departed);
    }

    [Fact]
    public void A_model_folder_deleted_under_a_configured_root_is_departed()
    {
        IndexModel(_rootA, "Removed", "RemovedClass");
        IndexModel(_rootA, "Keep", "KeepClass");
        Directory.Delete(Path.Combine(_rootA, "Removed"), recursive: true);

        var departed = Assert.Single(IndexPrune.Find(_repo, new[] { _rootA }).Departed);

        Assert.Equal("Removed", departed.Model);
        Assert.Equal(IndexPrune.Reasons.FolderMissing, departed.Reason);
    }

    [Fact]
    public void A_model_now_under_another_root_is_relocated_not_departed()
    {
        // Deleting it would throw away a model a sync puts right.
        IndexModel(_oldClone, "Moved", "MovedClass");
        WriteModelFolder(_rootA, "Moved", "MovedClass");

        var report = IndexPrune.Find(_repo, new[] { _rootA });

        Assert.Empty(report.Departed);
        var relocated = Assert.Single(report.Relocated);
        Assert.Equal("Moved", relocated.Model);
    }

    [Fact]
    public void A_missing_root_suspends_outside_root_verdicts_because_it_may_be_a_mistyped_root()
    {
        // The model is intact on disk under _oldClone; the configured spelling of that root is wrong.
        IndexModel(_oldClone, "Mistyped", "MistypedClass");

        var report = IndexPrune.Find(_repo, new[] { _rootA, _oldClone + "-typo" });

        Assert.Empty(report.Departed);
        Assert.Single(report.MissingRoots);
        Assert.Empty(IndexPrune.Prune(_repo, report));
        Assert.Contains(_repo.ListModels(), m => m.Name == "Mistyped");
    }

    [Fact]
    public void A_prune_with_no_packages_root_configured_is_refused()
    {
        // An MCP server started without D365FO_PACKAGES_PATH would otherwise see every standard model as departed.
        var old = Environment.GetEnvironmentVariable("D365FO_PACKAGES_PATH");
        Environment.SetEnvironmentVariable("D365FO_PACKAGES_PATH", null);
        try
        {
            IndexModel(_rootA, "Keep", "KeepClass");
            // A settings.json/profile on a developer box can still supply one; nothing to test then.
            if (D365FoSettings.FromEnvironment(_dbPath).PackagesPath is not null) return;

            var result = IndexPrune.SyncThenPrune(null, null, packagesOverride: null, databasePath: _dbPath,
                indexSource: false, prune: true);

            Assert.False(result.Ok);
            Assert.Equal("PACKAGES_PATH_NOT_FOUND", result.Error!.Code);
            Assert.Contains(_repo.ListModels(), m => m.Name == "Keep");
        }
        finally
        {
            Environment.SetEnvironmentVariable("D365FO_PACKAGES_PATH", old);
        }
    }

    [Fact]
    public void Deleting_a_model_removes_its_labels_but_not_a_label_file_another_model_still_ships()
    {
        var goneFolder = WriteModelFolder(_oldClone, "GoneTests", "GoneTestClass");
        var keepFolder = WriteModelFolder(_rootA, "Keep", "KeepClass");
        string LabelPath(string folder, string file) =>
            Path.Combine(folder, "AxLabelFile", "LabelResources", "en-US", $"{file}.en-US.label.txt");

        _repo.ApplyExtract(Batch("GoneTests", goneFolder, "GoneTestClass") with
        {
            LabelFiles = new[]
            {
                new ExtractedLabelFile("GoneLabels", "en-US", LabelPath(goneFolder, "GoneLabels")),
                new ExtractedLabelFile("Shared", "en-US", LabelPath(goneFolder, "Shared")),
            },
        });
        _repo.ApplyExtract(Batch("Keep", keepFolder, "KeepClass") with
        {
            LabelFiles = new[] { new ExtractedLabelFile("Shared", "en-US", LabelPath(keepFolder, "Shared")) },
        });
        InsertLabel("GoneLabels", "GoneKey");
        InsertLabel("Shared", "SharedKey");

        Assert.True(_repo.DeleteModel("GoneTests", goneFolder));

        Assert.Equal(new[] { "SharedKey" }, LabelKeys());
        Assert.False(_repo.DeleteModel("GoneTests", goneFolder));
    }

    private void IndexModel(string root, string model, string className, bool isCustom = true)
    {
        var folder = WriteModelFolder(root, model, className);
        _repo.ApplyExtract(Batch(model, folder, className, isCustom));
    }

    private static string WriteModelFolder(string root, string model, string className)
    {
        var folder = Path.Combine(root, model, model);
        Directory.CreateDirectory(Path.Combine(folder, "AxClass"));
        File.WriteAllText(Path.Combine(folder, "AxClass", className + ".xml"), $"<AxClass><Name>{className}</Name></AxClass>");
        return folder;
    }

    private static ExtractBatch Batch(string model, string folder, string className, bool isCustom = true)
        => ExtractBatch.Empty(model) with
        {
            IsCustom = isCustom,
            Classes = new[]
            {
                new ExtractedClass(className, null, false, false,
                    Path.Combine(folder, "AxClass", className + ".xml"), Array.Empty<ExtractedMethod>()),
            },
        };

    private void InsertLabel(string file, string key)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Labels(LabelFile, Language, Key, Value) VALUES($f, 'en-US', $k, 'x')";
        cmd.Parameters.AddWithValue("$f", file);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    private List<string> LabelKeys() => Column("SELECT Key FROM Labels ORDER BY Key");

    private List<string> ClassNames() => Column("SELECT Name FROM Classes ORDER BY Name");

    private List<string> Column(string sql)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(_repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var values = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) values.Add(r.GetString(0));
        return values;
    }
}
