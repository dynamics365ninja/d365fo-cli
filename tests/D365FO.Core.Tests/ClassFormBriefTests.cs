using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core.Analysis;
using D365FO.Core.Index;
using D365FO.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The brief class and form shapes (<c>get class --brief</c>, <c>get form --brief</c>,
/// <c>get_object_info(objectType=class|form, detail=brief)</c>).
/// </summary>
public class ClassFormBriefTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-brief-cf-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private static readonly ClassDetails IndexedClass = new(
        new ClassInfo(1, "ContosoDealCalc", "Contoso", "RunBaseBatch", false, false, null),
        new[]
        {
            new MethodInfo("run", "public void run()", "void", false),
            new MethodInfo("construct", "public static ContosoDealCalc construct()", "ContosoDealCalc", true),
        },
        new[]
        {
            new MethodInfo("prompt", "public boolean prompt()", "boolean", false) { DeclaringClass = "RunBase" },
            new MethodInfo("pack", "public container pack()", "container", false) { DeclaringClass = "RunBase" },
        });

    /// <summary>
    /// Through the bridge every method carries its whole source. The brief keeps the name and,
    /// from the index, the signature, and drops the body and the declaration text.
    /// </summary>
    [Fact]
    public void Class_bridge_brief_drops_method_bodies_and_takes_signatures_from_the_index()
    {
        var payload = JsonNode.Parse("""
            {
              "Name": "ContosoDealCalc", "Extends": "RunBaseBatch", "IsFinal": "False",
              "Declaration": "class ContosoDealCalc extends RunBaseBatch { }",
              "SourceCode": { "Declaration": "class ContosoDealCalc extends RunBaseBatch { }" },
              "_source": "bridge",
              "Methods": [
                { "Name": "run", "Visibility": "Private", "IsStatic": "False",
                  "Source": "public void run() { info(\"long body\"); }", "ReturnType": { "Type": "Void" } },
                { "Name": "helper", "Visibility": "Private", "IsStatic": "True",
                  "Source": "private static int helper() { return 1; }", "ReturnType": { "Type": "Integer" } }
              ]
            }
            """)!.AsObject();

        var brief = ClassBrief.FromBridge(payload, IndexedClass);
        var text = brief.ToJsonString();

        Assert.Equal("brief", (string?)brief["detail"]);
        Assert.Equal("RunBaseBatch", (string?)brief["properties"]!["Extends"]);
        Assert.DoesNotContain("long body", text);
        Assert.DoesNotContain("Declaration", text);
        Assert.DoesNotContain("_source", text);
        Assert.DoesNotContain("Visibility", text);   // the bridge says Private for public methods

        var methods = brief["methods"]!.AsArray();
        Assert.Equal("public void run()", (string?)methods[0]!["signature"]);
        Assert.Null(methods[0]!["returnType"]);       // the signature says it
        // Not in the index: return type and flags from the payload stand in.
        Assert.Null(methods[1]!["signature"]);
        Assert.Equal("Integer", (string?)methods[1]!["returnType"]);
        Assert.True((bool)methods[1]!["static"]!);
    }

    [Fact]
    public void Class_index_brief_lists_own_signatures_and_counts_inherited_methods()
    {
        var brief = JsonSerializer.SerializeToNode(ClassBrief.FromIndex(IndexedClass))!;

        Assert.Equal("brief", (string?)brief["detail"]);
        Assert.Equal(2, brief["methods"]!.AsArray().Count);
        Assert.Equal("public static ContosoDealCalc construct()", (string?)brief["methods"]![1]!["signature"]);
        Assert.Equal(2, (int)brief["inheritedMethodCount"]!);
        Assert.DoesNotContain("pack", brief.ToJsonString());
    }

    /// <summary>
    /// The full form payload carries every data source field's property bag, every control's
    /// layout and every method body. The brief keeps data sources with table and join, the
    /// design pattern, top-level controls with how many they hold, and method names.
    /// </summary>
    [Fact]
    public void Form_bridge_brief_keeps_data_sources_design_and_control_outline()
    {
        var payload = JsonNode.Parse("""
            {
              "Name": "ContosoDealTable", "FormTemplate": "None", "UnparsableSource": "",
              "DataSources": [
                { "Name": "Deal", "Table": "ContosoDealTable", "JoinSource": "", "LinkType": "Delayed",
                  "Fields": [ { "DataField": "DealId", "AllowEdit": "Yes" }, { "DataField": "Status" } ],
                  "Methods": [ { "Name": "active", "Source": "public int active() { return next active(); }" } ] },
                { "Name": "Line", "Table": "ContosoDealLine", "JoinSource": "Deal", "LinkType": "Active",
                  "Fields": [ { "DataField": "LineNum" } ] }
              ],
              "Design": {
                "Pattern": "DetailsTransaction", "PatternVersion": "1.1", "Style": "DetailsFormTransaction",
                "Caption": "@Contoso:Deal", "TopMargin": "-1", "LeftMode": "AutoLeft",
                "Controls": [
                  { "Name": "ActionPane", "Type": "ActionPane", "TopMargin": "-1",
                    "Controls": { "Name": "Tab", "Type": "ActionPaneTab",
                                  "Controls": [ { "Name": "New", "Type": "CommandButton" } ] } },
                  { "Name": "Grid", "Type": "Grid", "DataSource": "Line" }
                ]
              },
              "Methods": [ { "Name": "init", "Source": "public void init() { super(); }", "ReturnType": { "Type": "Void" } } ]
            }
            """)!.AsObject();

        var brief = FormBrief.FromBridge(payload);
        var text = brief.ToJsonString();

        Assert.Equal("brief", (string?)brief["detail"]);
        Assert.DoesNotContain("AllowEdit", text);
        Assert.DoesNotContain("TopMargin", text);
        Assert.DoesNotContain("super()", text);
        Assert.DoesNotContain("UnparsableSource", text);

        Assert.Equal("DetailsTransaction", (string?)brief["design"]!["Pattern"]);
        Assert.Equal("@Contoso:Deal", (string?)brief["design"]!["Caption"]);

        var ds = brief["dataSources"]!.AsArray();
        Assert.Equal("ContosoDealTable", (string?)ds[0]!["table"]);
        Assert.Equal(2, (int)ds[0]!["fieldCount"]!);
        Assert.Equal("active", (string?)ds[0]!["methods"]![0]);
        Assert.Null(ds[0]!["joinSource"]);
        Assert.Null(ds[0]!["linkType"]);              // only meaningful with a join source
        Assert.Equal("Deal", (string?)ds[1]!["joinSource"]);
        Assert.Equal("Active", (string?)ds[1]!["linkType"]);

        // A one-element collection may arrive as the element itself, not an array.
        var controls = brief["controls"]!.AsArray();
        Assert.Equal("ActionPane", (string?)controls[0]!["name"]);
        Assert.Equal(2, (int)controls[0]!["controlCount"]!);
        Assert.Equal("Line", (string?)controls[1]!["dataSource"]);
        Assert.Equal(4, (int)brief["controlCount"]!);

        Assert.Equal("init", (string?)brief["methods"]![0]!["name"]);
    }

    private bool _seeded;

    private CallToolResult Call(string json)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        if (!_seeded) repo.ApplyExtract(ExtractBatch.Empty("Contoso") with
        {
            Classes = new[]
            {
                new ExtractedClass("ContosoDealCalc", null, false, false, null, new[]
                {
                    new ExtractedMethod("run", "public void run()", "void", false),
                }),
            },
            Forms = new[]
            {
                new ExtractedForm("ContosoDealTable", null, new[] { new ExtractedFormDataSource("Deal", "ContosoDealTable") }),
            },
        });
        _seeded = true;
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        return McpServerHost.Invoke(new ToolHandlers(repo), new CallToolRequestParams { Name = "get_object_info", Arguments = args });
    }

    private static JsonElement Envelope(CallToolResult result) =>
        JsonDocument.Parse(Assert.IsType<TextContentBlock>(result.Content[0]).Text).RootElement;

    [Fact]
    public void Mcp_detail_brief_works_for_class_and_form()
    {
        var cls = Envelope(Call("""{"objectType":"class","name":"ContosoDealCalc","detail":"brief"}"""));
        Assert.True(cls.GetProperty("ok").GetBoolean());
        Assert.Equal("brief", cls.GetProperty("data").GetProperty("detail").GetString());
        Assert.Equal("public void run()", cls.GetProperty("data").GetProperty("methods")[0].GetProperty("signature").GetString());
        Assert.Equal(0, cls.GetProperty("data").GetProperty("inheritedMethodCount").GetInt32());

        var form = Envelope(Call("""{"objectType":"form","name":"ContosoDealTable","detail":"brief"}"""));
        Assert.True(form.GetProperty("ok").GetBoolean());
        Assert.Equal("ContosoDealTable", form.GetProperty("data").GetProperty("dataSources")[0].GetProperty("tableName").GetString());
    }

    [Fact]
    public void Mcp_detail_brief_for_a_missing_class_or_form_says_not_found()
    {
        Assert.Equal("CLASS_NOT_FOUND", Envelope(Call("""{"objectType":"class","name":"Nope","detail":"brief"}"""))
            .GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("FORM_NOT_FOUND", Envelope(Call("""{"objectType":"form","name":"Nope","detail":"brief"}"""))
            .GetProperty("error").GetProperty("code").GetString());
    }
}
