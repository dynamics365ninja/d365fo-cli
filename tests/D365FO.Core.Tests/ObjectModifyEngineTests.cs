using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The structured-modify surface beyond method bodies — <c>modify property</c>,
/// <c>add-field</c>, <c>add-enum-value</c>, <c>add-control</c> — plus the two
/// behaviours that make it safe to point at a real installation: writes to a
/// non-custom model are redirected to an extension, and every write is journaled
/// so <c>d365fo undo</c> can revert it.
///
/// Exercised against a fake in-process bridge, so no D365FO VM is involved.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class ObjectModifyEngineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"objmodify-{Guid.NewGuid():N}.sqlite");
    private readonly string _journalRoot = Path.Combine(Path.GetTempPath(), $"objmodify-j-{Guid.NewGuid():N}");
    private readonly string _journalDb;
    private readonly MetadataRepository _repo;
    private readonly string? _prevCustomModels;

    public ObjectModifyEngineTests()
    {
        _journalDb = Path.Combine(_journalRoot, "index.sqlite");
        _prevCustomModels = Environment.GetEnvironmentVariable("D365FO_CUSTOM_MODELS");
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", "FleetCustom");
        Directory.CreateDirectory(_journalRoot);

        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();

        // A Microsoft-owned model (not writable) …
        _repo.ApplyExtract(ExtractBatch.Empty("ApplicationSuite") with
        {
            Publisher = "Microsoft",
            Layer = "app",
            IsCustom = false,
            Tables = new[] { new ExtractedTable("CustTable", null, "x", Array.Empty<ExtractedTableField>()) },
            Enums = new[] { new ExtractedEnum("CustAccountStatus", null, Array.Empty<ExtractedEnumValue>()) },
            Forms = new[] { new ExtractedForm("FmVehicleList", "x", Array.Empty<ExtractedFormDataSource>()) },
        });

        // … and a custom model this installation owns.
        _repo.ApplyExtract(ExtractBatch.Empty("FleetCustom") with
        {
            Publisher = "Contoso",
            Layer = "usr",
            IsCustom = true,
            Tables = new[] { new ExtractedTable("FmVehicle", null, "x", Array.Empty<ExtractedTableField>()) },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", _prevCustomModels);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
        if (Directory.Exists(_journalRoot)) { try { Directory.Delete(_journalRoot, true); } catch { } }
    }

    private const string CustomTableXml =
        "<AxTable xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>FmVehicle</Name>" +
        "<Fields><AxTableField i:type=\"AxTableFieldString\"><Name>VIN</Name></AxTableField></Fields></AxTable>";

    private const string FormXml =
        "<AxForm><Name>FmVehicleList</Name><Design><Controls>" +
        "<AxFormControl i:type=\"AxFormGridControl\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">" +
        "<Name>Grid</Name><Type>Grid</Type><Controls /></AxFormControl>" +
        "</Controls></Design></AxForm>";

    // ---- base-object writes (custom model) ----------------------------------

    [Fact]
    public void Add_field_writes_the_concrete_subtype_into_the_base_table()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "LicensePlate",
            Type = "Name",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("updateObject", capture.WriteVerb);
        Assert.Equal("table", (string?)capture.WriteArgs!["kind"]);
        Assert.Equal("FmVehicle", (string?)capture.WriteArgs!["name"]);
        Assert.Equal("FleetCustom", (string?)capture.WriteArgs!["model"]);

        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var field = written.Descendants("AxTableField")
            .Single(e => e.Element("Name")?.Value == "LicensePlate");
        var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");
        // A plain <AxTableField> is an abstract type — the discriminator is mandatory.
        Assert.Equal("AxTableFieldString", (string?)field.Attribute(xsi + "type"));
        Assert.Equal("Name", field.Element("ExtendedDataType")!.Value);
    }

    [Fact]
    public void Add_field_rejects_a_duplicate_rather_than_writing_two()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "VIN",
            Type = "Name",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.AlreadyExists, result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void Set_property_replaces_an_existing_value_and_reports_the_old_one()
    {
        var capture = new BridgeCapture(
            "<AxTable><Name>FmVehicle</Name><Label>@SYS1</Label></AxTable>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "Label",
            Value = "@Fleet:Vehicles",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal("@Fleet:Vehicles", written.Root!.Element("Label")!.Value);
    }

    [Fact]
    public void Set_property_adds_the_element_when_it_is_absent()
    {
        var capture = new BridgeCapture("<AxTable><Name>FmVehicle</Name></AxTable>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "TableGroup",
            Value = "Main",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal("Main", written.Root!.Element("TableGroup")!.Value);
    }

    [Fact]
    public void Add_control_appends_a_bound_control_to_the_named_container()
    {
        var capture = new BridgeCapture(FormXml);
        using var harness = FakeBridge.Create(capture.Respond);

        // The form is in a Microsoft model per the index; --model pins it to the custom
        // one so this test exercises the control edit, not the extension fallback.
        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddControl,
            Kind = "form",
            ObjectName = "FmVehicleList",
            Member = "Grid_Make",
            Type = "String",
            Parent = "Grid",
            DataSource = "FmVehicle",
            DataField = "Make",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var control = written.Descendants()
            .Single(e => e.Name.LocalName == "AxFormControl" && e.Elements().Any(c => c.Name.LocalName == "Name" && c.Value == "Grid_Make"));
        Assert.Equal("Grid", control.Parent!.Parent!.Elements().First(e => e.Name.LocalName == "Name").Value);
        Assert.Equal("Make", control.Elements().First(e => e.Name.LocalName == "DataField").Value);
    }

    [Fact]
    public void Add_control_reports_an_unknown_parent_instead_of_writing_at_the_root()
    {
        var capture = new BridgeCapture(FormXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddControl,
            Kind = "form",
            ObjectName = "FmVehicleList",
            Member = "Foo",
            Type = "String",
            Parent = "NoSuchContainer",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.ControlNotFound, result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    // ---- extension fallback --------------------------------------------------

    [Fact]
    public void A_table_in_a_microsoft_model_is_extended_not_modified_in_place()
    {
        var capture = new BridgeCapture(readXml: null); // extension does not exist yet
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = "CustTable",
            Member = "FleetRating",
            Type = "Name",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        // A new extension object is *created*, never an update of CustTable itself.
        Assert.Equal("createObject", capture.WriteVerb);
        Assert.Equal("tableExtension", (string?)capture.WriteArgs!["kind"]);
        Assert.Equal("CustTable.Extension", (string?)capture.WriteArgs!["name"]);
        Assert.Equal("FleetCustom", (string?)capture.WriteArgs!["model"]);

        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal("AxTableExtension", written.Root!.Name.LocalName);
        Assert.Equal("CustTable.Extension", written.Root.Element("Name")!.Value);
        Assert.Contains(written.Descendants("AxTableField"), e => e.Element("Name")?.Value == "FleetRating");

        // …and the caller is told, rather than silently redirected.
        Assert.Contains(result.Warnings!, w => w.Contains("extension") && w.Contains("CustTable.Extension"));
    }

    [Fact]
    public void An_enum_in_a_microsoft_model_is_extended_without_a_hard_coded_ordinal()
    {
        var capture = new BridgeCapture(readXml: null);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddEnumValue,
            Kind = "enum",
            ObjectName = "CustAccountStatus",
            Member = "Impounded",
            Label = "@Fleet:Impounded",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("enumExtension", (string?)capture.WriteArgs!["kind"]);

        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var value = written.Descendants("AxEnumValue").Single();
        Assert.Equal("Impounded", value.Element("Name")!.Value);
        // An extensible enum is position-based; a written <Value> breaks when another
        // model inserts a member ahead of this one.
        Assert.Null(value.Element("Value"));
    }

    [Fact]
    public void An_explicit_extension_suffix_forces_the_extension_path_for_a_custom_object()
    {
        var capture = new BridgeCapture(readXml: null);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "Telemetry",
            Type = "Name",
            ExtensionSuffix = "Telematics",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("FmVehicle.Telematics", (string?)capture.WriteArgs!["name"]);
    }

    [Fact]
    public void Require_extension_fails_for_a_kind_that_cannot_be_extended()
    {
        var capture = new BridgeCapture(readXml: null);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "class",
            ObjectName = "SomeClass",
            Member = "Label",
            Value = "x",
            Model = "ApplicationSuite",
            RequireExtension = true,
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    // ---- journaling ----------------------------------------------------------

    [Fact]
    public void An_update_is_journaled_with_the_exact_pre_image_so_undo_can_restore_it()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table", ObjectName = "FmVehicle", Member = "LicensePlate", Type = "Name",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);

        var stored = Assert.Single(ModificationJournal.ForIndex(_journalDb).List(10));
        Assert.Equal(JournalOperation.Update, stored.Operation);
        Assert.Equal(JournalWritePath.Bridge, stored.WritePath);
        Assert.Equal("table", stored.Kind);
        Assert.Equal("FmVehicle", stored.ObjectName);
        Assert.Equal("FleetCustom", stored.Model);
        // Undo replays this straight back through updateObject — it has to be the
        // byte-exact document the bridge handed us, not a re-serialization of it.
        Assert.Equal(CustomTableXml, stored.PreImage);
        Assert.False(stored.IsTombstone);
    }

    [Fact]
    public void A_newly_created_extension_is_journaled_as_a_tombstone()
    {
        var capture = new BridgeCapture(readXml: null);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table", ObjectName = "CustTable", Member = "FleetRating", Type = "Name",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);

        var stored = Assert.Single(ModificationJournal.ForIndex(_journalDb).List(10));
        // Undoing a create means deleting the object; there is no prior state to keep,
        // and a non-tombstone entry would make undo try to restore null XML.
        Assert.Equal(JournalOperation.Create, stored.Operation);
        Assert.True(stored.IsTombstone);
        Assert.Null(stored.PreImage);
        Assert.Equal("CustTable.Extension", stored.ObjectName);
    }

    [Fact]
    public void Modify_method_is_journaled_too()
    {
        // This was the one write path in the CLI that captured a pre-image and then
        // discarded it, leaving `d365fo undo` unable to revert a method edit.
        const string classXml =
            "<AxClass><Name>FmVehicleService</Name><SourceCode><Methods>" +
            "<Method><Name>run</Name><Source><![CDATA[\n    void run()\n    {\n    }\n]]></Source></Method>" +
            "</Methods></SourceCode></AxClass>";

        var capture = new BridgeCapture(classXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = MethodModifyEngine.ModifyCore(
            new MethodModifyEngine.ModifyRequest("class", "FmVehicleService", "run", "// replaced", "FleetCustom"),
            null, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);

        var stored = Assert.Single(ModificationJournal.ForIndex(_journalDb).List(10));
        Assert.Equal(JournalOperation.Update, stored.Operation);
        Assert.Equal("FmVehicleService", stored.ObjectName);
        Assert.Equal(classXml, stored.PreImage);
    }

    // ---- validation ----------------------------------------------------------

    [Theory]
    [InlineData(ObjectModifyEngine.Operation.AddField, "form", "--edt")]
    [InlineData(ObjectModifyEngine.Operation.AddEnumValue, "table", "applies to enums")]
    [InlineData(ObjectModifyEngine.Operation.AddControl, "table", "applies to forms")]
    public void An_operation_is_rejected_for_the_wrong_kind(ObjectModifyEngine.Operation op, string kind, string _)
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = op, Kind = kind, ObjectName = "X", Member = "Y",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void An_unknown_object_with_no_model_override_is_reported_before_the_bridge_is_touched()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table", ObjectName = "NoSuchTable", Member = "Label", Value = "x",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.TableNotFound, result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    // ---- fake bridge ---------------------------------------------------------

    /// <summary>
    /// Answers <c>readObjectXml</c> with a fixed document (or NOT_FOUND when
    /// <c>readXml</c> is null) and records the create/update call.
    /// </summary>
    private sealed class BridgeCapture
    {
        private readonly string? _readXml;

        public BridgeCapture(string? readXml) => _readXml = readXml;

        public string? WriteVerb { get; private set; }

        public JsonObject? WriteArgs { get; private set; }

        public JsonObject Respond(JsonObject request)
        {
            var method = (string?)request["method"];
            JsonObject result;
            switch (method)
            {
                case "readObjectXml":
                    result = _readXml is null
                        ? new JsonObject { ["ok"] = false, ["error"] = "NOT_FOUND", ["message"] = "not found" }
                        : new JsonObject { ["ok"] = true, ["xml"] = _readXml };
                    break;
                case "createObject":
                case "updateObject":
                    WriteVerb = method;
                    WriteArgs = (JsonObject?)request["params"]?.DeepClone();
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
