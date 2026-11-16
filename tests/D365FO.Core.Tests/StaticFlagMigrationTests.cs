using System;
using System.IO;
using System.Linq;
using D365FO.Core.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Schema v22: methods whose trimmed signature starts with <c>static</c> were indexed with
/// IsStatic = 0. The upgrade fixes the flag from the stored signature, with no re-extract.
/// </summary>
public sealed class StaticFlagMigrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"staticflag-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    [Fact]
    public void Upgrading_a_v21_index_marks_methods_declared_static_first_as_static()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        // What the old extractor stored: the signature is right, the flag is not.
        repo.ApplyExtract(ExtractBatch.Empty("Starter") with
        {
            Classes = new[]
            {
                new ExtractedClass("StarterJob", null, false, false, "x", new[]
                {
                    new ExtractedMethod("main", "static void main(Args _args)", "void", false),
                    new ExtractedMethod("construct", "Static StarterJob construct()", "StarterJob", false),
                    new ExtractedMethod("run", "public void run()", "void", false),
                    new ExtractedMethod("staticLike", "public void staticLike()", "void", false),
                }),
            },
        });

        using (var conn = new SqliteConnection(repo.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 21;";
            cmd.ExecuteNonQuery();
        }

        var upgraded = new MetadataRepository(_dbPath);
        Assert.True(upgraded.EnsureSchema());

        var methods = upgraded.GetClassDetails("StarterJob")!.Methods.ToDictionary(m => m.Name, m => m.IsStatic);
        Assert.True(methods["main"]);
        Assert.True(methods["construct"]);
        Assert.False(methods["run"]);
        Assert.False(methods["staticLike"]);
    }
}
