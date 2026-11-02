using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using D365FO.Core.Xref;
using Microsoft.Data.SqlClient;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// A bare name whose kind the local index knows is looked up by exact paths and prefixes, so the
/// query seeks <c>Names.Path</c>. The "anywhere" match it replaces ORs in leading-wildcard terms
/// (<c>%/X</c>, <c>%/X/%</c>) that scan every name: about 60 s on a 6.1M-name database, past the
/// 30 s command timeout, so <c>find refs DynTpmDealTable --xref</c> never answered.
/// </summary>
public class XrefBareNameLookupTests
{
    [Fact]
    public void A_bare_table_name_resolves_to_the_table_in_both_path_forms_with_no_leading_wildcard()
    {
        var p = XrefQuery.ResolveTargetPaths("DynTpmDealTable", ["table"]);

        Assert.Equal(new[] { "/Tables/DynTpmDealTable", "Table/DynTpmDealTable" }, p.Exact);
        Assert.Equal(new[] { "/Tables/DynTpmDealTable/%", "Table/DynTpmDealTable/%" }, p.Like);
        Assert.DoesNotContain(p.Like, l => l.StartsWith('%'));
        Assert.False(p.Scoped);
    }

    [Fact]
    public void A_name_that_is_a_table_and_a_form_seeks_both()
    {
        var p = XrefQuery.ResolveTargetPaths("DynTpmDealTable", ["table", "form"]);

        Assert.Equal(
            new[] { "/Tables/DynTpmDealTable", "Table/DynTpmDealTable", "/Forms/DynTpmDealTable", "Form/DynTpmDealTable" },
            p.Exact);
        Assert.Equal(4, p.Like.Count);
    }

    [Fact]
    public void An_edt_is_sought_under_every_edt_root_element_the_compiler_writes()
    {
        var p = XrefQuery.ResolveTargetPaths("DynTpmDealId", ["edt"]);

        Assert.Contains("/Edts/DynTpmDealId", p.Exact);
        Assert.Contains("EdtString/DynTpmDealId", p.Exact);
        Assert.Contains("EdtEnum/DynTpmDealId", p.Exact);
        Assert.Equal(11, p.Exact.Count);
        Assert.DoesNotContain(p.Like, l => l.StartsWith('%'));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("view")]
    public void A_bare_name_of_no_known_kind_keeps_the_anywhere_match(string? kind)
    {
        var p = XrefQuery.ResolveTargetPaths("SalesLine", kind is null ? null : [kind]);

        Assert.Equal(new[] { "/SalesLine" }, p.Exact);
        Assert.Contains("%/SalesLine", p.Like);
    }

    [Fact]
    public void Kinds_do_not_change_a_member_an_aot_path_or_a_label()
    {
        string[] kinds = ["table"];
        Assert.Equal(XrefQuery.ResolveTargetPaths("SalesTable.initValue").Exact,
            XrefQuery.ResolveTargetPaths("SalesTable.initValue", kinds).Exact);
        Assert.Equal(XrefQuery.ResolveTargetPaths("/Tables/SalesLine").Like,
            XrefQuery.ResolveTargetPaths("/Tables/SalesLine", kinds).Like);
        Assert.Equal(XrefQuery.ResolveTargetPaths("@SYS123").Exact,
            XrefQuery.ResolveTargetPaths("@SYS123", kinds).Exact);
    }

    [Fact]
    public void Like_wildcards_in_a_known_name_match_literally()
    {
        var p = XrefQuery.ResolveTargetPaths("Sales_Line", ["table"]);

        Assert.Contains("/Tables/Sales[_]Line/%", p.Like);
        Assert.Contains("/Tables/Sales_Line", p.Exact);
    }

    [Fact]
    public void The_query_for_a_known_kind_binds_no_leading_wildcard_pattern()
    {
        using var cmd = new SqlCommand();
        XrefQuery.Prepare(cmd, XrefQuery.ResolveTargetPaths("DynTpmDealTable", ["table"]), -1, 50, aggregate: true);

        var patterns = cmd.Parameters.Cast<SqlParameter>()
            .Where(p => p.ParameterName.StartsWith("@L", StringComparison.Ordinal))
            .Select(p => (string)p.Value!).ToList();
        Assert.Equal(2, patterns.Count);
        Assert.DoesNotContain(patterns, l => l.StartsWith('%'));
    }

    [Fact]
    public void The_index_kinds_of_the_symbol_reach_the_query_and_the_bridge_fallback()
    {
        IReadOnlyList<string>? toDirect = null, toBridge = null;
        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, lines: 0,
            direct: (_, _, _, _, kinds) => { toDirect = kinds; return (null, "SqlException: no route"); },
            bridge: (_, _, _, _, kinds) =>
            {
                toBridge = kinds;
                return (new JsonObject { ["ok"] = true, ["count"] = 0, ["match"] = "object", ["items"] = new JsonArray() }, null);
            },
            ownerOf: _ => new XrefCoverage.Owner("CCTPM", null, ["table", "form"]));

        Assert.Equal(new[] { "table", "form" }, toDirect);
        Assert.Equal(new[] { "table", "form" }, toBridge);
        Assert.Equal("object", (string?)Assert.IsType<JsonObject>(r.Data)["match"]);
    }

    [Fact]
    public void A_symbol_the_index_does_not_know_is_queried_without_kinds()
    {
        IReadOnlyList<string>? asked = ["sentinel"];
        XrefReferences.Find("Unknown", null, null, 200, lines: 0,
            direct: (_, _, _, _, kinds) => { asked = kinds; return (new JsonObject { ["ok"] = true, ["count"] = 0, ["items"] = new JsonArray() }, null); },
            bridge: null, ownerOf: _ => null);

        Assert.Null(asked);
    }

    [Fact]
    public void The_owner_lookup_reports_every_kind_the_name_has()
    {
        var db = Path.Combine(Path.GetTempPath(), $"xref-kinds-{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new MetadataRepository(db);
            repo.EnsureSchema();
            repo.ApplyExtract(new ExtractBatch(
                Model: "CCTPM", Publisher: "Test", Layer: "cus", IsCustom: true,
                Tables: [], Classes: [],
                Edts: [new ExtractedEdt("DynTpmDealId", null, "String", null, 20)],
                Enums: [], MenuItems: [], CocExtensions: [], Labels: []));

            var owner = XrefCoverage.OwnerOf("DynTpmDealId", repo);

            Assert.Equal(new[] { "edt" }, owner!.Kinds);
        }
        finally
        {
            SqlitePool.ReleaseFor(db);
            foreach (var ext in new[] { "", "-wal", "-shm" })
                try { File.Delete(db + ext); } catch { }
        }
    }
}
