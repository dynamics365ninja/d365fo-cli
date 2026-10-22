using System.Text.Json.Nodes;
using D365FO.Core.Xref;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The DYNAMICSXREFDB where-used query, shared by the direct path and the bridge, and the
/// orchestration around it. No SQL Server here: the SQL text is checked on a command that is
/// never executed, and the sources are fakes.
/// </summary>
public class XrefQueryTests
{
    // ---- target path resolution ----

    [Theory]
    [InlineData("@WAX2194", "/Labels/@WAX2194")]
    [InlineData("@SysCommon:Delete", "/Labels/@SysCommon:Delete")]
    [InlineData("/Labels/@WAX2194", "/Labels/@WAX2194")]
    public void A_label_resolves_to_its_single_exact_path(string symbol, string path)
    {
        var p = XrefQuery.ResolveTargetPaths(symbol);

        Assert.Equal(new[] { path }, p.Exact);
        Assert.Empty(p.Like);
        Assert.True(p.Scoped);
    }

    [Fact]
    public void A_member_is_scoped_to_its_owner_across_every_container()
    {
        var p = XrefQuery.ResolveTargetPaths("SalesTable.initFromSalesQuotationTable");

        Assert.True(p.Scoped);
        Assert.Empty(p.Like);
        Assert.Contains("/Tables/SalesTable/Methods/initFromSalesQuotationTable", p.Exact);
        Assert.Contains("/Classes/SalesTable/Fields/initFromSalesQuotationTable", p.Exact);
    }

    [Fact]
    public void An_explicit_member_path_is_an_exact_leaf()
    {
        var p = XrefQuery.ResolveTargetPaths("/Tables/SalesLine/Fields/SalesPrice");

        Assert.Equal(new[] { "/Tables/SalesLine/Fields/SalesPrice" }, p.Exact);
        Assert.Empty(p.Like);
        Assert.True(p.Scoped);
    }

    [Fact]
    public void An_explicit_object_path_takes_its_children_too()
    {
        var p = XrefQuery.ResolveTargetPaths("/Tables/SalesLine");

        Assert.Equal(new[] { "/Tables/SalesLine" }, p.Exact);
        Assert.Equal(new[] { "/Tables/SalesLine/%" }, p.Like);
        Assert.False(p.Scoped);
    }

    [Fact]
    public void A_bare_name_matches_whole_path_nodes_not_prefixes()
    {
        var p = XrefQuery.ResolveTargetPaths("SalesLine");

        Assert.Equal(new[] { "/SalesLine" }, p.Exact);
        Assert.Contains("%/SalesLine", p.Like);
        Assert.Contains("%/SalesLine/%", p.Like);
        // "%/SalesLine%" pooled SalesLineType, SalesLine_IN, … into SalesLine's callers.
        Assert.DoesNotContain("%/SalesLine%", p.Like);
        Assert.False(p.Scoped);
    }

    // ---- the SQL ----

    [Fact]
    public void A_kind_filter_is_applied_in_sql_before_top()
    {
        using var cmd = new SqliteConnection().CreateCommand();

        XrefQuery.Prepare(cmd, XrefQuery.ResolveTargetPaths("SalesLine"), XrefQuery.KindId("Call"), 50);

        // Filtering the returned rows instead answered "the Calls among the first N references".
        Assert.Contains("AND r.Kind = @kind", cmd.CommandText);
        Assert.Equal(3, cmd.Parameters["@kind"].Value);
        Assert.Equal(50, cmd.Parameters["@limit"].Value);
        // The OR'ed path terms are grouped, so the kind filter applies to all of them.
        Assert.Contains("WHERE (tgtName.Path IN (@P0) OR tgtName.Path LIKE @L0", cmd.CommandText);
    }

    [Fact]
    public void The_aggregate_query_returns_one_row_per_source_and_kind()
    {
        using var cmd = new SqliteConnection().CreateCommand();

        XrefQuery.Prepare(cmd, XrefQuery.ResolveTargetPaths("SalesLine"), -1, 50, aggregate: true);

        Assert.Contains("GROUP BY srcName.Path, r.Kind", cmd.CommandText);
        Assert.Contains("COUNT(*)      AS Refs", cmd.CommandText);
        Assert.DoesNotContain("r.Line", cmd.CommandText);
        // GROUP BY/COUNT only — STRING_AGG would shut out SQL Server 2016, which older dev VMs run.
        Assert.DoesNotContain("STRING_AGG", cmd.CommandText);
    }

    [Fact]
    public void Names_only_asks_for_aggregated_rows_and_lines_for_raw_ones()
    {
        var asked = new List<bool>();
        XrefReferences.Find("SalesLine", null, null, 10, lines: 0,
            direct: (_, _, _, agg) => { asked.Add(agg); return (Rows(false), null); }, bridge: null);
        XrefReferences.Find("SalesLine", null, null, 10, lines: 1,
            direct: (_, _, _, agg) => { asked.Add(agg); return (Rows(false), null); }, bridge: null);

        Assert.Equal(new[] { true, false }, asked);
    }

