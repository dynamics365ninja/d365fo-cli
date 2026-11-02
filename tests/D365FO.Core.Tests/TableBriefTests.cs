using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core.Analysis;
using D365FO.Core.Index;
using D365FO.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The brief table shape (<c>get table --brief</c>, <c>get_object_info(detail=brief)</c>).
/// </summary>
public class TableBriefTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-brief-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    /// <summary>
    /// The bridge payload is the reflected AxTable: every field carries its whole property
    /// bag, every relation its constraints, and field groups ride along. The brief keeps
    /// names, types, EDTs and keys only.
    /// </summary>
    [Fact]
    public void Bridge_brief_drops_field_groups_constraints_and_field_property_bags()
    {
        var payload = JsonNode.Parse("""
            {
              "Name": "ContosoDealTable", "Label": "@Contoso:Deal", "TableGroup": "Main",
              "Fields": [
                { "Name": "DealId", "ExtendedDataType": "ContosoDealId", "Label": "@Contoso:DealId",
                  "HelpText": "@Contoso:DealIdHelp", "Mandatory": "Yes", "AllowEdit": "No" },
                { "Name": "Status", "EnumType": "ContosoDealStatus", "Label": "@Contoso:Status" }
              ],
              "FieldGroups": [ { "Name": "Overview", "Fields": [ { "DataField": "DealId" } ] } ],
              "Indexes": [ { "Name": "DealIdx", "AlternateKey": "Yes", "Fields": [ { "DataField": "DealId" } ] },
                           { "Name": "StatusIdx", "AllowDuplicates": "Yes", "Fields": [ { "DataField": "Status" } ] } ],
              "Relations": [ { "Name": "CustTable", "RelatedTable": "CustTable", "Cardinality": "ZeroMore",
                               "Constraints": [ { "Name": "AccountNum", "Field": "CustAccount", "RelatedField": "AccountNum" } ] } ],
              "Methods": [ { "Name": "find", "Source": "public static ContosoDealTable find() {}" } ]
            }
            """)!.AsObject();

        var brief = TableBrief.FromBridge(payload, indexed: null);
        var text = brief.ToJsonString();

        Assert.Equal("brief", (string?)brief["detail"]);
        Assert.Equal("Main", (string?)brief["properties"]!["TableGroup"]);
        Assert.DoesNotContain("FieldGroups", text);
        Assert.DoesNotContain("HelpText", text);
        Assert.DoesNotContain("Constraints", text);
        Assert.DoesNotContain("Source", text);

        var fields = brief["fields"]!.AsArray();
        Assert.Equal("ContosoDealId", (string?)fields[0]!["edt"]);
        Assert.Null(fields[0]!["type"]);                       // no index to say it is a String
        Assert.Equal("Enum", (string?)fields[1]!["type"]);     // an EnumType says so on its own
        Assert.Equal("ContosoDealStatus", (string?)fields[1]!["enumType"]);

        var indexes = brief["indexes"]!.AsArray();
        Assert.Equal("DealId", (string?)indexes[0]!["fields"]![0]);
        Assert.True((bool)indexes[0]!["unique"]!);
        Assert.Null(indexes[1]!["unique"]);

        Assert.Equal("CustTable", (string?)brief["relations"]![0]!["relatedTable"]);
        Assert.Equal(1, (int)brief["methodCount"]!);
    }

    [Fact]
    public void Bridge_brief_takes_field_types_from_the_index_when_it_has_the_table()
    {
        var payload = JsonNode.Parse("""{ "Name": "T", "Fields": [ { "Name": "DealId", "ExtendedDataType": "ContosoDealId" } ] }""")!.AsObject();
        var indexed = new TableDetails(
            new TableInfo(1, "T", "Contoso", null, null),
            new[] { new TableFieldInfo("DealId", "String", "ContosoDealId", null, false) },
            Array.Empty<RelationInfo>(), Array.Empty<TableMethodInfo>(),
            Array.Empty<TableIndexInfo>(), Array.Empty<TableDeleteActionInfo>());

        var brief = TableBrief.FromBridge(payload, indexed);

        Assert.Equal("String", (string?)brief["fields"]![0]!["type"]);
    }

    // ---- MCP get_object_info(detail=…) ----

    private bool _seeded;

    private CallToolResult Call(string json)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        if (!_seeded) repo.ApplyExtract(ExtractBatch.Empty("Contoso") with
        {
            Tables = new[]
            {
                new ExtractedTable("ContosoDealTable", null, null, new[]
                {
                    new ExtractedTableField("DealId", "String", "ContosoDealId", "@Contoso:DealId", true),
                }),
            },
        });
        _seeded = true;
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        return McpServerHost.Invoke(new ToolHandlers(repo), new CallToolRequestParams { Name = "get_object_info", Arguments = args });
    }

    private static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement;

    [Fact]
    public void Mcp_detail_brief_returns_the_summary_shape()
    {
        var envelope = Envelope(Call("""{"objectType":"table","name":"ContosoDealTable","detail":"brief"}"""));

        Assert.True(envelope.GetProperty("ok").GetBoolean());
        var field = envelope.GetProperty("data").GetProperty("fields")[0];
        Assert.Equal("ContosoDealId", field.GetProperty("edt").GetString());
        Assert.False(field.TryGetProperty("label", out _));
    }

    [Theory]
    [InlineData("""{"objectType":"class","name":"X","detail":"brief"}""", "table only")]
    [InlineData("""{"objectType":"table","name":"ContosoDealTable","detail":"tiny"}""", "Unknown detail")]
    [InlineData("""{"objectType":"table","name":"ContosoDealTable","detail":"brief","relations":true}""", "slice")]
    public void Mcp_detail_it_cannot_honour_is_refused_not_ignored(string json, string message)
    {
        var envelope = Envelope(Call(json));

        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal("BAD_INPUT", envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(message, envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Mcp_detail_full_is_the_unchanged_default()
    {
        var full = Envelope(Call("""{"objectType":"table","name":"ContosoDealTable","detail":"full"}"""));
        var none = Envelope(Call("""{"objectType":"table","name":"ContosoDealTable"}"""));

        Assert.Equal(none.GetRawText(), full.GetRawText());
        Assert.Equal("@Contoso:DealId", none.GetProperty("data").GetProperty("fields")[0].GetProperty("label").GetString());
    }
}
