using System.Text.Json;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

public class McpDispatcherTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-mcp-{Guid.NewGuid():N}.sqlite");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private async Task<List<JsonDocument>> Roundtrip(params string[] requests)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var dispatcher = new StdioDispatcher(new ToolHandlers(repo));

        using var input = new StringReader(string.Join('\n', requests) + '\n');
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        return output.ToString()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => JsonDocument.Parse(s))
            .ToList();
    }

    [Fact]
    public async Task Initialize_returns_protocol_version_and_capabilities()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        var doc = Assert.Single(resp);
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("2024-11-05", result.GetProperty("protocolVersion").GetString());
        Assert.True(result.GetProperty("capabilities").GetProperty("tools").ValueKind == JsonValueKind.Object);

        // Clients surface `instructions` to the model once, before any tool call. It carries
        // the rule canon, so losing it silently drops every X++ rule from the MCP surface.
        var instructions = result.GetProperty("instructions").GetString();
        Assert.False(string.IsNullOrWhiteSpace(instructions));
        Assert.Contains(D365FO.Core.Knowledge.RuleCanon.Require("core"), instructions!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolsList_returns_non_empty_catalog()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        var doc = Assert.Single(resp);
        var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
        Assert.True(tools.GetArrayLength() >= 10);
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        Assert.Contains("search", names);
        Assert.Contains("get_object_info", names);
        Assert.Contains("index_status", names);
    }

    [Fact]
    public async Task UnknownMethod_returns_jsonrpc_error()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":3,"method":"does/not/exist"}""");
        var doc = Assert.Single(resp);
        var err = doc.RootElement.GetProperty("error");
        Assert.Equal(-32601, err.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task ToolsCall_wraps_tool_result_in_content_block()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"index_status","arguments":{}}}""");
        var doc = Assert.Single(resp);
        var content = doc.RootElement.GetProperty("result").GetProperty("content");
        var first = content[0];
        Assert.Equal("text", first.GetProperty("type").GetString());
        var payload = JsonDocument.Parse(first.GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Notification_does_not_get_a_reply()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Empty(resp);
    }

    [Fact]
    public async Task ToolsList_exposes_unified_tools()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":10,"method":"tools/list"}""");
        var doc = Assert.Single(resp);
        var names = doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToHashSet();
        // The consolidated, discriminator-based tool surface.
        Assert.Contains("search", names);
        Assert.Contains("get_object_info", names);
        Assert.Contains("get_method", names);
        Assert.Contains("labels", names);
        Assert.Contains("security_info", names);
        Assert.Contains("extension_info", names);
        Assert.Contains("object_patterns", names);
        Assert.Contains("generate_object", names);
        Assert.Contains("models", names);
        Assert.Contains("analyze", names);
        Assert.Contains("prepare", names);
        Assert.Contains("find_references", names);
        // The old per-type tools must be gone.
        Assert.DoesNotContain("search_classes", names);
        Assert.DoesNotContain("get_data_entity", names);
        Assert.DoesNotContain("list_models", names);
        // The extension/handler + generate tools folded into the unified surface.
        Assert.DoesNotContain("find_coc_extensions", names);
        Assert.DoesNotContain("find_event_handlers", names);
        Assert.DoesNotContain("find_extensions", names);
        Assert.DoesNotContain("get_table_extension_info", names);
        Assert.DoesNotContain("analyze_extension_points", names);
        Assert.DoesNotContain("form_pattern", names);
        Assert.DoesNotContain("generate", names);
        Assert.DoesNotContain("generate_xml", names);
    }

    [Fact]
    public async Task ExtensionInfo_coc_returns_ok_on_fresh_db()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"extension_info","arguments":{"mode":"coc","target":"CustTable"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task ExtensionInfo_unknown_mode_returns_badInput()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":22,"method":"tools/call","params":{"name":"extension_info","arguments":{"mode":"bogus","target":"CustTable"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("BAD_INPUT", payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task GenerateObject_xml_objectType_returns_xml_without_writing()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":23,"method":"tools/call","params":{"name":"generate_object","arguments":{"objectType":"enum","name":"FmColor","values":["Red","Green"]}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Models_list_returns_empty_collection_for_fresh_db()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":11,"method":"tools/call","params":{"name":"models","arguments":{"action":"list"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, payload.RootElement.GetProperty("data").GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task GetObjectInfo_unknown_service_returns_structured_notFound_error()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":12,"method":"tools/call","params":{"name":"get_object_info","arguments":{"objectType":"service","name":"DoesNotExist"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("SERVICE_NOT_FOUND", payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Prepare_create_issues_grounding_token_on_fresh_db()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":20,"method":"tools/call","params":{"name":"prepare","arguments":{"mode":"create","name":"FmWidget","type":"class"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        var data = payload.RootElement.GetProperty("data");
        Assert.False(string.IsNullOrEmpty(data.GetProperty("groundingToken").GetString()));
        Assert.Equal("class", data.GetProperty("objectType").GetString());
    }

    [Fact]
    public async Task Labels_create_accepts_text_alias_for_value()
    {
        var file = Path.Combine(Path.GetTempPath(), $"d365fo-lbl-{Guid.NewGuid():N}.en-us.label.txt");
        try
        {
            // 'text' is a guessed alias for the canonical 'value' param. The
            // dispatcher must forward it so the written file carries the value.
            var req = "{\"jsonrpc\":\"2.0\",\"id\":24,\"method\":\"tools/call\",\"params\":{\"name\":\"labels\","
                    + "\"arguments\":{\"action\":\"create\",\"file\":\"" + file.Replace("\\", "\\\\") + "\","
                    + "\"key\":\"@Con:Hello\",\"text\":\"Hello world\"}}}";
            var resp = await Roundtrip(req);
            var doc = Assert.Single(resp);
            var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
                .GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("Hello world", await File.ReadAllTextAsync(file));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Labels_create_fans_out_bulk_array()
    {
        var file = Path.Combine(Path.GetTempPath(), $"d365fo-lbl-{Guid.NewGuid():N}.en-us.label.txt");
        try
        {
            // A `labels:[…]` array writes every entry through the single-create path
            // with the shared top-level file; the report aggregates per-entry results.
            var req = "{\"jsonrpc\":\"2.0\",\"id\":26,\"method\":\"tools/call\",\"params\":{\"name\":\"labels\","
                    + "\"arguments\":{\"action\":\"create\",\"file\":\"" + file.Replace("\\", "\\\\") + "\","
                    + "\"labels\":[{\"key\":\"@Con:One\",\"value\":\"First\"},"
                    + "{\"labelId\":\"@Con:Two\",\"text\":\"Second\"}]}}}";
            var resp = await Roundtrip(req);
            var doc = Assert.Single(resp);
            var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
                .GetProperty("content")[0].GetProperty("text").GetString()!);
            Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
            var data = payload.RootElement.GetProperty("data");
            Assert.Equal(2, data.GetProperty("total").GetInt32());
            Assert.Equal(2, data.GetProperty("created").GetInt32());
            Assert.Equal(0, data.GetProperty("failed").GetInt32());
            var written = await File.ReadAllTextAsync(file);
            Assert.Contains("First", written);
            Assert.Contains("Second", written);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task ExtensionInfo_points_accepts_full_extension_name()
    {
        // The dispatch must reach the handler and resolve the dotted extension
        // name to its base target without erroring on a fresh db.
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":25,"method":"tools/call","params":{"name":"extension_info","arguments":{"mode":"points","target":"CustTable.Extension"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task FindReferences_returns_empty_on_fresh_db()
    {
        var resp = await Roundtrip("""{"jsonrpc":"2.0","id":21,"method":"tools/call","params":{"name":"find_references","arguments":{"name":"CustTable"}}}""");
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, payload.RootElement.GetProperty("data").GetProperty("count").GetInt32());
    }

    /// <summary>
    /// Every tool schema declares additionalProperties:false, but the dispatcher
    /// never enforced it: a misspelled key was dropped and the handler ran as if
    /// the caller had omitted the argument — the "confident lie" failure mode.
    /// Same rule the CLI enforces via StrictParsing.
    /// </summary>
    [Fact]
    public async Task ToolsCall_with_a_misspelled_argument_is_rejected()
    {
        var resp = await Roundtrip(
            """{"jsonrpc":"2.0","id":31,"method":"tools/call","params":{"name":"find_references","arguments":{"nmae":"CustTable"}}}""");
        var doc = Assert.Single(resp);

        var error = doc.RootElement.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Contains("Unknown argument 'nmae'", error.GetProperty("message").GetString());
        Assert.Equal("BAD_INPUT", error.GetProperty("data").GetProperty("code").GetString());
    }

    [Fact]
    public async Task ToolsCall_still_accepts_the_tolerated_label_aliases()
    {
        // `labels` deliberately accepts client-guessed aliases (text/label for
        // value, model for installTo, …); they are declared in its schema, so the
        // strict check must not reject them.
        var resp = await Roundtrip(
            """{"jsonrpc":"2.0","id":32,"method":"tools/call","params":{"name":"labels","arguments":{"action":"search","query":"Vehicle","limit":5}}}""");
        var doc = Assert.Single(resp);
        Assert.True(doc.RootElement.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("class")]
    [InlineData("table")]
    [InlineData("enum")]
    [InlineData("form")]
    public async Task GetObjectInfo_dispatches_per_objectType(string objectType)
    {
        var req = "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"get_object_info\",\"arguments\":{\"objectType\":\""
                  + objectType + "\",\"name\":\"Nope\"}}}";
        var resp = await Roundtrip(req);
        var doc = Assert.Single(resp);
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        // Each objectType routes to its reader; an absent object yields a typed not-found.
        Assert.False(payload.RootElement.GetProperty("ok").GetBoolean());
        var code = payload.RootElement.GetProperty("error").GetProperty("code").GetString();
        Assert.EndsWith("_NOT_FOUND", code);
    }

    [Fact]
    public void PrepareChange_answers_for_kernel_declared_table_methods()
    {
        // Every table inherits validateWrite from xRecord — a kernel type with no AOT
        // metadata, so the index has no row for the most common CoC target there is.
        // "Not found" read as "does not exist"; the kernel catalog now answers instead.
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Fleet") with
        {
            Tables = new[] { new ExtractedTable("FmVehicle", null, "x", Array.Empty<ExtractedTableField>()) },
        });

        var result = new ToolHandlers(repo).PrepareChange("FmVehicle", null, "validateWrite", null, null, null);
        Assert.True(result.Ok);

        var json = JsonSerializer.Serialize(result.Data);
        using var doc = JsonDocument.Parse(json);
        var method = doc.RootElement.GetProperty("method");
        Assert.True(method.GetProperty("found").GetBoolean());
        Assert.Equal("public boolean validateWrite()", method.GetProperty("signature").GetString());
        Assert.Contains("orig()", json);

        // A method no kernel type declares still reports honestly as not found.
        var missing = new ToolHandlers(repo).PrepareChange("FmVehicle", null, "noSuchMethodAnywhere", null, null, null);
        var missingJson = JsonSerializer.Serialize(missing.Data);
        Assert.Contains("\"found\":false", missingJson);
    }

    [Fact]
    public async Task Prepare_test_aggregates_methods_coverage_and_red_first_cycle()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Fleet") with
        {
            Classes = new[]
            {
                new ExtractedClass("FmVehicleService", null, false, false, "x",
                    new[] { new ExtractedMethod("run", "public void run()", "void", false),
                            new ExtractedMethod("new", "public void new()", "void", false) }),
                new ExtractedClass("FmVehicleServiceTest", "SysTestCase", false, false, "x",
                    Array.Empty<ExtractedMethod>()),
            },
        });

        var dispatcher = new StdioDispatcher(new ToolHandlers(repo));
        using var input = new StringReader(
            """{"jsonrpc":"2.0","id":30,"method":"tools/call","params":{"name":"prepare","arguments":{"mode":"test","object":"FmVehicleService"}}}""" + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);

        var doc = JsonDocument.Parse(output.ToString().Trim());
        var payload = JsonDocument.Parse(doc.RootElement.GetProperty("result")
            .GetProperty("content")[0].GetProperty("text").GetString()!);
        var data = payload.RootElement.GetProperty("data");

        Assert.True(payload.RootElement.GetProperty("ok").GetBoolean());
        // `run` is worth a test; the `new` lifecycle member is not.
        var methods = data.GetProperty("methodsWorthTesting").EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("run", methods);
        Assert.DoesNotContain("new", methods);
        // Existing coverage is surfaced so a second class is not started for the same target.
        Assert.Contains("FmVehicleServiceTest", data.GetProperty("existingTests").EnumerateArray().Select(t => t.GetString()));
        // The scaffold call and the red-first cycle are stated.
        Assert.Contains("generate systest", data.GetProperty("scaffoldCall").GetString());
        Assert.Contains("--method run", data.GetProperty("scaffoldCall").GetString());
        Assert.False(string.IsNullOrEmpty(data.GetProperty("groundingToken").GetString()));
    }
}
