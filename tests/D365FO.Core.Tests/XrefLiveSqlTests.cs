using System.Text.Json.Nodes;
using D365FO.Core.Xref;
using Microsoft.Data.SqlClient;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The where-used query against a real SQL Server, through the same public path
/// <c>find refs --xref</c> takes: <c>D365FO_XREF_CONNECTIONSTRING</c>, the
/// <c>Encrypt</c> default, <c>Microsoft.Data.SqlClient</c>, the T-SQL itself.
/// </summary>
/// <remarks>
/// Inert unless <c>D365FO_TEST_SQLSERVER</c> holds a connection string to a server where the
/// login may create a database (no <c>Initial Catalog</c> needed). CI has none, so the unit
/// tests in <see cref="XrefQueryTests"/> check the SQL as text; this is the run that proves it
/// executes. Each run seeds its own uniquely named copy of the three DYNAMICSXREFDB tables
/// the query reads, laid with the traps the query has fallen into before, and drops it.
/// Locally: <c>docker run -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD=… -p 14333:1433
/// mcr.microsoft.com/mssql/server:2022-latest</c>, then
/// <c>D365FO_TEST_SQLSERVER="Server=127.0.0.1,14333;User ID=sa;Password=…"</c>.
/// </remarks>
public sealed class XrefLiveSqlTests : IDisposable
{
    private static readonly string? Server = Environment.GetEnvironmentVariable("D365FO_TEST_SQLSERVER");

    private readonly string? _database;
    private readonly string? _oldXref = Environment.GetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING");

