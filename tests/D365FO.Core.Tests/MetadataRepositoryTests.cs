using D365FO.Core.Index;
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
    public void GetTable_missing_returns_null()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        Assert.Null(repo.GetTableDetails("DoesNotExist"));
    }
}
