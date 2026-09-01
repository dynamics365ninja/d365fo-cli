using D365FO.Core.Index;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Every column the per-model re-extract filters by must be indexed.
/// </summary>
/// <remarks>
/// <para>
/// <c>ApplyExtract</c> clears a model's rows before re-inserting them: roughly forty statements
/// of the shape <c>DELETE FROM X WHERE ModelId=@m</c>, plus a dozen nested
/// <c>DELETE FROM Child WHERE ParentId IN (SELECT ParentId FROM Parent WHERE ModelId=@m)</c>.
/// Of the 28 tables carrying <c>ModelId</c>, exactly one was indexed on it — so each of those
/// statements scanned the whole table, over 60k classes and 525k methods, once per model for
/// about 200 models.
/// </para>
/// <para>
/// Measured on a 751 MB index, re-applying one model: RentalManagement (906 classes) spent
/// <b>50.5 s writing before, 12.0 s after</b>; a 76-class model, <b>4.9 s before, 1.4 s after</b>.
/// The cost was O(models x database size), which is why a full extract got slower as it went.
/// </para>
/// <para>
/// A new table with a <c>ModelId</c> and no index would silently reintroduce it, and the symptom
/// (a slow extract) is far away from the cause. So the check is derived: it reads the columns out
/// of the schema rather than from a list somebody has to remember to extend.
/// </para>
/// </remarks>
public sealed class SchemaIndexCoverageTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"schema-idx-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    private SqliteConnection OpenFreshSchema()
    {
        new MetadataRepository(_dbPath).EnsureSchema();
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    /// <summary>Tables that declare a column, from the live schema rather than a hand-kept list.</summary>
    private static List<string> TablesWithColumn(SqliteConnection connection, string column)
    {
        var tables = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        var hits = new List<string>();
        foreach (var table in tables)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase)) continue;

                // An INTEGER PRIMARY KEY is an alias for the rowid, so a lookup on it is
                // already a B-tree seek and PRAGMA index_list shows nothing. Models.ModelId is
                // exactly that; demanding an index there would add a second copy of the rowid.
                var isRowidAlias = reader.GetInt32(5) == 1
                    && string.Equals(reader.GetString(2), "INTEGER", StringComparison.OrdinalIgnoreCase);
                if (!isRowidAlias) hits.Add(table);
                break;
            }
        }
        return hits;
    }

    /// <summary>Is <paramref name="column"/> the FIRST column of some index on the table?</summary>
    /// <remarks>
    /// First column, because SQLite can only use an index for a lookup on a prefix of its
    /// columns — an index on <c>(Name, ModelId)</c> does nothing for <c>WHERE ModelId=?</c>.
    /// </remarks>
    private static bool HasLeadingIndexOn(SqliteConnection connection, string table, string column)
    {
        var indexes = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA index_list({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) indexes.Add(reader.GetString(1));
        }

        foreach (var index in indexes)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA index_info({index})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // seqno 0 is the leading column.
                if (reader.GetInt32(0) != 0) continue;
                if (!reader.IsDBNull(2) && string.Equals(reader.GetString(2), column, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Foreign keys declared by <paramref name="table"/>, as (child column, parent).</summary>
    private static List<(string Column, string Parent)> ForeignKeysOf(SqliteConnection connection, string table)
    {
        var keys = new List<(string, string)>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA foreign_key_list({table})";
        using var reader = cmd.ExecuteReader();
        var parentOrdinal = reader.GetOrdinal("table");
        var fromOrdinal = reader.GetOrdinal("from");
        while (reader.Read()) keys.Add((reader.GetString(fromOrdinal), reader.GetString(parentOrdinal)));
        return keys;
    }

    /// <summary>
    /// Foreign keys are enforced (<c>PRAGMA foreign_keys = ON</c>), so deleting a parent row
    /// makes SQLite look for children referencing it. With no index on the child's FK column
    /// that is a full scan of the child table for <em>every</em> parent row deleted — a cost
    /// that appears nowhere in the SQL the extractor writes, which is what made it hard to
    /// spot: <c>DELETE FROM Forms WHERE ModelId=?</c> spent 1.27s scanning FormDataSources
    /// once per form.
    /// </summary>
    [Fact]
    public void Every_foreign_key_child_column_is_indexed()
    {
        using var connection = OpenFreshSchema();

        var tables = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        var uncovered = new List<string>();
        var seen = 0;
        foreach (var table in tables)
        {
            foreach (var (column, parent) in ForeignKeysOf(connection, table))
            {
                seen++;
                if (!HasLeadingIndexOn(connection, table, column))
                    uncovered.Add($"{table}({column}) -> {parent}");
            }
        }

        Assert.True(seen > 10, $"only {seen} foreign keys found — the schema query is wrong");
        Assert.True(uncovered.Count == 0,
            "These foreign key columns have no index leading on them, so enforcing the key "
            + "scans the whole child table once per deleted parent row:\n  "
            + string.Join("\n  ", uncovered));
    }

    [Fact]
    public void Every_table_with_a_ModelId_is_indexed_on_it()
    {
        using var connection = OpenFreshSchema();

        var tables = TablesWithColumn(connection, "ModelId");
        Assert.True(tables.Count > 20, $"only {tables.Count} tables carry ModelId — the schema query is wrong");

        var unindexed = tables.Where(t => !HasLeadingIndexOn(connection, t, "ModelId")).ToList();

        Assert.True(unindexed.Count == 0,
            "These tables carry ModelId with no index leading on it, so the per-model DELETE in "
            + "ApplyExtract scans them whole, once per model:\n  " + string.Join("\n  ", unindexed));
    }

    [Theory]
    // The parent keys the nested per-model deletes filter the CHILD table by.
    [InlineData("Methods", "ClassId")]
    [InlineData("ClassAttributes", "ClassId")]
    [InlineData("TableFields", "TableId")]
    [InlineData("TableMethods", "TableId")]
    [InlineData("TableIndexes", "TableId")]
    [InlineData("TableDeleteActions", "TableId")]
    [InlineData("EnumValues", "EnumId")]
    [InlineData("QueryDataSources", "QueryId")]
    [InlineData("ViewFields", "ViewId")]
    [InlineData("DataEntityFields", "EntityId")]
    [InlineData("ReportDataSets", "ReportId")]
    [InlineData("ServiceOperations", "ServiceId")]
    [InlineData("ServiceGroupMembers", "GroupId")]
    [InlineData("MapFields", "MapId")]
    [InlineData("MapTables", "MapId")]
    [InlineData("SecurityMap", "Role")]
    [InlineData("Relations", "FromTable")]
    [InlineData("Labels", "LabelFile")]
    public void Every_child_key_the_reextract_deletes_by_is_indexed(string table, string column)
    {
        using var connection = OpenFreshSchema();

        Assert.True(HasLeadingIndexOn(connection, table, column),
            $"{table}({column}) has no index leading on it — the nested DELETE in ApplyExtract "
            + "scans the whole child table once per model.");
    }
}
