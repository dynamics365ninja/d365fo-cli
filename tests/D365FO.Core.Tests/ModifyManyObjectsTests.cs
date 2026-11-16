using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #268: one <c>modify batch</c> whose steps name their own objects, against a fake bridge.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class ModifyManyObjectsTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"modifymany-{Guid.NewGuid():N}.sqlite");
    private readonly string _journalRoot = Path.Combine(Path.GetTempPath(), $"modifymany-j-{Guid.NewGuid():N}");
    private readonly string _journalDb;
    private readonly MetadataRepository _repo;
    private readonly string? _prevCustomModels;

    public ModifyManyObjectsTests()
    {
        _journalDb = Path.Combine(_journalRoot, "index.sqlite");
        _prevCustomModels = Environment.GetEnvironmentVariable("D365FO_CUSTOM_MODELS");
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", "FleetCustom");
        Directory.CreateDirectory(_journalRoot);

        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        _repo.ApplyExtract(ExtractBatch.Empty("FleetCustom") with
        {
            IsCustom = true,
            Tables = new[]
            {
                new ExtractedTable("FmVehicle", null, "x", Array.Empty<ExtractedTableField>()),
                new ExtractedTable("FmTrip", null, "x", Array.Empty<ExtractedTableField>()),
            },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", _prevCustomModels);
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
        if (Directory.Exists(_journalRoot)) { try { Directory.Delete(_journalRoot, true); } catch { } }
    }

    private static string TableXml(string name) =>
        "<AxTable xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>" + name + "</Name>" +
        "<Fields><AxTableField i:type=\"AxTableFieldString\"><Name>Code</Name></AxTableField></Fields></AxTable>";

    private const string TwoTables = """
        [
          { "kind": "table", "object": "FmVehicle", "op": "property", "member": "Label", "value": "@Fleet:Vehicle" },
          { "kind": "table", "object": "FmTrip", "operation": "property", "member": "Label", "value": "@Fleet:Trip" },
          { "kind": "table", "object": "FmVehicle", "operation": "property", "member": "DeveloperDocumentation", "value": "@Fleet:VehicleDoc" }
        ]
        """;

    private (ToolResult<object> Result, Bridge Capture) Run(string json, bool dryRun = false)
    {
        var steps = BatchStepParser.Parse(json, "", "", null);
        var bridge = new Bridge(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["FmVehicle"] = TableXml("FmVehicle"),
            ["FmTrip"] = TableXml("FmTrip"),
        });
        using var harness = FakeBridge.Create(bridge.Respond);
        var options = new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "",
            ObjectName = "",
            Member = "batch",
            DryRun = dryRun,
        };
        return (ObjectModifyEngine.ModifyManyCore(steps, options, _repo, harness.Client, _journalDb), bridge);
    }

    private static JsonElement Data(ToolResult<object> result) => JsonSerializer.SerializeToElement(result.Data);

    [Fact]
    public void Steps_that_name_their_objects_write_each_object_once_in_one_bridge_session()
    {
        var (result, bridge) = Run(TwoTables);

        Assert.True(result.Ok, result.Error?.Message);
        // Grouped by object: FmVehicle's two steps are one read-edit-write, FmTrip's one.
        Assert.Equal(new[] { "FmVehicle", "FmTrip" }, bridge.Writes.Select(w => w.Name).ToArray());
        Assert.Contains("<Label>@Fleet:Vehicle</Label>", bridge.Writes[0].Xml);
        Assert.Contains("<DeveloperDocumentation>@Fleet:VehicleDoc</DeveloperDocumentation>", bridge.Writes[0].Xml);
        Assert.Contains("<Label>@Fleet:Trip</Label>", bridge.Writes[1].Xml);

        var data = Data(result);
        Assert.Equal(2, data.GetProperty("objects").GetInt32());
        Assert.Equal(3, data.GetProperty("steps").GetInt32());
        Assert.Equal(2, data.GetProperty("written").GetInt32());
        Assert.Equal("d365fo undo --steps 2", data.GetProperty("undo").GetString());
        Assert.Equal(new[] { 1, 3 }, data.GetProperty("results")[0].GetProperty("steps").EnumerateArray().Select(e => e.GetInt32()).ToArray());

        // One journal entry per object, so `undo --steps 2` reverts the whole batch.
        Assert.Equal(2, ModificationJournal.ForIndex(_journalDb).List(10).Count);
    }

    [Fact]
    public void A_refused_object_is_reported_and_the_others_are_still_written()
    {
        const string json = """
            [
              { "kind": "table", "object": "FmVehicle", "operation": "property", "member": "NoSuchProperty268", "value": "x" },
              { "kind": "table", "object": "FmTrip", "operation": "property", "member": "Label", "value": "@Fleet:Trip" }
            ]
            """;

        var (result, bridge) = Run(json);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BatchPartial, result.Error!.Code);
        Assert.Equal(new[] { "FmTrip" }, bridge.Writes.Select(w => w.Name).ToArray());

        var data = Data(result);
        Assert.Equal(1, data.GetProperty("written").GetInt32());
        Assert.Equal(1, data.GetProperty("failed").GetInt32());
        var first = data.GetProperty("results")[0];
        Assert.False(first.GetProperty("ok").GetBoolean());
        Assert.Equal("FmVehicle", first.GetProperty("object").GetString());
        Assert.Equal(JsonValueKind.Object, first.GetProperty("error").ValueKind);
    }

    [Fact]
    public void When_every_object_fails_the_first_failure_code_is_returned()
    {
        const string json = """
            [ { "kind": "table", "object": "NoSuchTable268", "operation": "property", "member": "Label", "value": "x" } ]
            """;

        var (result, bridge) = Run(json);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.TableNotFound, result.Error!.Code);
        Assert.Empty(bridge.Writes);
    }

    [Fact]
    public void Dry_run_applies_every_step_in_memory_and_writes_nothing()
    {
        var (result, bridge) = Run(TwoTables, dryRun: true);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Empty(bridge.Writes);
        Assert.Empty(ModificationJournal.ForIndex(_journalDb).List(10));

        var data = Data(result);
        Assert.True(data.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, data.GetProperty("written").GetInt32());
        Assert.True(data.GetProperty("results")[0].GetProperty("data").GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public void Dry_run_still_reports_a_step_that_would_be_refused()
    {
        const string json = """
            [ { "kind": "table", "object": "FmVehicle", "operation": "property", "member": "NoSuchProperty268", "value": "x" } ]
            """;

        var (result, bridge) = Run(json, dryRun: true);

        Assert.False(result.Ok);
        Assert.Contains("dry run", result.Error!.Message);
        Assert.Empty(bridge.Writes);
    }

    [Fact]
    public void A_step_that_names_no_object_is_refused_before_the_bridge()
    {
        const string json = """
            [
              { "kind": "table", "object": "FmVehicle", "operation": "property", "member": "Label", "value": "x" },
              { "operation": "property", "member": "Label", "value": "y" }
            ]
            """;

        var (result, bridge) = Run(json);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Contains("Step(s) 2", result.Error.Message);
        Assert.Empty(bridge.Writes);
        Assert.Equal(0, bridge.Reads);
    }

    [Fact]
    public void Parser_takes_kind_object_and_model_from_the_step_and_defaults_from_the_command()
    {
        const string json = """
            [
              { "op": "property", "member": "Label", "value": "a" },
              { "kind": "enum", "object": "FmStatus", "model": "FleetCustom", "operation": "add-enum-value", "member": "Parked" }
            ]
            """;

        var steps = BatchStepParser.Parse(json, "table", "FmVehicle", null);

        Assert.Equal(("table", "FmVehicle", (string?)null), (steps[0].Kind, steps[0].ObjectName, steps[0].Model));
        Assert.Equal(ObjectModifyEngine.Operation.SetProperty, steps[0].Operation);
        Assert.Equal(("enum", "FmStatus", (string?)"FleetCustom"), (steps[1].Kind, steps[1].ObjectName, steps[1].Model));
        Assert.True(BatchStepParser.NamesObjects(json));
        Assert.False(BatchStepParser.NamesObjects("""[ { "operation": "property", "member": "Label", "value": "a" } ]"""));
    }

    [Fact]
    public void A_single_object_dry_run_reads_but_does_not_write()
    {
        var bridge = new Bridge(new Dictionary<string, string> { ["FmVehicle"] = TableXml("FmVehicle") });
        using var harness = FakeBridge.Create(bridge.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "Label",
            Value = "@Fleet:Vehicle",
            DryRun = true,
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(1, bridge.Reads);
        Assert.Empty(bridge.Writes);
        Assert.True(Data(result).GetProperty("dryRun").GetBoolean());
    }

    // ---- fake bridge -----------------------------------------------------------------

    private sealed class Bridge
    {
        private readonly IReadOnlyDictionary<string, string> _objects;

        public Bridge(IReadOnlyDictionary<string, string> objects) => _objects = objects;

        public List<(string Name, string Xml)> Writes { get; } = new();

        public int Reads { get; private set; }

        public JsonObject Respond(JsonObject request)
        {
            var method = (string?)request["method"];
            var name = (string?)request["params"]?["name"] ?? "";
            JsonObject result;
            switch (method)
            {
                case "readObjectXml":
                    Reads++;
                    result = _objects.TryGetValue(name, out var xml)
                        ? new JsonObject { ["ok"] = true, ["xml"] = xml }
                        : new JsonObject { ["ok"] = false, ["error"] = "NOT_FOUND", ["message"] = "not found" };
                    break;
                case "createObject":
                case "updateObject":
                    Writes.Add((name, (string?)request["params"]?["xml"] ?? ""));
                    result = new JsonObject { ["ok"] = true };
                    break;
                default:
                    result = new JsonObject { ["ok"] = true };
                    break;
            }
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]?.DeepClone(),
                ["result"] = result,
            };
        }
    }

    /// <summary>Routes each request line to a responder; kept per test file, as the others do.</summary>
    private sealed class FakeBridge : IDisposable
    {
        public BridgeClient Client { get; }

        private FakeBridge(BridgeClient client) => Client = client;

        public static FakeBridge Create(Func<JsonObject, JsonObject> respondWith)
        {
            var reader = new DeferredResponseReader(respondWith);
            return new FakeBridge(new BridgeClient(writer: new TeeWriter(reader), reader: reader));
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class TeeWriter : TextWriter
    {
        private readonly DeferredResponseReader _signal;
        private readonly StringBuilder _buffer = new();

        public TeeWriter(DeferredResponseReader signal) => _signal = signal;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                var line = _buffer.ToString();
                _buffer.Clear();
                _signal.OnRequest(line);
            }
            else
            {
                _buffer.Append(value);
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
        private readonly Func<JsonObject, JsonObject> _respondWith;
        private readonly Queue<string> _queued = new();

        public DeferredResponseReader(Func<JsonObject, JsonObject> respondWith) => _respondWith = respondWith;

        public void OnRequest(string requestLine)
        {
            var parsed = JsonNode.Parse(requestLine) as JsonObject
                ?? throw new InvalidOperationException("bad request JSON");
            _queued.Enqueue(_respondWith(parsed).ToJsonString());
        }

        public override string? ReadLine() => _queued.Count == 0 ? null : _queued.Dequeue();
    }
}
