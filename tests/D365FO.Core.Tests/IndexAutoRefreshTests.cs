using D365FO.Core.Index;
using D365FO.Core.Journal;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// A write re-indexes the model it touched, so the next read in the same process sees it.
/// </summary>
/// <remarks>
/// Every test passes its own roots and database to the scope rather than setting
/// <c>D365FO_PACKAGES_PATH</c>: that variable is process-wide and other test classes read it.
/// </remarks>
public class IndexAutoRefreshTests : IDisposable
{
    private static readonly string SamplesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private readonly string _work = Path.Combine(Path.GetTempPath(), $"autorefresh-{Guid.NewGuid():N}");
    private readonly string _root;
    private readonly string _db;
    private readonly string _classes;

    public IndexAutoRefreshTests()
    {
        _root = Path.Combine(_work, "PackagesLocalDirectory");
        CopyDirectory(SamplesDir, _root);
        _db = Path.Combine(_work, "index.sqlite");
        _classes = Path.Combine(_root, "TestModel", "TestModel", "AxClass");

        // The model as an agent finds it: indexed, method bodies included.
        Assert.True(IndexSync.Sync("TestModel", null, _root, _db, indexSource: true).Ok);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_db);
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    [Fact]
    public void A_class_written_then_read_in_one_process_is_found()
    {
        // The failure this prevents: create a table, then `generate entity --table` says
        // TABLE_NOT_FOUND because nothing refreshed the index in between.
        var repo = new MetadataRepository(_db);
        Assert.Null(repo.GetClassDetails("ConFleetDispatcher"));

        IReadOnlyList<IndexAutoRefresh.ModelRefresh>? refreshed;
        using (var scope = IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root }))
        {
            WriteClass("ConFleetDispatcher");
            refreshed = scope.Flush();
        }

        var r = Assert.Single(refreshed!);
        Assert.Equal("TestModel", r.Model);
        Assert.True(r.Ok, r.Error);
        Assert.NotNull(repo.GetClassDetails("ConFleetDispatcher"));
    }

    [Fact]
    public void A_refresh_after_a_write_keeps_the_models_method_source_index()
    {
        // A sync without indexSource clears the model's method bodies, which silently broke
        // `find references`. The refresh reads the state first and keeps it.
        var repo = new MetadataRepository(_db);
        var before = repo.CountMethodSource("TestModel");
        Assert.True(before > 0);

        using (var scope = IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root }))
        {
            WriteClass("ConFleetDispatcher");
            Assert.True(Assert.Single(scope.Flush()!).MethodSourceIndexed);
        }

        Assert.True(repo.CountMethodSource("TestModel") > before);
    }

    [Fact]
    public void An_undone_write_is_forgotten_by_the_index()
    {
        var repo = new MetadataRepository(_db);
        using (var scope = IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root }))
        {
            WriteClass("ConFleetDispatcher");
            scope.Flush();
            Assert.NotNull(repo.GetClassDetails("ConFleetDispatcher"));

            // A private journal holding just this write: the shared one is appended to by every
            // test running in parallel, so "undo the last step" there could undo someone else's.
            var target = Path.Combine(_classes, "ConFleetDispatcher.xml");
            var journal = new ModificationJournal(Path.Combine(_work, "journal"));
            journal.Append(new JournalEntry(
                Id: "", TimestampUtc: default, Command: "generate class", TargetType: JournalTargetType.AotObject,
                Kind: "class", ObjectName: "ConFleetDispatcher", SecondaryKey: null, Model: "TestModel",
                Operation: JournalOperation.Create, WritePath: JournalWritePath.Disk, TargetPath: target,
                PreImage: null, IsTombstone: true, RnrProjDelta: null));
            scope.Flush();

            var undo = UndoEngine.Undo(journal, steps: 1, dryRun: false);
            Assert.True(Assert.Single(undo.Steps).Ok);
            Assert.NotNull(scope.Flush());
        }

        Assert.Null(repo.GetClassDetails("ConFleetDispatcher"));
    }

    [Fact]
    public void Without_an_open_scope_a_write_refreshes_nothing()
    {
        // Library callers and tests that write files must not trigger a re-extract.
        WriteClass("ConFleetDispatcher");

        Assert.Null(new MetadataRepository(_db).GetClassDetails("ConFleetDispatcher"));
    }

    [Fact]
    public void A_scope_opened_with_refresh_off_records_nothing()
    {
        using var scope = IndexAutoRefresh.BeginScope(enabled: false, databasePath: _db, packagesRoots: new[] { _root });
        WriteClass("ConFleetDispatcher");

        Assert.Null(scope.Flush());
        Assert.Null(new MetadataRepository(_db).GetClassDetails("ConFleetDispatcher"));
    }

    [Fact]
    public void The_environment_variable_turns_the_refresh_off()
    {
        var previous = Environment.GetEnvironmentVariable(IndexAutoRefresh.EnvVar);
        try
        {
            Environment.SetEnvironmentVariable(IndexAutoRefresh.EnvVar, "0");
            using var scope = IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root });
            Assert.False(scope.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable(IndexAutoRefresh.EnvVar, previous);
        }
    }

    [Fact]
    public void A_write_outside_every_configured_root_is_not_refreshed()
    {
        // A scaffold written with --out to a scratch folder is not part of the index, and a model
        // name it happens to share must not send the refresh off to re-read a real model.
        var scratch = Path.Combine(_work, "scratch", "TestModel", "TestModel", "AxClass");
        Directory.CreateDirectory(scratch);
        using var scope = IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root });

        ScaffoldFileWriter.Write(ClassXml("ConFleetScratch"), Path.Combine(scratch, "ConFleetScratch.xml"));

        Assert.Null(scope.Flush());
    }

    [Fact]
    public void A_label_file_is_traced_to_its_model_four_levels_down()
    {
        var label = Path.Combine(_root, "TestModel", "TestModel", "AxLabelFile", "LabelResources", "en-US", "Fleet.en-US.label.txt");

        var hit = IndexAutoRefresh.LocateUnderRoots(label, new[] { _root });

        Assert.Equal("TestModel", hit?.Model);
    }

    [Theory]
    [InlineData("TestModel", "bin", "TestModel.xml")]
    [InlineData("TestModel", "Descriptor", "TestModel.xml")]
    [InlineData("TestModel", "XppMetadata", "TestModel", "AxClass", "ConFleetX.xml")]
    [InlineData("TestModel", "TestModel", "TestModel.rnrproj.xml")]
    public void Build_output_and_descriptors_are_not_mistaken_for_a_model(params string[] segments)
    {
        // A compile writes these under the watched roots; read as <Package>/<Model>/… they named
        // models like "bin" and "XppMetadata", and the daemon tried to re-sync them on every build.
        var path = Path.Combine(new[] { _root }.Concat(segments).ToArray());

        Assert.Null(IndexAutoRefresh.LocateUnderRoots(path, new[] { _root }));
    }

    [Fact]
    public void A_successful_refresh_drops_the_stale_not_refreshed_warning_and_reports_the_model()
    {
        var result = ToolResult<object>.Success(new { path = "x" },
            new[] { "Index not auto-refreshed — run `d365fo index refresh --model TestModel`.", "keep me" });

        var attached = IndexAutoRefresh.Attach(result,
            new[] { new IndexAutoRefresh.ModelRefresh("TestModel", true, 12, true, null) });

        Assert.Equal(new[] { "keep me" }, attached.Warnings);
        var json = D365Json.Serialize(attached);
        Assert.Contains("\"indexRefreshed\":{\"model\":\"TestModel\",\"elapsedMs\":12", json, StringComparison.Ordinal);
    }

    private void WriteClass(string name)
        => ScaffoldFileWriter.Write(ClassXml(name), Path.Combine(_classes, name + ".xml"));

    private static string ClassXml(string name) =>
        $"<AxClass><Name>{name}</Name><SourceCode><Declaration><![CDATA[public class {name} {{ }}]]></Declaration>"
        + "<Methods><Method><Name>run</Name><Source><![CDATA[    public void run()\n    {\n    }\n]]></Source></Method></Methods>"
        + "</SourceCode></AxClass>";

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
