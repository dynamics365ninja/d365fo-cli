using System.Text.Json;
using D365FO.Core.Guardrails;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The grounding gate proves references against the index, and an object the same command is
/// about to write is not in the index yet.
/// </summary>
/// <remarks>
/// <c>generate workflow</c> writes a WorkflowDocument whose <c>getQueryName()</c> returns
/// <c>queryStr(XQuery)</c>, and writes <c>XQuery</c> in the same run. The gate judged the
/// document alone and warned that the query was unknown, on every workflow scaffold;
/// <c>sysoperation</c>, <c>business-event</c> and <c>number-sequence</c> did the same with their
/// own companions. Under <c>D365FO_GROUNDING_ENFORCE=true</c> the same finding refused the write.
/// </remarks>
[Collection(EnvironmentCollectionDefinition.Name)]
public class GroundingGeneratedObjectsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-gate-gen-{Guid.NewGuid():N}.sqlite");
    private readonly string? _enforceBefore = Environment.GetEnvironmentVariable(ProvenanceStore.EnforceEnvVar);
    private readonly MetadataRepository _repo;

    public GroundingGeneratedObjectsTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        // The document's base class exists, so the query is the only thing the index cannot prove.
        _repo.ApplyExtract(ExtractBatch.Empty("ApplicationFoundation") with
        {
            Classes = new[] { new ExtractedClass("WorkflowDocument", null, true, false, null, Array.Empty<ExtractedMethod>()) },
        });
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
        GC.SuppressFinalize(this);
    }

    private static JsonElement Grounding(GroundingGate.GateResult gate) =>
        JsonDocument.Parse(D365Json.Serialize(gate.Grounding)).RootElement.Clone();

    [Fact]
    public void A_query_the_same_command_writes_is_not_reported_as_unknown()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, null);
        var doc = WorkflowScaffolder.WorkflowDocument("ContosoNoteWorkflowDocument", "ContosoNoteWorkflowQuery");

        var alone = GroundingGate.Check(null, "ContosoNoteWorkflow", doc, repository: _repo);
        Assert.Contains(alone.Warnings, w => w.Contains("queryStr(ContosoNoteWorkflowQuery)"));
        Assert.Equal(1, Grounding(alone).GetProperty("referenceErrors").GetInt32());

        var withCompanions = GroundingGate.Check(null, "ContosoNoteWorkflow", doc, repository: _repo,
            generatedObjects: ["ContosoNoteWorkflowDocument", "ContosoNoteWorkflowQuery"]);
        Assert.DoesNotContain(withCompanions.Warnings, w => w.StartsWith("grounding:"));
        Assert.Equal(0, Grounding(withCompanions).GetProperty("referenceErrors").GetInt32());
    }

    [Fact]
    public void Under_enforcement_the_scaffold_is_not_refused_for_its_own_companion()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, "true");
        var doc = WorkflowScaffolder.WorkflowDocument("ContosoNoteWorkflowDocument", "ContosoNoteWorkflowQuery");
        var token = ProvenanceStore.CreateToken(new ProvenanceContext("create", "ContosoNoteWorkflow"));

        var alone = GroundingGate.Check(token, "ContosoNoteWorkflow", doc, repository: _repo);
        Assert.Equal("VALIDATION_FAILED", alone.Failure?.Error?.Code);

        var withCompanions = GroundingGate.Check(token, "ContosoNoteWorkflow", doc, repository: _repo,
            generatedObjects: ["ContosoNoteWorkflowQuery"]);
        Assert.Null(withCompanions.Failure);
    }

    [Fact]
    public void A_name_the_command_does_not_write_is_still_reported()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, null);
        var doc = WorkflowScaffolder.WorkflowDocument("ContosoNoteWorkflowDocument", "ContosoTypoQuery");

        var gate = GroundingGate.Check(null, "ContosoNoteWorkflow", doc, repository: _repo,
            generatedObjects: ["ContosoNoteWorkflowQuery"]);
        Assert.Contains(gate.Warnings, w => w.Contains("queryStr(ContosoTypoQuery)"));
    }

    [Theory]
    [InlineData("unknown-type", "ContosoOpContract", true)]
    [InlineData("unknown-intrinsic-target", "classStr(ContosoOpContract)", true)]
    [InlineData("unknown-intrinsic-target", "fieldStr(ContosoOpContract, Amount)", true)]
    [InlineData("unknown-intrinsic-target", "tableStr(CustTabel)", false)]
    [InlineData("unknown-method", "ContosoOpContract", false)]   // only "not in the index" findings
    public void Only_findings_about_a_generated_name_are_dropped(string kind, string identifier, bool dropped)
    {
        var v = new ReferenceViolation(kind, "error", 1, identifier, "detail");
        var generated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ContosoOpContract" };
        Assert.Equal(dropped, GroundingGate.NamesGeneratedObject(v, generated));
    }

    [Fact]
    public void A_required_symbol_the_command_writes_counts_as_proven()
    {
        Environment.SetEnvironmentVariable(ProvenanceStore.EnforceEnvVar, null);

        var gate = GroundingGate.Check(null, "ContosoNumNum", doc: null, requiredSymbols: ["ContosoNumNum"],
            repository: _repo, generatedObjects: ["ContosoNumNum"]);
        Assert.Equal(0, Grounding(gate).GetProperty("referenceErrors").GetInt32());
    }
}
