using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #112: structured method-level modify via D365FO.Bridge. Exercises
/// <see cref="MethodModifyEngine.ModifyCore"/> against a fake bridge (no real
/// process spawned) so the read → structured-replace → validate → write
/// sequence is verified without a D365FO VM.
/// </summary>
public sealed class MethodModifyEngineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"methodmodify-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public MethodModifyEngineTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        var batch = new ExtractBatch(
            Model: "ApplicationSuite",
            Publisher: "Microsoft",
            Layer: "app",
            IsCustom: false,
            Tables: Array.Empty<ExtractedTable>(),
            Classes: new[]
            {
                new ExtractedClass("CustBalance", null, false, false, "x", new[]
                {
                    new ExtractedMethod("calc", "real calc()", "real", false),
                }),
            },
            Edts: Array.Empty<ExtractedEdt>(),
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>());
        _repo.ApplyExtract(batch);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
    }

    private const string ClassXmlWithMethod =
        "<AxClass><Name>CustBalance</Name><SourceCode><Methods>" +
        "<Method><Name>calc</Name><Source><![CDATA[\n    real calc()\n    {\n        return 1;\n    }\n]]></Source></Method>" +
        "</Methods></SourceCode></AxClass>";

    [Fact]
    public async Task Modify_replaces_method_source_and_writes_back_through_bridge()
    {
        JsonObject? capturedUpdateArgs = null;
        using var harness = FakeBridge.Create(request =>
        {
            var method = (string?)request["method"];
            if (method == "readObjectXml")
            {
                return Response(request, new JsonObject { ["ok"] = true, ["xml"] = ClassXmlWithMethod });
            }
            if (method == "updateObject")
            {
                capturedUpdateArgs = (JsonObject?)request["params"];
                return Response(request, new JsonObject { ["ok"] = true });
            }
            throw new InvalidOperationException("unexpected method: " + method);
        });

        var req = new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.True(result.Ok);
        Assert.NotNull(capturedUpdateArgs);
        Assert.Equal("ApplicationSuite", (string?)capturedUpdateArgs!["model"]);
        Assert.Contains("return 2;", (string?)capturedUpdateArgs["xml"]);
        Assert.DoesNotContain("return 1;", (string?)capturedUpdateArgs["xml"]);
    }

    [Fact]
    public void Modify_unknown_method_fails_METHOD_NOT_FOUND_with_known_methods_hint()
    {
        using var harness = FakeBridge.Create(request =>
            Response(request, new JsonObject { ["ok"] = true, ["xml"] = ClassXmlWithMethod }));

        var req = new MethodModifyEngine.ModifyRequest("class", "CustBalance", "doesNotExist", "return 2;", Model: "ApplicationSuite");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.MethodNotFound, result.Error!.Code);
        Assert.Contains("calc", result.Error.Hint);
    }

    [Fact]
    public void Modify_unsupported_kind_fails_BAD_INPUT_without_calling_bridge()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var req = new MethodModifyEngine.ModifyRequest("enum", "SomeEnum", "x", "y");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    [Fact]
    public void Modify_unresolvable_model_fails_before_touching_bridge()
    {
        using var harness = FakeBridge.Create(_ => throw new InvalidOperationException("bridge should not be called"));

        var req = new MethodModifyEngine.ModifyRequest("class", "NoSuchClassInIndex", "calc", "return 2;");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ClassNotFound, result.Error!.Code);
    }

    [Fact]
    public void Modify_bridge_read_failure_surfaces_as_failure()
    {
        using var harness = FakeBridge.Create(request =>
            Response(request, new JsonObject { ["ok"] = false, ["error"] = "NOT_FOUND", ["message"] = "no such object" }));

        var req = new MethodModifyEngine.ModifyRequest("class", "CustBalance", "calc", "return 2;", Model: "ApplicationSuite");
        var result = MethodModifyEngine.ModifyCore(req, _repo, harness.Client);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ClassNotFound, result.Error!.Code);
    }

    [Fact]
    public void LocateMethodsContainer_finds_table_shape_direct_child()
    {
        var doc = System.Xml.Linq.XDocument.Parse("<AxTable><Methods><Method><Name>m</Name></Method></Methods></AxTable>");
        var container = MethodModifyEngine.LocateMethodsContainer(doc.Root);
        Assert.NotNull(container);
        Assert.Equal("Methods", container!.Name.LocalName);
    }

    [Fact]
    public void LocateMethodsContainer_ignores_nested_datasource_methods()
    {
        var doc = System.Xml.Linq.XDocument.Parse(
            "<AxForm><SourceCode><DataSources><DataSource><Methods><Method><Name>m</Name></Method></Methods></DataSource></DataSources></SourceCode></AxForm>");
        var container = MethodModifyEngine.LocateMethodsContainer(doc.Root);
        Assert.Null(container);
    }

    private static JsonObject Response(JsonObject request, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = request["id"]?.DeepClone(),
        ["result"] = result,
    };

    /// <summary>
    /// Minimal fake bridge: routes each request line to <paramref name="respondWith"/>
    /// and feeds the returned JSON-RPC envelope back as the next response line.
    /// Mirrors <c>BridgeClientTests.FakeBridge</c> (kept separate/internal to each
    /// test file rather than shared, per this repo's existing test-fixture style).
    /// </summary>
    private sealed class FakeBridge : IDisposable
    {
        public BridgeClient Client { get; }

        private FakeBridge(BridgeClient client) => Client = client;

        public static FakeBridge Create(Func<JsonObject, JsonObject> respondWith)
        {
            var reader = new DeferredResponseReader(respondWith);
            var client = new BridgeClient(writer: new TeeWriter(reader), reader: reader);
            return new FakeBridge(client);
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly DeferredResponseReader signal;
        private readonly StringBuilder buffer = new();

        public TeeWriter(DeferredResponseReader signal) => this.signal = signal;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = buffer.ToString();
                buffer.Clear();
                signal.OnRequest(line);
            }
            else
            {
                buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null) return;
            foreach (var ch in value) Write(ch);
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }
    }

    private sealed class DeferredResponseReader : TextReader
    {
        private readonly Func<JsonObject, JsonObject> respondWith;
        private readonly Queue<string> queued = new();

        public DeferredResponseReader(Func<JsonObject, JsonObject> respondWith) => this.respondWith = respondWith;

        public void OnRequest(string requestLine)
        {
            var parsed = JsonNode.Parse(requestLine) as JsonObject
                ?? throw new InvalidOperationException("bad request JSON");
            var response = respondWith(parsed);
            queued.Enqueue(response.ToJsonString());
        }

        public override string? ReadLine() => queued.Count == 0 ? null : queued.Dequeue();
    }
}