    public XrefLiveSqlTests()
    {
        if (string.IsNullOrWhiteSpace(Server)) return;

        _database = "xreftest_" + Guid.NewGuid().ToString("N")[..12];
        Execute(Server, $"CREATE DATABASE [{_database}]");

        var db = new SqlConnectionStringBuilder(Server) { InitialCatalog = _database };
        // Deliberately no Encrypt key: the default the CLI fills in has to reach a server with a
        // self-signed certificate, which is what a dev VM's local SQL Server has.
        db.Remove("Encrypt");
        Execute(db.ConnectionString, Seed());
        Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING", db.ConnectionString);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING", _oldXref);
        if (_database is null) return;
        SqlConnection.ClearAllPools();
        Execute(Server!, $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}]");
    }

    private static void Execute(string connectionString, string sql)
    {
        using var c = new SqlConnection(XrefReferences.Normalize(connectionString));
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>The three tables the query reads, shaped like DYNAMICSXREFDB's.</summary>
    private static string Seed() => """
        CREATE TABLE Modules (Id INT PRIMARY KEY, Module NVARCHAR(256) NOT NULL);
        CREATE TABLE Names (Id INT IDENTITY PRIMARY KEY, Path NVARCHAR(900) NOT NULL UNIQUE, ModuleId INT NULL);
        CREATE TABLE [References] (SourceId INT NOT NULL, TargetId INT NOT NULL, Kind TINYINT NOT NULL,
                                   Line INT NOT NULL, [Column] INT NOT NULL);

        INSERT Modules VALUES (1, N'ApplicationSuite'), (2, N'MyModule');
        INSERT Names (Path, ModuleId) VALUES
          (N'/Tables/SalesLine', 1), (N'/Tables/SalesLine/Fields/SalesPrice', 1),
          (N'/Tables/SalesLine/Methods/find', 1),
          -- Prefix traps: a "%/SalesLine%" match pooled these into SalesLine's callers.
          (N'/Classes/SalesLineType', 1), (N'/Tables/SalesLine_IN', 1),
          (N'/Labels/@SYS123', NULL),
          (N'/Classes/SalesLineType/Methods/insert', 1),
          (N'/Classes/SalesLineType/Methods/validateWrite', 1),
          (N'/Forms/SalesTable/DataSources/SalesLine/Methods/active', 1),
          (N'/Classes/MyExt_SalesLine_Extension', 2),
          (N'/Classes/PrefixOnly/Methods/run', 1),
          (N'/Classes/Bulk/Methods/run', 1);

        DECLARE @n TABLE (Path NVARCHAR(900), Id INT);
        INSERT @n SELECT Path, Id FROM Names;
        DECLARE @SalesLine INT = (SELECT Id FROM @n WHERE Path = N'/Tables/SalesLine'),
                @Price INT = (SELECT Id FROM @n WHERE Path = N'/Tables/SalesLine/Fields/SalesPrice'),
                @Find INT = (SELECT Id FROM @n WHERE Path = N'/Tables/SalesLine/Methods/find'),
                @TypeCls INT = (SELECT Id FROM @n WHERE Path = N'/Classes/SalesLineType'),
                @In INT = (SELECT Id FROM @n WHERE Path = N'/Tables/SalesLine_IN'),
                @Label INT = (SELECT Id FROM @n WHERE Path = N'/Labels/@SYS123'),
                @Insert INT = (SELECT Id FROM @n WHERE Path = N'/Classes/SalesLineType/Methods/insert'),
                @Validate INT = (SELECT Id FROM @n WHERE Path = N'/Classes/SalesLineType/Methods/validateWrite'),
                @Active INT = (SELECT Id FROM @n WHERE Path = N'/Forms/SalesTable/DataSources/SalesLine/Methods/active'),
                @Ext INT = (SELECT Id FROM @n WHERE Path = N'/Classes/MyExt_SalesLine_Extension'),
                @Prefix INT = (SELECT Id FROM @n WHERE Path = N'/Classes/PrefixOnly/Methods/run'),
                @Bulk INT = (SELECT Id FROM @n WHERE Path = N'/Classes/Bulk/Methods/run');

        -- Kind ids: 0 Declaration, 1 Set, 2 Read, 3 Call, 4 Reference, 5 Type, 6 Extends, 7 Implements.
        INSERT [References] VALUES
          (@Insert, @SalesLine, 5, 10, 9), (@Insert, @Price, 2, 12, 20),
          (@Validate, @Price, 2, 30, 14), (@Validate, @Label, 4, 31, 22),
          (@Active, @Find, 3, 5, 18), (@Active, @Find, 3, 9, 18), (@Active, @Find, 3, 14, 18),
          (@Ext, @SalesLine, 6, 1, 1),
          (@Prefix, @TypeCls, 3, 3, 4), (@Prefix, @In, 2, 4, 4);

        -- 300 Reads that sort before every Call: "the Calls among the first 200 references of any
        -- kind" — the old filter-after-TOP — finds none of the three.
        INSERT [References] (SourceId, TargetId, Kind, Line, [Column])
        SELECT TOP (300) @Bulk, @Price, 2, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 1
        FROM sys.all_objects;
        """;

    private static JsonObject Ok(ToolResult<object> r)
    {
        Assert.True(r.Ok, r.Error?.Message);
        return Assert.IsType<JsonObject>(r.Data);
    }

    private static List<string> Sources(JsonObject data) =>
        data["items"]!.AsArray().Select(i => (string)i!["source"]!).ToList();

    [Fact]
    public void A_bare_name_returns_one_row_per_referencing_object_and_no_prefix_matches()
    {
        if (_database is null) return;

        var data = Ok(XrefReferences.Find("SalesLine", null, null, 200, lines: 0));

        Assert.Equal("xrefdb", (string?)data["_source"]);
        Assert.Equal(
            new[] { "/Classes/Bulk", "/Classes/MyExt_SalesLine_Extension", "/Classes/SalesLineType", "/Forms/SalesTable" },
            Sources(data));
        Assert.DoesNotContain("/Classes/PrefixOnly", Sources(data));

        var type = data["items"]!.AsArray().Single(i => (string)i!["source"]! == "/Classes/SalesLineType")!;
        // Rows come back ordered by source path, then kind id: Read (2) before Type (5).
        Assert.Equal(new[] { "Read", "Type" }, type["kinds"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(new[] { "insert", "validateWrite" }, type["members"]!.AsArray().Select(m => (string)m!));
        Assert.Equal("ApplicationSuite", (string?)type["module"]);
        Assert.Equal(304 + 3, (int)data["references"]!);
    }

    [Fact]
    public void A_kind_filter_finds_calls_that_sort_after_the_row_limit()
    {
        if (_database is null) return;

        var data = Ok(XrefReferences.Find("SalesLine", "call", null, 200, lines: 1));

        Assert.Equal("references", (string?)data["mode"]);
        var rows = data["items"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r =>
        {
            Assert.Equal("Call", (string?)r!["kind"]);
            Assert.Equal("/Forms/SalesTable/DataSources/SalesLine/Methods/active", (string?)r["source"]);
            Assert.Equal(18, (int)r["column"]!);
        });
        Assert.False((bool)data["truncated"]!);
    }

    [Fact]
    public void A_qualified_member_is_scoped_to_its_owner()
    {
        if (_database is null) return;

        var data = Ok(XrefReferences.Find("SalesLine.find", null, null, 200, lines: 0));

        Assert.True((bool)data["scoped"]!);
        Assert.Equal(new[] { "/Forms/SalesTable" }, Sources(data));
        Assert.Equal(3, (int)data["items"]![0]!["refs"]!);
    }

    [Fact]
    public void An_explicit_field_path_returns_its_readers()
    {
        if (_database is null) return;

        var data = Ok(XrefReferences.Find("/Tables/SalesLine/Fields/SalesPrice", null, null, 200, lines: 0));

        Assert.Equal(new[] { "/Classes/Bulk", "/Classes/SalesLineType" }, Sources(data));
    }

    [Fact]
    public void A_label_returns_its_where_used()
    {
        if (_database is null) return;

        var data = Ok(XrefReferences.Find("@SYS123", null, null, 200, lines: 0));

        var only = Assert.Single(data["items"]!.AsArray())!;
        Assert.Equal("/Classes/SalesLineType", (string?)only["source"]);
        Assert.Equal(new[] { "validateWrite" }, only["members"]!.AsArray().Select(m => (string)m!));
    }

    [Fact]
    public void The_object_limit_reads_enough_rows_to_fill_it()
    {
        if (_database is null) return;

        // 300 Bulk rows come first; one object per 20 rows read would stop at Bulk alone.
        var data = Ok(XrefReferences.Find("SalesLine", null, null, 2, lines: 0));

        Assert.Equal(2, (int)data["count"]!);
        Assert.True((bool)data["truncated"]!);
    }

    [Fact]
    public void A_missing_database_fails_with_the_servers_reason()
    {
        if (_database is null) return;

        var missing = new SqlConnectionStringBuilder(Server) { InitialCatalog = _database + "_missing" };
        Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING", missing.ConnectionString);

        var r = XrefReferences.Find("SalesLine", null, null, 200, lines: 0);

        Assert.False(r.Ok);
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, r.Error!.Code);
        Assert.Contains("Cannot open database", r.Error.Message);
    }
}
