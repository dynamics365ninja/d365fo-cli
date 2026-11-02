using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using D365FO.Core.Xref;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The xref where-used (<c>find refs --xref</c>, MCP <c>find_references xref:true</c>) when the
/// cross-reference database has never seen the module that declares the symbol, which is the
/// normal state of a custom model on a developer machine.
/// </summary>
/// <remarks>
/// Fakes stand in for the direct query, the bridge fallback, the index lookup and the coverage
/// probe, so each case is the decision alone.
/// </remarks>
public class XrefReferencesCoverageTests
{
    private static readonly XrefCoverage.Owner Custom = new("CCTPM", "CCTPM");

    private static JsonObject Answer(int count)
    {
        var items = new JsonArray();
        for (var i = 0; i < count; i++)
            items.Add(new JsonObject { ["source"] = $"/Classes/Caller{i}", ["kind"] = "Call", ["module"] = "ApplicationSuite", ["refs"] = 1 });
        return new JsonObject { ["ok"] = true, ["symbol"] = "DynTpmDealTable", ["count"] = count, ["items"] = items };
    }

    private static ToolResult<object> Find(
        XrefSource direct, XrefSource? bridge, XrefCoverage.Owner? owner, (bool?, string?) coverage,
        Action? probed = null, int lines = 0, string symbol = "DynTpmDealTable") =>
        XrefReferences.Find(symbol, null, null, 200, lines, direct, bridge,
            ownerOf: _ => owner,
            moduleHasRows: _ => { probed?.Invoke(); return coverage; });

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void An_empty_answer_over_a_module_with_no_rows_is_refused_with_the_build_that_fills_it(int lines)
    {
        var r = Find((_, _, _, _) => (Answer(0), null), null, Custom, (false, null), lines: lines);

        Assert.False(r.Ok);
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, r.Error!.Code);
        Assert.Contains("0 rows for module CCTPM", r.Error.Message);
        Assert.Contains("d365fo build --model CCTPM --xref", r.Error.Hint);
    }

    [Fact]
    public void The_bridge_fallback_answer_is_judged_the_same_way()
    {
        var r = Find((_, _, _, _) => (null, "SqlException: no route"), (_, _, _, _) => (Answer(0), null), Custom, (false, null));

        Assert.False(r.Ok);
        Assert.Contains("0 rows for module CCTPM", r.Error!.Message);
    }

    [Fact]
    public void A_failed_query_over_an_uncovered_module_names_the_cause_and_keeps_both_errors()
    {
        var r = Find((_, _, _, _) => (null, "SqlException: timeout"), (_, _, _, _) => (null, "bridge error: did not respond"),
            Custom, (false, null));

        Assert.Equal(D365FoErrorCodes.XrefUnavailable, r.Error!.Code);
        Assert.Contains("0 rows for module CCTPM", r.Error.Message);
        Assert.Contains("SqlException: timeout", r.Error.Message);
        Assert.Contains("did not respond", r.Error.Message);
    }

    [Fact]
    public void A_failed_query_whose_coverage_is_unknown_keeps_the_generic_refusal()
    {
        var r = Find((_, _, _, _) => (null, "SqlException: no route"), null, Custom, (null, "no route"));

        Assert.Equal(D365FoErrorCodes.XrefUnavailable, r.Error!.Code);
        Assert.Contains("could not be queried", r.Error.Message);
    }

    [Fact]
    public void An_answer_from_a_covered_module_comes_back_unchanged()
    {
        var r = Find((_, _, _, _) => (Answer(0), null), null, new XrefCoverage.Owner("ApplicationSuite", "ApplicationSuite"), (true, null),
            symbol: "CustTable");

        Assert.True(r.Ok);
        Assert.Null(r.Warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Rows_from_other_modules_are_kept_with_a_warning_about_the_missing_one(int lines)
    {
        var r = Find((_, _, _, _) => (Answer(2), null), null, Custom, (false, null), lines: lines);

        Assert.True(r.Ok);
        Assert.Contains(r.Warnings!, w => w.Contains("module CCTPM") && w.Contains("--xref"));
    }

    [Fact]
    public void A_symbol_the_index_does_not_know_skips_the_coverage_probe()
    {
        var probed = false;
        var r = Find((_, _, _, _) => (Answer(0), null), null, null, (false, null), () => probed = true, symbol: "Unknown");

        Assert.True(r.Ok);
        Assert.False(probed);
    }

    [Fact]
    public void An_unresolved_package_is_unknown_coverage_so_an_empty_answer_stays_an_answer()
    {
        var probed = false;
        var r = Find((_, _, _, _) => (Answer(0), null), null, new XrefCoverage.Owner("CCTPM", null), (false, null), () => probed = true);

        Assert.True(r.Ok);
        Assert.False(probed);
        Assert.Contains(r.Warnings!, w => w.Contains("model CCTPM") && w.Contains("d365fo build --model CCTPM --xref"));
    }

    [Fact]
    public void An_index_that_cannot_be_read_skips_the_check_never_the_query()
    {
        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, 0,
            (_, _, _, _) => (Answer(0), null), null,
            ownerOf: _ => throw new InvalidOperationException("no index"),
            moduleHasRows: _ => (false, null));

        Assert.True(r.Ok);
        Assert.Null(r.Warnings);
    }

    [Fact]
    public void Without_an_owner_lookup_the_answer_is_as_before()
    {
        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, 0,
            (_, _, _, _) => (Answer(0), null), null);

        Assert.True(r.Ok);
        Assert.Null(r.Warnings);
    }
}
