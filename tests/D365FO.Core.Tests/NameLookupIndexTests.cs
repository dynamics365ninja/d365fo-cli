using System;
using System.Collections.Generic;
using System.IO;
using D365FO.Core.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Schema v24: names are matched with <c>COLLATE NOCASE</c>, which a BINARY index cannot
/// serve, so on a full installation every symbol and member lookup of <c>validate
/// references</c> scanned the table and one large class took minutes.
/// </summary>
public sealed class NameLookupIndexTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"nocase-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    public static TheoryData<string> Lookups => new()
    {
        "SELECT 1 FROM Tables WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Classes WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Edts WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Enums WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Forms WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Queries WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Views WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM DataEntities WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM Maps WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM MenuItems WHERE Name = 'x' COLLATE NOCASE",
        "SELECT 1 FROM ObjectExtensions WHERE ExtensionName = 'x' COLLATE NOCASE",
        "SELECT 1 FROM CocExtensions WHERE TargetClass = 'x' COLLATE NOCASE AND TargetMethod = 'y' COLLATE NOCASE",
        "SELECT 1 FROM Labels WHERE LabelFile = 'x' COLLATE NOCASE AND Key = 'y' COLLATE NOCASE",
        "SELECT 1 FROM Labels WHERE LabelFile = 'x' COLLATE NOCASE",
        "SELECT Value FROM Labels WHERE Key = 'y' OR Key = '@y' OR (Key = '1' AND +LabelFile = 'x')",
        "SELECT 1 FROM Methods m JOIN Classes c ON m.ClassId = c.ClassId " +
            "WHERE c.Name = 'x' COLLATE NOCASE AND m.Name = 'y' COLLATE NOCASE",
        "SELECT 1 FROM TableMethods m JOIN Tables t ON m.TableId = t.TableId " +
            "WHERE t.Name = 'x' COLLATE NOCASE AND m.Name = 'y' COLLATE NOCASE",
    };

    [Theory]
    [MemberData(nameof(Lookups))]
    public void A_case_insensitive_name_lookup_is_an_index_seek(string sql)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        foreach (var step in Plan(repo, sql))
        {
            Assert.DoesNotContain("SCAN", step);
            // A seek on a leading column alone (ClassId=?) still walks every method of the type.
            if (sql.Contains("m.Name =") && step.Contains("Methods")) Assert.Contains("Name=?", step);
        }
    }

    [Fact]
    public void Upgrading_a_v23_index_adds_the_indexes_without_a_re_extract()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Kept"));
        Execute(repo, "UPDATE Models SET SourceFingerprint = 'unchanged'; " +
                      "DROP INDEX IX_Classes_Name_NoCase; PRAGMA user_version = 23;");

        var upgraded = new MetadataRepository(_dbPath);
        Assert.True(upgraded.EnsureSchema());

        Assert.Contains(Plan(upgraded, "SELECT 1 FROM Classes WHERE Name = 'x' COLLATE NOCASE"),
            step => step.Contains("IX_Classes_Name_NoCase"));
        using var conn = new SqliteConnection(upgraded.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT SourceFingerprint FROM Models WHERE Name = 'Kept'";
        Assert.Equal("unchanged", cmd.ExecuteScalar());
    }

    private static List<string> Plan(MetadataRepository repo, string sql)
    {
        using var conn = new SqliteConnection(repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = cmd.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) steps.Add(reader.GetString(3));
        return steps;
    }

    private static void Execute(MetadataRepository repo, string sql)
    {
        using var conn = new SqliteConnection(repo.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
