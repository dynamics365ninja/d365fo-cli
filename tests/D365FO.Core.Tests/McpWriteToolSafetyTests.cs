using System.Text.Json;
using D365FO.Core.Index;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The safety flags of the tools that write, and the dedup cache around them. Before this,
/// <c>delete_object</c> was advertised <c>readOnlyHint=true</c> / <c>destructiveHint=false</c>
/// (so clients skipped the confirmation), had its result dedup-cached, and stayed callable on
/// an <c>MCP_SERVER_MODE=read-only</c> server; <c>index_sync</c> and <c>sdlc</c> were also
/// missing from the write list; and a read issued after a write within 60 s was answered from
/// the pre-write cache.
/// </summary>
/// <remarks>
/// In the environment collection because <see cref="CallDedup"/> is process-wide: a write
/// tool called by a parallel test class would flush the cache between two calls here.
/// </remarks>
[Collection(EnvironmentCollectionDefinition.Name)]
public class McpWriteToolSafetyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-writesafety-{Guid.NewGuid():N}.sqlite");

    public McpWriteToolSafetyTests() => CallDedup.Clear();

    public void Dispose()
    {
        CallDedup.Clear();
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    private StdioDispatcher Dispatcher(McpServerMode mode = McpServerMode.Full)
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        return new StdioDispatcher(new ToolHandlers(repo), mode);
    }

    private static async Task<JsonElement> Send(StdioDispatcher dispatcher, string request)
    {
        using var input = new StringReader(request + "\n");
        using var output = new StringWriter();
        await dispatcher.RunAsync(input, output);
        return JsonDocument.Parse(output.ToString()).RootElement.Clone();
    }

    private static string CallText(JsonElement response) =>
        response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;

    private static async Task<Dictionary<string, JsonElement>> ListedAnnotations(StdioDispatcher dispatcher)
    {
        var response = await Send(dispatcher, """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        return response.GetProperty("result").GetProperty("tools").EnumerateArray()
            .ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("annotations"));
    }

    // ---- Classification ----

    [Theory]
    [InlineData("delete_object")]
    [InlineData("undo_last_modification")]
    [InlineData("sdlc")]
    [InlineData("generate_object")]
    [InlineData("modify_object")]
    [InlineData("modify_method")]
    [InlineData("labels")]
    public async Task Destructive_tool_is_not_advertised_read_only(string tool)
    {
        var annotations = await ListedAnnotations(Dispatcher());

        Assert.False(annotations[tool].GetProperty("readOnlyHint").GetBoolean(), $"{tool} advertised read-only");
        Assert.True(annotations[tool].GetProperty("destructiveHint").GetBoolean(), $"{tool} not advertised destructive");
        Assert.False(annotations[tool].GetProperty("idempotentHint").GetBoolean(), $"{tool} advertised idempotent");
    }

    [Fact]
    public void Default_sdk_transport_advertises_the_same_hints_as_the_legacy_transport()
    {
        // The SDK host is what Claude Desktop and most clients get. It listed tools with no
        // annotations at all, so the corrected hints above never reached them.
        foreach (var tool in McpServerHost.BuildToolList())
        {
            var d = ToolCatalog.All.Single(x => x.Name == tool.Name);
            var expected = ToolCatalog.AnnotationsFor(d);
            Assert.NotNull(tool.Annotations);
            Assert.Equal((bool)expected["readOnlyHint"]!, tool.Annotations!.ReadOnlyHint);
            Assert.Equal((bool)expected["destructiveHint"]!, tool.Annotations.DestructiveHint);
            Assert.Equal((bool)expected["idempotentHint"]!, tool.Annotations.IdempotentHint);
            Assert.Equal((string?)expected["title"], tool.Annotations.Title);
        }

        var delete = McpServerHost.BuildToolList().Single(t => t.Name == "delete_object");
        Assert.False(delete.Annotations!.ReadOnlyHint);
        Assert.True(delete.Annotations.DestructiveHint);
    }

    [Fact]
    public async Task Index_sync_is_advertised_as_a_write_that_is_not_destructive()
    {
        // It rewrites the index, which the server derives and can rebuild — a client should
        // know it changes state, but it destroys nothing the caller owns.
        var annotations = (await ListedAnnotations(Dispatcher()))["index_sync"];

        Assert.False(annotations.GetProperty("readOnlyHint").GetBoolean());
        Assert.False(annotations.GetProperty("destructiveHint").GetBoolean());
        Assert.True(annotations.GetProperty("idempotentHint").GetBoolean());
    }

    [Fact]
    public void Write_tool_list_cannot_miss_a_descriptor_that_writes()
    {
        // The list is derived from each descriptor's declared effect, not kept by hand. Lock
        // the tools known to write so a reclassification is a visible, reviewed change.
        var declaredWrites = ToolCatalog.All.Where(d => d.Effect != ToolCatalog.ToolEffect.Read)
            .Select(d => d.Name).ToHashSet();

        Assert.Equal(declaredWrites.OrderBy(n => n), ToolCatalog.WriteTools.OrderBy(n => n));
        foreach (var tool in new[]
                 {
                     "generate_object", "labels", "modify_method", "modify_object", "undo_last_modification",
                     "delete_object", "index_sync", "sdlc",
                 })
            Assert.Contains(tool, ToolCatalog.WriteTools);
    }

    [Fact]
    public void Tool_that_takes_a_write_argument_is_not_declared_read_only()
    {
        // A second net under the declared effect: an argument that only means something to a
        // writer (where to write, whether to overwrite, a preview of a write) on a tool declared
        // Read is almost certainly a misclassified new tool.
        string[] writeArguments = ["installTo", "overwrite", "dryRun", "out"];
        foreach (var d in ToolCatalog.All.Where(d => d.Effect == ToolCatalog.ToolEffect.Read))
        {
            var properties = d.InputSchema["properties"]?.AsObject();
            if (properties is null) continue;
            foreach (var arg in writeArguments)
                Assert.False(properties.ContainsKey(arg),
                    $"{d.Name} declares `{arg}` but is classified ToolEffect.Read — does it write?");
        }
    }

    [Fact]
    public void Every_tool_that_writes_is_local_and_hidden_from_read_only_mode()
    {
        foreach (var d in ToolCatalog.All.Where(d => d.Effect != ToolCatalog.ToolEffect.Read))
        {
            Assert.Contains(d.Name, ServerModeConfig.LocalTools);
            Assert.False(ServerModeConfig.IsToolAllowed(McpServerMode.ReadOnly, d.Name),
                $"{d.Name} writes but is allowed in read-only mode");
        }
    }

    // ---- Read-only mode ----

    [Fact]
    public async Task Delete_object_is_not_listed_or_callable_in_read_only_mode()
    {
        var dispatcher = Dispatcher(McpServerMode.ReadOnly);

        var annotations = await ListedAnnotations(dispatcher);
        Assert.DoesNotContain("delete_object", annotations.Keys);

        var response = await Send(dispatcher,
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"delete_object","arguments":{"kind":"class","name":"Ghost"}}}""");
        Assert.Equal(D365FoErrorCodes.ModeNotAllowed,
            response.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
    }

    // ---- Dedup cache ----

    [Fact]
    public async Task Delete_object_result_is_not_served_from_the_dedup_cache()
    {
        var dispatcher = Dispatcher();
        const string call = """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"delete_object","arguments":{"kind":"class","name":"Ghost"}}}""";

        await Send(dispatcher, call);
        var second = CallText(await Send(dispatcher, call));

        Assert.DoesNotContain("[dedup]", second);
    }

    [Fact]
    public async Task Read_after_a_write_is_not_answered_from_the_pre_write_cache()
    {
        var dispatcher = Dispatcher();
        const string read = """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"search","arguments":{"type":"class","query":"DedupAfterWriteProbe"}}}""";

        await Send(dispatcher, read);
        // Sanity: the read really is cached, so the assertion below tests the flush.
        Assert.Contains("[dedup]", CallText(await Send(dispatcher, read)));

        // Any write tool call flushes — this one is refused (no installTo / path), which still
        // counts: a refused batch or a half-failed build can have changed disk or the index.
        await Send(dispatcher,
            """{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"delete_object","arguments":{"kind":"class","name":"Ghost"}}}""");

        Assert.DoesNotContain("[dedup]", CallText(await Send(dispatcher, read)));
    }

    [Fact]
    public void Read_tool_call_does_not_flush_the_dedup_cache()
    {
        CallDedup.Store(CallDedup.Key("search", "{}"), "body", isError: false);

        Assert.False(CallDedup.InvalidateAfter("get_object_info"));
        Assert.NotNull(CallDedup.TryGet(CallDedup.Key("search", "{}")));

        Assert.True(CallDedup.InvalidateAfter("modify_object"));
        Assert.Null(CallDedup.TryGet(CallDedup.Key("search", "{}")));
    }

    [Fact]
    public void Read_that_started_before_a_write_is_not_cached_after_the_flush()
    {
        // A read on one connection captures the generation, a write on another flushes while
        // the read is still running, then the read stores its now pre-write answer.
        var key = CallDedup.Key("search", """{"query":"InFlight"}""");
        var before = CallDedup.Generation;

        Assert.True(CallDedup.InvalidateAfter("delete_object"));
        Assert.NotEqual(before, CallDedup.Generation);
        CallDedup.Store(key, "pre-write body", isError: false, observedGeneration: before);

        Assert.Null(CallDedup.TryGet(key));

        // A read that started after the flush is cached as before.
        CallDedup.Store(key, "post-write body", isError: false, observedGeneration: CallDedup.Generation);
        Assert.Equal("post-write body", CallDedup.TryGet(key)!.Value.Body);
    }
}
