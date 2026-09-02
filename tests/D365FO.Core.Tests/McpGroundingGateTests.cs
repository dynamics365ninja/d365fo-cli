using System.Text.Json;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// <c>D365FO_GROUNDING_ENFORCE</c> has to mean the same thing on both front doors.
/// </summary>
/// <remarks>
/// It did not. The gate lived beside the CLI's generate commands, so a deployment that turned
/// enforcement on got a fail-closed shell surface and an MCP server that wrote whatever it was
/// asked for — the one surface a remote agent actually uses. Nothing reported the difference:
/// the write succeeded, which is exactly what an ungated write looks like.
/// </remarks>
[Collection(EnvironmentCollectionDefinition.Name)]
public class McpGroundingGateTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-gate-{Guid.NewGuid():N}.sqlite");
    private readonly string _outDir = Path.Combine(Path.GetTempPath(), $"d365fo-gate-out-{Guid.NewGuid():N}");
    private readonly string? _enforceBefore = Environment.GetEnvironmentVariable(ProvenanceStore.EnforceEnvVar);
    private readonly ToolHandlers _handlers;

    public McpGroundingGateTests()
    {
        Directory.CreateDirectory(_outDir);
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        _handlers = new ToolHandlers(repo);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, _enforceBefore);
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
        if (Directory.Exists(_outDir)) Directory.Delete(_outDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private JsonElement Generate(string objectType, string name)
    {
        var descriptor = ToolCatalog.All.Single(d => d.Name == "generate_object");
        var path = Path.Combine(_outDir, name + ".xml");
        using var args = JsonDocument.Parse(
            $$"""{"objectType":"{{objectType}}","name":"{{name}}","out":{{JsonSerializer.Serialize(path)}}}""");
        var result = descriptor.Invoke(_handlers, args.RootElement);
        return JsonDocument.Parse(D365Json.Serialize(result)).RootElement.Clone();
    }

    [Fact]
    public void Enforcement_off_writes_and_reports_what_the_gate_saw()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, null);

        var envelope = Generate("class", "ConFleetGateOff");

        Assert.True(envelope.GetProperty("ok").GetBoolean());
        var data = envelope.GetProperty("data");
        Assert.True(File.Exists(data.GetProperty("path").GetString()!));

        // The findings travel with the write rather than being discarded: an agent that cannot
        // see what was checked cannot tell a grounded write from an unchecked one.
        var grounding = data.GetProperty("grounding");
        Assert.False(grounding.GetProperty("enforced").GetBoolean());
        Assert.False(grounding.GetProperty("tokenSupplied").GetBoolean());
    }

    [Fact]
    public void Enforcement_on_refuses_a_write_with_no_token()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, "true");

        var envelope = Generate("class", "ConFleetGateOn");

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("GROUNDING_REQUIRED", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(Path.Combine(_outDir, "ConFleetGateOn.xml")),
            "the refusal has to happen before the write, or it is not a gate");
    }

    [Fact]
    public void Enforcement_on_lets_a_token_bound_to_the_object_through()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, "true");

        var token = ProvenanceStore.CreateToken(new ProvenanceContext("create", "ConFleetGateToken"));
        var descriptor = ToolCatalog.All.Single(d => d.Name == "generate_object");
        var path = Path.Combine(_outDir, "ConFleetGateToken.xml");
        using var args = JsonDocument.Parse(
            $$"""
              {"objectType":"class","name":"ConFleetGateToken",
               "out":{{JsonSerializer.Serialize(path)}},"groundingToken":{{JsonSerializer.Serialize(token)}}}
              """);

        var envelope = JsonDocument.Parse(D365Json.Serialize(descriptor.Invoke(_handlers, args.RootElement))).RootElement;

        Assert.True(envelope.GetProperty("ok").GetBoolean(),
            envelope.TryGetProperty("error", out var e) ? e.GetProperty("message").GetString() : "");
        Assert.True(envelope.GetProperty("data").GetProperty("grounding").GetProperty("tokenValid").GetBoolean());
        Assert.True(File.Exists(path));
    }

    /// <summary>A token issued for one object does not license a write to another.</summary>
    [Fact]
    public void Enforcement_on_refuses_a_token_issued_for_a_different_object()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, "true");

        var token = ProvenanceStore.CreateToken(new ProvenanceContext("create", "ConFleetSomethingElse"));
        var descriptor = ToolCatalog.All.Single(d => d.Name == "generate_object");
        var path = Path.Combine(_outDir, "ConFleetWrongToken.xml");
        using var args = JsonDocument.Parse(
            $$"""
              {"objectType":"class","name":"ConFleetWrongToken",
               "out":{{JsonSerializer.Serialize(path)}},"groundingToken":{{JsonSerializer.Serialize(token)}}}
              """);

        var envelope = JsonDocument.Parse(D365Json.Serialize(descriptor.Invoke(_handlers, args.RootElement))).RootElement;

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("GROUNDING_REQUIRED", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.False(File.Exists(path));
    }
}
