using System.Text.Json.Nodes;
using D365FO.Core.Bridge;
using D365FO.Core.Xref;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// A cross-reference server that cannot be reached is reported after one connect timeout, not
/// three. The bridge fallback and the coverage probe read the same connection string, so against
/// a stopped LocalDB each waited out the same timeout again: about 74 s before the error.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class XrefConnectionFailureTests : IDisposable
{
    private readonly string? _saved = Environment.GetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING");

    public void Dispose() => Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING", _saved);

    private static readonly XrefCoverage.Owner Custom = new("CCTPM", "CCTPM");

    [Fact]
    public void An_unreachable_server_is_not_asked_again_through_the_bridge_or_the_coverage_probe()
    {
        var bridgeAsked = false;
        var probeAsked = false;

        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (null, XrefReferences.ServerUnreachable + "A network-related or instance-specific error occurred."),
            bridge: (_, _, _, _) => { bridgeAsked = true; return (null, "same server"); },
            ownerOf: _ => Custom,
            moduleHasRows: _ => { probeAsked = true; return (false, null); });

        Assert.False(r.Ok);
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, r.Error!.Code);
        Assert.False(bridgeAsked);
        Assert.False(probeAsked);
        Assert.Contains("Bridge fallback skipped", r.Error.Message);
    }

    [Fact]
    public void A_failure_the_server_answered_still_falls_back_to_the_bridge()
    {
        // A login or certificate failure is answered by a server, and the bridge's
        // System.Data.SqlClient can get past what Microsoft.Data.SqlClient refuses.
        var bridgeAsked = false;

        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (null, "XREFDB_UNAVAILABLE: SqlException: The certificate chain was issued by an authority that is not trusted."),
            bridge: (_, _, _, _) =>
            {
                bridgeAsked = true;
                return (new JsonObject { ["ok"] = true, ["count"] = 0, ["items"] = new JsonArray() }, null);
            });

        Assert.True(bridgeAsked);
        Assert.True(r.Ok);
    }

    [Fact]
    public void An_unreachable_LocalDB_gets_the_SqlLocalDB_steps_as_the_hint()
    {
        Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING",
            "Server=(LocalDB)\\MSSQLLocalDB;Database=XRef_dev;Integrated Security=true");

        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (null, XrefReferences.ServerUnreachable + "The server was not found."),
            bridge: null);

        Assert.Contains("SqlLocalDB stop MSSQLLocalDB", r.Error!.Hint);
    }

    [Theory]
    [InlineData("Server=(LocalDB)\\MSSQLLocalDB;Database=X;Integrated Security=true", "MSSQLLocalDB")]
    [InlineData("Data Source=(localdb)\\ProjectsV13;Initial Catalog=X", "ProjectsV13")]
    public void The_LocalDB_hint_names_the_instance(string connectionString, string instance)
    {
        var hint = XrefReferences.LocalDbHint(connectionString);

        Assert.Contains($"SqlLocalDB info {instance}", hint);
        Assert.Contains($"SqlLocalDB stop {instance}", hint);
    }

    [Theory]
    [InlineData("Server=.;Database=DYNAMICSXREFDB;Integrated Security=true")]
    [InlineData("Server=sql01;Database=X;Integrated Security=true")]
    [InlineData("not a connection string")]
    [InlineData(null)]
    public void A_server_that_is_not_LocalDB_gets_no_LocalDB_hint(string? connectionString) =>
        Assert.Null(XrefReferences.LocalDbHint(connectionString));

    [Theory]
    [InlineData(-2, 11, true)]          // connect timeout
    [InlineData(53, 20, true)]          // network path not found
    [InlineData(258, 20, true)]         // TCP wait timed out (measured against an unreachable host)
    [InlineData(-1983577849, 20, true)] // LocalDB instance does not exist (measured)
    [InlineData(18456, 14, false)]      // login failed: a server answered
    [InlineData(4060, 11, false)]       // cannot open database: a server answered
    public void Only_errors_where_no_server_answered_count_as_unreachable(int number, byte severity, bool unreachable) =>
        Assert.Equal(unreachable, XrefReferences.IsUnreachable(number, severity, "A network-related or instance-specific error occurred."));

    [Fact]
    public void A_handshake_failure_after_the_connection_was_established_is_not_unreachable() =>
        // The bridge's System.Data.SqlClient can get past a certificate check this client refuses.
        Assert.False(XrefReferences.IsUnreachable(-2146893019, 20,
            "A connection was successfully established with the server, but then an error occurred during the pre-login handshake."));

    [Fact]
    public void The_marker_does_not_reach_the_message()
    {
        var r = XrefReferences.Find("DynTpmDealTable", null, null, 200, lines: 0,
            direct: (_, _, _, _) => (null, XrefReferences.ServerUnreachable + "SqlException: The server was not found."),
            bridge: null);

        Assert.DoesNotContain(XrefReferences.ServerUnreachable, r.Error!.Message);
        Assert.Contains("SqlException: The server was not found.", r.Error.Message);
    }
}
