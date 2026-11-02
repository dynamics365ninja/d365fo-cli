using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// <c>build --xref</c> writes the cross-reference database that <c>find refs --xref</c> reads,
/// and the reader says when that database has never seen the symbol's module.
/// </summary>
/// <remarks>
/// On a developer machine nothing fills DYNAMICSXREFDB for a custom model: the CLI's own xppc
/// call passed no cross-reference switches, and an xref query about a custom symbol came back
/// empty or timed out with nothing to say why.
/// </remarks>
public class XrefBuildAndCoverageTests
{
    private static readonly XppModelBuild.Target Model =
        new("ConFleet", "ConFleet", Path.Combine(Path.GetTempPath(), "Metadata"), null);

    private static readonly string Packages = Path.Combine(Path.GetTempPath(), "PackagesLocalDirectory");

    // ---- build --xref: the switches ---------------------------------------------------------

    [Fact]
    public void An_xref_build_passes_the_switches_xppc_documents_for_the_reader_database()
    {
        var (xref, problem) = XppModelBuild.ReadXrefTarget(
            @"Server=(LocalDB)\MSSQLLocalDB;Database=XRef_dev01;Integrated Security=true;TrustServerCertificate=true");
        Assert.Null(problem);

        var args = XppModelBuild.XppcArgs(Model, Packages, incremental: false, xref);

        Assert.Equal(["-xref", @"-xrefSqlServer=(LocalDB)\MSSQLLocalDB", "-xrefDbName=XRef_dev01"], args.TakeLast(3));
        Assert.DoesNotContain("-norecreatecrossrefindexes", args);
    }