    [Fact]
    public void Aggregated_rows_sum_into_the_object_reference_count()
    {
        var raw = Rows(false, ("/Classes/Bulk/Methods/run", "Read", "ApplicationSuite"),
                              ("/Classes/Bulk/Methods/run", "Call", "ApplicationSuite"));
        raw["items"]![0]!["refs"] = 300;
        raw["items"]![1]!["refs"] = 2;

        var data = XrefReferences.Group(raw, 10);

        Assert.Equal(1, (int)data["count"]!);
        Assert.Equal(302, (int)data["references"]!);
        Assert.Equal(302, (int)data["items"]![0]!["refs"]!);
    }

    [Fact]
    public void No_kind_filter_without_a_kind()
    {
        using var cmd = new SqliteConnection().CreateCommand();

        XrefQuery.Prepare(cmd, XrefQuery.ResolveTargetPaths("@WAX2194"), -1, 50);

        Assert.DoesNotContain("@kind", cmd.CommandText);
        Assert.DoesNotContain("LIKE", cmd.CommandText);
    }

    [Fact]
    public void An_unknown_reference_kind_is_rejected_before_the_database_is_touched()
    {
        using var never = new SqliteConnection("Data Source=/nonexistent/dir/x.db;Mode=ReadOnly");

        var r = XrefQuery.Find(never, "SalesLine", "class", 10);

        Assert.False((bool)r["ok"]!);
        Assert.Equal("BAD_INPUT", (string?)r["error"]);
    }

    [Fact]
    public void A_database_that_cannot_be_queried_reports_why()
    {
        using var noXref = new SqliteConnection("Data Source=:memory:");

        var r = XrefQuery.Find(noXref, "SalesLine", null, 10);

        Assert.False((bool)r["ok"]!);
        Assert.Equal("XREFDB_UNAVAILABLE", (string?)r["error"]);
    }

    [Theory]
    [InlineData("/Classes/SalesLineType/Methods/insert", "/Classes/SalesLineType", "insert")]
    [InlineData("/Forms/SalesTable/DataSources/SalesLine/Methods/active", "/Forms/SalesTable", "active")]
    [InlineData("/Classes/SalesLineType", "/Classes/SalesLineType", null)]
    public void A_source_path_folds_to_its_object(string source, string obj, string? member)
    {
        Assert.Equal(obj, XrefQuery.ObjectOf(source, out var m));
        Assert.Equal(member, m);
    }

    // ---- connection string ----

    [Fact]
    public void Encrypt_defaults_to_optional_like_the_bridge_client()
    {
        var cs = new SqlConnectionStringBuilder(XrefReferences.Normalize(XrefQuery.DefaultConnectionString));
        Assert.Equal(SqlConnectionEncryptOption.Optional, cs.Encrypt);
        Assert.True(cs.IntegratedSecurity);
    }

    [Fact]
    public void An_explicit_encrypt_setting_is_kept()
    {
        var cs = new SqlConnectionStringBuilder(
            XrefReferences.Normalize("Server=xref;Database=DYNAMICSXREFDB;Encrypt=Strict"));
        Assert.Equal(SqlConnectionEncryptOption.Strict, cs.Encrypt);
    }

    // ---- orchestration ----

    private static JsonObject Rows(bool truncated = false, params (string Source, string Kind, string Module)[] rows)
    {
        var items = new JsonArray();
        foreach (var (src, kind, module) in rows)
            items.Add(new JsonObject
            {
                ["source"] = src, ["target"] = "/Tables/SalesLine", ["kind"] = kind,
                ["kindId"] = XrefQuery.KindId(kind), ["line"] = 10, ["column"] = 5, ["module"] = module,
            });
        return new JsonObject
        {
            ["ok"] = true, ["symbol"] = "SalesLine", ["kindFilter"] = "", ["count"] = items.Count,
            ["truncated"] = truncated, ["source"] = "xrefdb", ["scoped"] = false, ["items"] = items,
        };
    }

    private static readonly (string, string, string)[] Sample =
    [
        ("/Classes/SalesLineType/Methods/insert", "Type", "ApplicationSuite"),
        ("/Classes/SalesLineType/Methods/insert", "Read", "ApplicationSuite"),
        ("/Classes/SalesLineType/Methods/validateWrite", "Read", "ApplicationSuite"),
        ("/Forms/SalesTable/DataSources/SalesLine/Methods/active", "Call", "ApplicationSuite"),
        ("/Classes/MyExt_SalesLine_Extension", "Extends", "MyModule"),
    ];

