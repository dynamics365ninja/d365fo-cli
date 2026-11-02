using System.Text.Json.Nodes;
using D365FO.Cli.Commands.Find;
using D365FO.Core;
using D365FO.Core.Bridge;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>find refs --xref</c> when the cross-reference database has never seen the module that
/// declares the symbol, which is the normal state of a custom model on a developer machine.
/// </summary>
/// <remarks>
/// Fakes stand in for the bridge query, the index lookup and the coverage probe, so each case is
/// the decision alone.
/// </remarks>
public class FindRefsXrefCoverageTests
{
    private static readonly XrefCoverage.Owner Custom = new("CCTPM", "CCTPM");

    private static FindRefsCommand.Settings Settings(string name = "DynTpmDealTable") =>
        new() { Name = name, Xref = true, Output = "json" };

    private static JsonObject Answer(int count)
    {
        var items = new JsonArray();
        for (var i = 0; i < count; i++)
            items.Add(new JsonObject { ["source"] = $"/Classes/Caller{i}", ["module"] = "ApplicationSuite" });
        return new JsonObject { ["ok"] = true, ["count"] = count, ["items"] = items };
    }

    [Fact]
    public void An_empty_answer_over_a_module_with_no_rows_is_refused_with_the_build_that_fills_it()
    {
        var result = FindRefsCommand.FindViaXref(Settings(),
            query: (_, _, _) => (Answer(0), null),
            ownerOf: _ => Custom,
            moduleHasRows: _ => (false, null));

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, result.Error!.Code);
        Assert.Contains("0 rows for module CCTPM", result.Error.Message);
        Assert.Contains("--xref", result.Error.Hint);
    }

    [Fact]
    public void A_failed_query_over_an_uncovered_module_names_the_cause_and_keeps_the_transport_error()
    {
        var result = FindRefsCommand.FindViaXref(Settings(),
            query: (_, _, _) => (null, "bridge error: Bridge did not respond within 60s (method: findReferences)."),
            ownerOf: _ => Custom,
            moduleHasRows: _ => (false, null));

        Assert.Equal(D365FoErrorCodes.XrefUnavailable, result.Error!.Code);
        Assert.Contains("0 rows for module CCTPM", result.Error.Message);
        Assert.Contains("did not respond", result.Error.Message);
    }

    [Fact]
    public void A_failed_query_whose_coverage_is_unknown_keeps_the_generic_refusal()
    {
        var result = FindRefsCommand.FindViaXref(Settings(),
            query: (_, _, _) => (null, "the metadata bridge is disabled"),
            ownerOf: _ => Custom,
            moduleHasRows: _ => (null, "the metadata bridge is disabled"));

        Assert.Equal(D365FoErrorCodes.XrefUnavailable, result.Error!.Code);
        Assert.Contains("could not be queried", result.Error.Message);
    }

    [Fact]
    public void An_answer_from_a_covered_module_comes_back_unchanged()
    {
        var result = FindRefsCommand.FindViaXref(Settings("CustTable"),
            query: (_, _, _) => (Answer(0), null),
            ownerOf: _ => new XrefCoverage.Owner("ApplicationSuite", "ApplicationSuite"),
            moduleHasRows: _ => (true, null));

        Assert.True(result.Ok);
        Assert.Null(result.Warnings);
    }

    [Fact]
    public void Rows_from_other_modules_are_kept_with_a_warning_about_the_missing_one()
    {
        var result = FindRefsCommand.FindViaXref(Settings(),
            query: (_, _, _) => (Answer(2), null),
            ownerOf: _ => Custom,
            moduleHasRows: _ => (false, null));

        Assert.True(result.Ok);
        Assert.Contains(result.Warnings!, w => w.Contains("module CCTPM") && w.Contains("--xref"));
    }

    [Fact]
    public void A_symbol_the_index_does_not_know_skips_the_coverage_probe()
    {
        var probed = false;
        var result = FindRefsCommand.FindViaXref(Settings("Unknown"),
            query: (_, _, _) => (Answer(0), null),
            ownerOf: _ => null,
            moduleHasRows: _ => { probed = true; return (false, null); });

        Assert.True(result.Ok);
        Assert.False(probed);
    }

    [Fact]
    public void An_unresolved_package_is_unknown_coverage_so_an_empty_answer_stays_an_answer()
    {
        var probed = false;
        var result = FindRefsCommand.FindViaXref(Settings(),
            query: (_, _, _) => (Answer(0), null),
            ownerOf: _ => new XrefCoverage.Owner("CCTPM", null),
            moduleHasRows: _ => { probed = true; return (false, null); });

        Assert.True(result.Ok);
        Assert.False(probed);
        Assert.Contains(result.Warnings!, w => w.Contains("model CCTPM") && w.Contains("--xref"));
    }
}
