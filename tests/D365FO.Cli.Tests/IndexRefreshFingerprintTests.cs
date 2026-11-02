using D365FO.Cli.Commands.Index;
using D365FO.Core.Index;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>index refresh</c> decides per model from the stored fingerprint. These pin the cases the
/// old <c>count:newestMtime</c> fingerprint, and the DB-mtime threshold applied on top of it,
/// both let through.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class IndexRefreshFingerprintTests : IDisposable
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-refresh-fp-{Guid.NewGuid():N}");
    private readonly string _packages;
    private readonly string _classes;
    private readonly string _db;

    public IndexRefreshFingerprintTests()
    {
        _packages = Path.Combine(_root, "PackagesLocalDirectory");
        _classes = Path.Combine(_packages, "ConFleet", "ConFleet", "AxClass");
        _db = Path.Combine(_root, "index.sqlite");
        Directory.CreateDirectory(_classes);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Refresh_re_extracts_a_model_whose_only_change_is_a_renamed_object()
    {
        WriteClass("ConFleetOld");
        WriteClass("ConFleetKeep");
        Assert.Equal(0, Refresh());
        Assert.Contains("ConFleetOld", ClassNames());

        // Rename the way Visual Studio does it: new file, new <Name>, old file gone. The file
        // count and the newest mtime stay exactly where they were.
        File.Delete(Path.Combine(_classes, "ConFleetOld.xml"));
        WriteClass("ConFleetNew");

        Assert.Equal(0, Refresh());

        var names = ClassNames();
        Assert.Contains("ConFleetNew", names);
        Assert.DoesNotContain("ConFleetOld", names);
    }

    [Fact]
    public void Refresh_skips_a_model_whose_fingerprint_still_matches()
    {
        WriteClass("ConFleetKeep");
        Assert.Equal(0, Refresh());
        var before = new MetadataRepository(_db).GetExtractionRuns(model: "ConFleet").Count;

        Assert.Equal(0, Refresh());

        Assert.Equal(before, new MetadataRepository(_db).GetExtractionRuns(model: "ConFleet").Count);
    }

    private void WriteClass(string name)
    {
        var path = Path.Combine(_classes, name + ".xml");
        File.WriteAllText(path,
            $"<AxClass><Name>{name}</Name><SourceCode><Declaration><![CDATA[public class {name} {{ }}]]></Declaration><Methods /></SourceCode></AxClass>");
        // Every file is older than the index, which is what put the old DB-mtime threshold in
        // charge of skipping the model.
        File.SetLastWriteTimeUtc(path, Stamp);
    }

    private int Refresh()
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            return IndexRefreshCommand.RefreshCore(OutputMode.Kind.Json, new IndexRefreshCommand.Settings
            {
                PackagesPath = _packages,
                DatabasePath = _db,
            });
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private List<string> ClassNames()
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(new MetadataRepository(_db).ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Name FROM Classes";
        var names = new List<string>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }
}