    [Fact]
    public void A_build_without_xref_leaves_the_database_alone()
    {
        var args = XppModelBuild.XppcArgs(Model, Packages, incremental: true);

        Assert.DoesNotContain(args, a => a.StartsWith("-xref", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Only_the_last_model_of_an_xref_build_rebuilds_the_cross_reference_indexes()
    {
        var (xref, _) = XppModelBuild.ReadXrefTarget(null);

        var first = XppModelBuild.XppcArgs(Model, Packages, incremental: false, xref, lastXrefModel: false);
        var last = XppModelBuild.XppcArgs(Model, Packages, incremental: false, xref, lastXrefModel: true);

        Assert.Contains("-norecreatecrossrefindexes", first);
        Assert.DoesNotContain("-norecreatecrossrefindexes", last);
    }

    [Theory]
    [InlineData(3, 2, 2, null)]   // the last model ran, without the switch
    [InlineData(3, 2, 1, null)]   // the last model ran and failed: it ran without the switch too
    [InlineData(1, 0, -1, null)]  // a single model never defers
    [InlineData(3, -1, -1, null)] // LabelC failed first: xppc never ran
    [InlineData(3, 1, 0, 0)]      // stopped at the second: repeat the first, which compiled
    [InlineData(3, 0, -1, 0)]     // the first failed: it is the only candidate
    [InlineData(3, 1, 1, 1)]      // LabelC failed on the third after the second compiled
    public void A_build_that_stops_before_its_last_model_still_recreates_the_cross_reference_indexes(
        int count, int lastRun, int lastSucceeded, int? expected)
        => Assert.Equal(expected, XppModelBuild.XrefIndexRecreateTarget(count, lastRun, lastSucceeded));

    [Fact]
    public void The_index_recreate_run_is_xref_only_without_the_deferral_and_keeps_the_build_logs()
    {
        var (xref, _) = XppModelBuild.ReadXrefTarget(null);

        var args = XppModelBuild.XrefIndexRecreateArgs(Model, Packages, xref!);

        Assert.Contains("-xref", args);
        Assert.Contains("-xrefonly", args);
        Assert.DoesNotContain("-norecreatecrossrefindexes", args);
        Assert.DoesNotContain("-incremental", args);
        Assert.Single(args, a => a.StartsWith("-log=", StringComparison.Ordinal));
        Assert.Contains(args, a => a.StartsWith("-log=", StringComparison.Ordinal) && a.EndsWith("BuildModelResult.xrefindexes.log", StringComparison.Ordinal));
        Assert.DoesNotContain(args, a => a.EndsWith("BuildModelResult.log", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_connection_string_xppc_keeps_its_own_default_database()
    {
        var (xref, problem) = XppModelBuild.ReadXrefTarget("   ");

        Assert.Null(problem);
        Assert.Null(xref!.Server);
        Assert.Null(xref.Database);
        Assert.Equal("-xref", XppModelBuild.XppcArgs(Model, Packages, false, xref)[^1]);
    }

    [Theory]
    [InlineData("Data Source=sqlhost;Initial Catalog=XrefDb;Integrated Security=SSPI", "sqlhost", "XrefDb")]
    [InlineData("server=.;database=DYNAMICSXREFDB", ".", "DYNAMICSXREFDB")]
    public void The_server_and_database_are_read_under_any_of_their_spellings(string cs, string server, string db)
    {
        var (xref, _) = XppModelBuild.ReadXrefTarget(cs);

        Assert.Equal(server, xref!.Server);
        Assert.Equal(db, xref.Database);
    }

    [Fact]
    public void A_sql_login_is_refused_because_xppc_cannot_pass_it()
    {
        var (xref, problem) = XppModelBuild.ReadXrefTarget("Server=sqlhost;Database=XrefDb;User ID=sa;Password=x");

        Assert.Null(xref);
        Assert.Contains("Windows authentication", problem);
    }

    [Fact]
    public void An_xref_build_forced_onto_msbuild_is_refused_rather_than_dropped()
    {
        var result = SdlcRunner.Build(null, "Tool.csproj", engine: "msbuild", xref: true);

        Assert.False(result.Ok);
        Assert.Equal("BAD_INPUT", result.Error!.Code);
        Assert.Contains("--xref", result.Error.Message);
    }

    // ---- coverage: which module, and what an answer means ------------------------------------

    [Theory]
    [InlineData("DynTpmDealTable", "DynTpmDealTable")]
    [InlineData("DynTpmDealTable.initValue", "DynTpmDealTable")]
    [InlineData("/Tables/DynTpmDealTable/Methods/initValue", "DynTpmDealTable")]
    [InlineData("@DynTpm:DealId", null)]
    [InlineData("/Labels/@DynTpm:DealId", null)]
    public void The_owner_of_a_symbol_is_the_type_it_names(string symbol, string? owner)
        => Assert.Equal(owner, XrefCoverage.OwnerName(symbol));

    [Fact]
    public void The_module_is_the_package_folder_above_the_model()
    {
        var path = Path.Combine("C:", "repo", "Metadata", "CCTPMPackage", "CCTPM", "AxClass", "DynTpmX.xml");

        Assert.Equal("CCTPMPackage", XrefCoverage.PackageOf(path));
        Assert.Null(XrefCoverage.PackageOf(Path.Combine("C:", "somewhere", "DynTpmX.xml")));
    }

    private static readonly XrefCoverage.Owner Custom = new("CCTPM", "CCTPM");

    [Fact]
    public void An_empty_answer_for_a_module_with_no_rows_is_refused_and_names_the_build_that_fills_it()
    {
        var verdict = XrefCoverage.Assess(0, "DynTpmDealTable", Custom, moduleHasRows: false);

        var error = verdict.Failure!.Error!;
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, error.Code);
        Assert.Contains("0 rows for module CCTPM", error.Message);
        Assert.Contains("d365fo build --model CCTPM --xref", error.Hint);
    }

    [Fact]
    public void An_answer_with_rows_from_elsewhere_is_kept_with_a_warning()
    {
        var verdict = XrefCoverage.Assess(3, "DynTpmDealTable", Custom, moduleHasRows: false);

        Assert.Null(verdict.Failure);
        Assert.Contains("references from inside that module are missing", verdict.Warning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void A_covered_or_unknown_module_leaves_the_answer_as_it_is(bool? hasRows)
    {
        var verdict = XrefCoverage.Assess(0, "CustTable", new XrefCoverage.Owner("ApplicationSuite", "ApplicationSuite"), hasRows);

        Assert.Null(verdict.Failure);
        Assert.Null(verdict.Warning);
    }

    [Fact]
    public void A_model_whose_package_the_index_cannot_name_has_unknown_coverage_not_none()
    {
        var db = Path.Combine(Path.GetTempPath(), $"xref-owner-{Guid.NewGuid():N}.sqlite");
        try
        {
            var repo = new D365FO.Core.Index.MetadataRepository(db);
            repo.EnsureSchema();
            repo.ApplyExtract(new D365FO.Core.Index.ExtractBatch(
                Model: "CCTPM", Publisher: "Test", Layer: "cus", IsCustom: true,
                Tables: [], Classes: [],
                Edts: [new D365FO.Core.Index.ExtractedEdt("DynTpmDealId", null, "String", null, 20)],
                Enums: [], MenuItems: [], CocExtensions: [], Labels: []));

            var owner = XrefCoverage.OwnerOf("DynTpmDealId", repo);

            Assert.Equal("CCTPM", owner!.Model);
            Assert.Null(owner.Module);
            var verdict = XrefCoverage.Assess(0, "DynTpmDealId", owner, moduleHasRows: false);
            Assert.Null(verdict.Failure);
            Assert.Contains("d365fo build --model CCTPM --xref", verdict.Warning);
            Assert.Null(XrefCoverage.Assess(4, "DynTpmDealId", owner, moduleHasRows: null).Warning);
        }
        finally
        {
            SqlitePool.ReleaseFor(db);
            foreach (var ext in new[] { "", "-wal", "-shm" })
                try { File.Delete(db + ext); } catch { }
        }
    }

    [Fact]
    public void A_coverage_reply_is_read_for_the_module_asked_about()
    {
        var reply = new JsonObject
        {
            ["ok"] = true,
            ["modules"] = new JsonArray(
                new JsonObject { ["module"] = "ApplicationSuite", ["hasRows"] = true },
                new JsonObject { ["module"] = "CCTPM", ["hasRows"] = false }),
        };

        Assert.Equal(((bool?)false, (string?)null), BridgeGate.ReadCoverage(reply, "cctpm"));
        Assert.Null(BridgeGate.ReadCoverage(new JsonObject { ["ok"] = false, ["error"] = "XREFDB_UNAVAILABLE" }, "CCTPM").HasRows);
    }

    [Fact]
    public void An_xref_query_gets_more_time_than_the_database_gives_it()
    {
        // The bridge allows the database a 15 s connect and a 30 s command; a client budget below
        // that turns every slow query into an anonymous timeout.
        Assert.True(BridgeGate.XrefRequestTimeout > TimeSpan.FromSeconds(45));
        Assert.Equal(BridgeGate.XrefRequestTimeout, BridgeGate.DefaultOptions(BridgeGate.XrefRequestTimeout).RequestTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), BridgeGate.DefaultOptions().RequestTimeout);
    }
}
