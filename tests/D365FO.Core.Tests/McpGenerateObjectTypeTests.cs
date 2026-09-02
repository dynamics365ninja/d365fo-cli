using System.Text.Json;
using System.Xml.Linq;
using D365FO.Core.Index;
using D365FO.Core.Validation;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The object types <c>generate_object</c> gained, exercised through the real binder.
/// </summary>
/// <remarks>
/// The parity harness proves these are declared and dispatched; this proves what comes back is
/// an artefact rather than an envelope. Both are needed: the previous state of this tool was a
/// description that listed types the switch did not handle, and a switch that handled types the
/// description never mentioned.
/// </remarks>
public class McpGenerateObjectTypeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-gen-{Guid.NewGuid():N}.sqlite");
    private readonly ToolHandlers _handlers;

    public McpGenerateObjectTypeTests()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        _handlers = new ToolHandlers(repo);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
        GC.SuppressFinalize(this);
    }

    private JsonElement Generate(string arguments)
    {
        var descriptor = ToolCatalog.All.Single(d => d.Name == "generate_object");
        using var args = JsonDocument.Parse(arguments);
        var result = descriptor.Invoke(_handlers, args.RootElement);
        return JsonDocument.Parse(D365Json.Serialize(result)).RootElement.Clone();
    }

    private static void AssertOk(JsonElement envelope)
    {
        if (!envelope.GetProperty("ok").GetBoolean())
        {
            var error = envelope.GetProperty("error");
            Assert.Fail($"{error.GetProperty("code").GetString()}: {error.GetProperty("message").GetString()}");
        }
    }

    /// <summary>Every document a handler returns has to be one the AOT reader would accept.</summary>
    private static void AssertAotDocument(string xml)
    {
        var doc = XDocument.Parse(xml);
        Assert.NotNull(doc.Root);
        Assert.StartsWith("Ax", doc.Root!.Name.LocalName, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(
            doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "Name")?.Value),
            "the document carries no <Name>, so nothing can look it up");

        var violations = new List<XppViolation>();
        ContractShapeRules.Check(xml, violations);
        Assert.Empty(violations);
    }

    [Fact]
    public void View_projects_a_query()
    {
        var envelope = Generate("""
            {"objectType":"view","name":"ConFleetOpenView","query":"ConFleetQuery",
             "fields":["VehicleId:ConFleetVehicle","Status:ConFleetVehicle:VehicleStatus"],
             "computed":["DaysOpen:computeDaysOpen:Int"]}
            """);
        AssertOk(envelope);
        AssertAotDocument(envelope.GetProperty("data").GetProperty("xml").GetString()!);
    }

    [Fact]
    public void A_view_without_a_query_is_refused_rather_than_guessed()
    {
        var envelope = Generate("""{"objectType":"view","name":"ConFleetOpenView"}""");
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Contains("query", envelope.GetProperty("error").GetProperty("message").GetString(),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Map_carries_its_fields_and_its_mappings()
    {
        var envelope = Generate("""
            {"objectType":"map","name":"ConAddressMap","fields":["Street:Street","City:City"],
             "mapTo":["CustTable:Street=Street,City=City","VendTable"]}
            """);
        AssertOk(envelope);
        AssertAotDocument(envelope.GetProperty("data").GetProperty("xml").GetString()!);
    }

    [Fact]
    public void Systest_is_red_first()
    {
        var envelope = Generate("""
            {"objectType":"systest","name":"ConFleetPostingTest","targetClass":"ConFleetPosting",
             "methods":["post","validate"]}
            """);
        AssertOk(envelope);
        var xml = envelope.GetProperty("data").GetProperty("xml").GetString()!;
        AssertAotDocument(xml);
        Assert.Contains("this.fail(", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_service_returns_the_three_files_the_pattern_needs()
    {
        var envelope = Generate("""
            {"objectType":"custom-service","name":"ConFleetService",
             "operationSpecs":["reserve:boolean","release"]}
            """);
        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        foreach (var part in new[] { "serviceClass", "service", "serviceGroup" })
            AssertAotDocument(data.GetProperty(part).GetProperty("xml").GetString()!);
    }

    [Fact]
    public void Number_sequence_returns_the_module_extension_and_the_edt()
    {
        var envelope = Generate("""
            {"objectType":"number-sequence","name":"ConFleet","edt":"ConFleetId","scope":"company"}
            """);
        AssertOk(envelope);
        var documents = envelope.GetProperty("data").GetProperty("documents");
        Assert.Equal(2, documents.GetArrayLength());
        foreach (var d in documents.EnumerateArray())
            AssertAotDocument(d.GetProperty("xml").GetString()!);
    }

    [Fact]
    public void Workflow_returns_the_template_the_document_and_the_elements_asked_for()
    {
        var envelope = Generate("""
            {"objectType":"workflow","name":"ConFleetApprovalWorkflow","approvalName":"ConFleetApproval",
             "taskName":"ConFleetTask","documentMenuItem":"ConFleetOpen"}
            """);
        AssertOk(envelope);
        var documents = envelope.GetProperty("data").GetProperty("documents");
        Assert.Equal(4, documents.GetArrayLength());
        foreach (var d in documents.EnumerateArray())
            AssertAotDocument(d.GetProperty("xml").GetString()!);
    }

    [Fact]
    public void Report_returns_the_whole_stack()
    {
        var envelope = Generate("""
            {"objectType":"report","name":"ConFleetAging","fields":["VehicleId","DaysOpen:Integer"],
             "parameters":["FromDate:Date"],"preProcess":true}
            """);
        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        Assert.Equal("ConFleetAgingDP", data.GetProperty("dpClass").GetString());
        Assert.Equal("ConFleetAgingTmp", data.GetProperty("tmpTable").GetString());

        var documents = data.GetProperty("documents");
        Assert.True(documents.GetArrayLength() >= 4,
            "an SSRS report is a stack: AxReport, DP, temp table and controller at minimum");
        foreach (var d in documents.EnumerateArray())
            AssertAotDocument(d.GetProperty("xml").GetString()!);
    }

    [Theory]
    [InlineData("""{"objectType":"report-extension","pattern":"dataset","dpClass":"SalesInvoiceDP","tmpTable":"SalesInvoiceTmp"}""")]
    [InlineData("""{"objectType":"report-extension","pattern":"custom-design","report":"SalesInvoice","design":"ConDesign","documentType":"SalesOrderInvoice"}""")]
    [InlineData("""{"objectType":"report-extension","pattern":"menu-redirect","controller":"SalesInvoiceController","report":"SalesInvoice","design":"ConDesign"}""")]
    public void Report_extension_covers_the_three_ways_to_extend_a_shipped_report(string arguments)
    {
        var envelope = Generate(arguments);
        AssertOk(envelope);
        foreach (var d in envelope.GetProperty("data").GetProperty("documents").EnumerateArray())
            AssertAotDocument(d.GetProperty("xml").GetString()!);
    }

    [Fact]
    public void An_unknown_report_extension_pattern_names_the_three_that_exist()
    {
        var envelope = Generate("""{"objectType":"report-extension","pattern":"rdl-edit"}""");
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        var hint = envelope.GetProperty("error").GetProperty("hint").GetString();
        Assert.Contains("dataset", hint, StringComparison.Ordinal);
        Assert.Contains("custom-design", hint, StringComparison.Ordinal);
        Assert.Contains("menu-redirect", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_script_and_event_handler_scaffold_a_class_each()
    {
        AssertAotDocument(Generate("""
            {"objectType":"migration-script","name":"ConFleetBackfill","sourceTable":"ConFleetOld",
             "targetTable":"ConFleetVehicle","mode":"upsert","batchSize":500}
            """) is var migration && migration.GetProperty("ok").GetBoolean()
                ? migration.GetProperty("data").GetProperty("xml").GetString()!
                : throw new Xunit.Sdk.XunitException(migration.GetProperty("error").GetProperty("message").GetString()));

        var handler = Generate("""
            {"objectType":"event-handler","name":"ConFleetEventHandler","sourceKind":"Table",
             "sourceObject":"ConFleetVehicle","event":"Inserted"}
            """);
        AssertOk(handler);
        AssertAotDocument(handler.GetProperty("data").GetProperty("xml").GetString()!);
    }

    /// <summary>
    /// The index is empty here, so this proves the routing and the refusal — not the mining.
    /// </summary>
    [Theory]
    [InlineData("""{"objectType":"find-methods","table":"ConFleetVehicle"}""")]
    [InlineData("""{"objectType":"table-relation","table":"ConFleetVehicle"}""")]
    public void The_table_augmenters_route_and_say_what_is_missing(string arguments)
    {
        var envelope = Generate(arguments);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        var error = envelope.GetProperty("error");
        Assert.Equal("NOT_FOUND", error.GetProperty("code").GetString());
        Assert.DoesNotContain("Unknown objectType", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_objectType_that_does_not_exist_lists_the_ones_that_do()
    {
        var envelope = Generate("""{"objectType":"stored-procedure","name":"X"}""");
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        var hint = envelope.GetProperty("error").GetProperty("hint").GetString()!;

        // The hint is the only catalogue an agent gets after a wrong guess, so it has to name
        // every type the switch really handles.
        foreach (var objectType in new[]
                 {
                     "table", "class", "coc", "form", "edt", "enum", "query", "sysoperation",
                     "business-event", "runbase", "security-policy", "menu-item", "privilege",
                     "duty", "role", "entity", "extension", "event-handler", "view", "map",
                     "systest", "migration-script", "custom-service", "number-sequence",
                     "workflow", "report", "report-extension", "find-methods", "table-relation",
                     "form-clone", "datasource-method", "control-method",
                 })
            Assert.Contains(objectType, hint, StringComparison.Ordinal);
    }
}
