using D365FO.Core.Guardrails;
using Xunit;

namespace D365FO.Core.Tests;

public class ProvenanceStoreTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"d365fo-home-{Guid.NewGuid():N}");
    private readonly string? _prevHome;

    public ProvenanceStoreTests()
    {
        _prevHome = Environment.GetEnvironmentVariable("D365FO_HOME");
        Environment.SetEnvironmentVariable("D365FO_HOME", _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_HOME", _prevHome);
        try { Directory.Delete(_home, recursive: true); } catch { }
    }

    [Fact]
    public void Token_roundtrips_across_store_instances()
    {
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable", "validateWrite", "table"));
        var bundle = ProvenanceStore.TryGet(token);
        Assert.NotNull(bundle);
        Assert.Equal("CustTable", bundle!.Context.ObjectName);
        Assert.True(bundle.ExpiresUtc > DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Validate_accepts_bound_object()
    {
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable"));
        var (ok, _) = ProvenanceStore.Validate(token, "CustTable");
        Assert.True(ok);
    }

    [Fact]
    public void Validate_accepts_derived_extension_name()
    {
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable", ProposedName: "CustTable_MyExt_Extension"));
        var (ok, _) = ProvenanceStore.Validate(token, "CustTable_MyExt_Extension");
        Assert.True(ok);
    }

    [Fact]
    public void Validate_rejects_object_that_merely_contains_the_bound_name()
    {
        // A token issued for the short name "Cust" must not become a wildcard over every
        // object with "Cust" somewhere inside it.
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "Cust"));
        var (ok, reason) = ProvenanceStore.Validate(token, "SalesCustTable");
        Assert.False(ok);
        Assert.Contains("bound", reason);
    }

    [Fact]
    public void Validate_rejects_shorter_object_the_bound_name_contains()
    {
        // The reverse direction: a token for CustTable does not authorize the unrelated "Cust".
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable"));
        var (ok, _) = ProvenanceStore.Validate(token, "Cust");
        Assert.False(ok);
    }

    [Fact]
    public void Validate_accepts_derived_name_without_a_proposed_name()
    {
        // Extensions/handlers are named by prefixing the base object; that stays authorized
        // even when prepare did not propose the exact name up front.
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable"));
        var (ok, _) = ProvenanceStore.Validate(token, "CustTableEventHandler");
        Assert.True(ok);
    }

    [Fact]
    public void Validate_rejects_other_object()
    {
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable"));
        var (ok, reason) = ProvenanceStore.Validate(token, "SalesLine");
        Assert.False(ok);
        Assert.Contains("bound", reason);
    }

    [Fact]
    public void Validate_rejects_missing_and_garbage_tokens()
    {
        Assert.False(ProvenanceStore.Validate(null, "CustTable").Ok);
        Assert.False(ProvenanceStore.Validate("", "CustTable").Ok);
        Assert.False(ProvenanceStore.Validate("deadbeefdeadbeefdeadbeefdeadbeef", "CustTable").Ok);
        Assert.False(ProvenanceStore.Validate("../../../etc/passwd", "CustTable").Ok);
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("goal", "CustTable"));
        var path = Path.Combine(_home, "provenance", token + ".json");
        // Rewrite the bundle with an expiry in the past.
        var json = File.ReadAllText(path);
        var expired = System.Text.RegularExpressions.Regex.Replace(json,
            "\"ExpiresUtc\":\"[^\"]+\"",
            $"\"ExpiresUtc\":\"{DateTimeOffset.UtcNow.AddMinutes(-1):O}\"");
        File.WriteAllText(path, expired);
        Assert.Null(ProvenanceStore.TryGet(token));
        Assert.False(ProvenanceStore.Validate(token, "CustTable").Ok);
    }
}