    private static JsonObject Data(ToolResult<object> r) => Assert.IsType<JsonObject>(r.Data);

    [Fact]
    public void Names_only_folds_references_into_one_row_per_object()
    {
        var r = XrefReferences.Find("SalesLine", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (Rows(false, Sample), null), bridge: null);

        Assert.True(r.Ok);
        var data = Data(r);
        Assert.Equal("names", (string?)data["mode"]);
        Assert.Equal(3, (int)data["count"]!);
        Assert.Equal(5, (int)data["references"]!);
        Assert.False((bool)data["truncated"]!);

        var first = data["items"]![0]!.AsObject();
        Assert.Equal("/Classes/SalesLineType", (string?)first["source"]);
        Assert.Equal(new[] { "Type", "Read" }, first["kinds"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(new[] { "insert", "validateWrite" }, first["members"]!.AsArray().Select(k => (string)k!));
        Assert.Equal(3, (int)first["refs"]!);
        // Line, column and target are what --lines is for.
        Assert.Null(first["line"]);
        Assert.Null(first["target"]);
        // A reference from the object itself has no member to list.
        Assert.Null(data["items"]![2]!["members"]);
    }

    [Fact]
    public void Names_only_reads_more_rows_than_objects_it_returns()
    {
        int asked = 0;
        XrefReferences.Find("SalesLine", null, null, 10, lines: 0,
            direct: (_, _, limit, _) => { asked = limit; return (Rows(false, Sample), null); }, bridge: null);

        Assert.True(asked > 10);
    }

    [Fact]
    public void Names_only_caps_objects_and_says_it_did()
    {
        var r = XrefReferences.Find("SalesLine", null, null, 2, lines: 0,
            direct: (_, _, _, _) => (Rows(false, Sample), null), bridge: null);

        var data = Data(r);
        Assert.Equal(2, (int)data["count"]!);
        Assert.True((bool)data["truncated"]!);
    }

    [Fact]
    public void Lines_returns_one_row_per_reference_read_with_the_limit_as_given()
    {
        int asked = 0;
        var r = XrefReferences.Find("SalesLine", null, null, 50, lines: 1,
            direct: (_, _, limit, _) => { asked = limit; return (Rows(false, Sample), null); }, bridge: null);

        var data = Data(r);
        Assert.Equal(50, asked);
        Assert.Equal("references", (string?)data["mode"]);
        Assert.Equal(5, data["items"]!.AsArray().Count);
        Assert.Equal(10, (int)data["items"]![0]!["line"]!);
        Assert.Equal("xrefdb", (string?)data["_source"]);
        Assert.Null(data["ok"]);
        // Provenance first, rows last: a truncated read of a long answer still names its store.
        Assert.Equal("_source", data.First().Key);
        Assert.Equal("items", data.Last().Key);
    }

    [Fact]
    public void The_bridge_answers_when_the_direct_query_cannot()
    {
        var r = XrefReferences.Find("SalesLine", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (null, "SqlException: no route"),
            bridge: (_, _, _, _) => (Rows(false, Sample), null));

        Assert.True(r.Ok);
        Assert.Equal("xrefdb-bridge", (string?)Data(r)["_source"]);
    }

    [Fact]
    public void Both_failures_are_reported_when_neither_can_answer()
    {
        var r = XrefReferences.Find("SalesLine", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (null, "SqlException: no route"),
            bridge: (_, _, _, _) => (null, "bridge error: timeout"));

        Assert.False(r.Ok);
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, r.Error!.Code);
        Assert.Contains("no route", r.Error.Message);
        Assert.Contains("timeout", r.Error.Message);
    }

    [Fact]
    public void A_model_filter_is_rejected_rather_than_dropped()
    {
        var r = XrefReferences.Find("SalesLine", null, "Foundation", 200, 0,
            direct: (_, _, _, _) => throw new InvalidOperationException("must not query"), bridge: null);

        Assert.Equal(D365FoErrorCodes.BadInput, r.Error!.Code);
    }

    [Theory]
    [InlineData("class")]
    [InlineData("table")]
    public void An_artifact_kind_is_rejected_where_a_reference_kind_is_meant(string kind)
    {
        var r = XrefReferences.Find("SalesLine", kind, null, 200, 0,
            direct: (_, _, _, _) => throw new InvalidOperationException("must not query"), bridge: null);

        Assert.Equal(D365FoErrorCodes.BadInput, r.Error!.Code);
        Assert.Contains("Call", r.Error.Hint);
    }

    [Fact]
    public void A_reference_kind_reaches_the_query_in_its_canonical_spelling()
    {
        string? asked = null;
        XrefReferences.Find("SalesLine", "call", null, 200, 0,
            direct: (_, kind, _, _) => { asked = kind; return (Rows(false), null); }, bridge: null);

        Assert.Equal("Call", asked);
    }
}
