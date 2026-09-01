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
            Edts = new[] { new ExtractedEdt("CustAccount", null, "String", null, 20) },
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
        SqlitePool.ReleaseFor(_dbPath);
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

    // ---- kind coverage, batching, query and security --------------------------

    [Fact]
    public void A_kind_the_bridge_can_write_is_no_longer_refused_by_a_hand_written_list()
    {
        // SupportedKinds was five entries while the bridge resolves 41 from the registry, so a
        // query - which it can write perfectly well - came back "unsupported kind".
        var capture = new BridgeCapture("<AxQuery><Name>FmVehicleQuery</Name></AxQuery>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "query",
            ObjectName = "FmVehicleQuery",
            Member = "Title",
            Value = "@SYS1",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("updateObject", capture.WriteVerb);
    }

    [Fact]
    public void An_unknown_kind_names_the_near_misses_instead_of_all_41()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "securityprivilage",
            ObjectName = "X",
            Member = "Label",
            Value = "@SYS1",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal("BAD_INPUT", result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void A_batch_applies_every_step_in_one_write()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "batch",
            Model = "FleetCustom",
            Batch = new[]
            {
                new ObjectModifyEngine.ModifyRequest
                {
                    Operation = ObjectModifyEngine.Operation.AddField,
                    Kind = "table", ObjectName = "FmVehicle", Member = "Note", Type = "Notes",
                },
                new ObjectModifyEngine.ModifyRequest
                {
                    Operation = ObjectModifyEngine.Operation.AddIndex,
                    Kind = "table", ObjectName = "FmVehicle", Member = "NoteIdx",
                    Fields = new[] { "Note" },
                },
            },
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        // One write, not two: that is the whole point of batching.
        Assert.Equal(1, capture.WriteCount);

        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Contains(written.Descendants("AxTableField"), e => e.Element("Name")?.Value == "Note");
        Assert.Contains(written.Descendants("AxTableIndex"), e => e.Element("Name")?.Value == "NoteIdx");
    }

    [Fact]
    public void A_refused_step_discards_the_whole_batch_and_writes_nothing()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "batch",
            Model = "FleetCustom",
            Batch = new[]
            {
                new ObjectModifyEngine.ModifyRequest
                {
                    Operation = ObjectModifyEngine.Operation.AddField,
                    Kind = "table", ObjectName = "FmVehicle", Member = "Note", Type = "Notes",
                },
                // Second step refuses: the field does not exist on the table.
                new ObjectModifyEngine.ModifyRequest
                {
                    Operation = ObjectModifyEngine.Operation.AddIndex,
                    Kind = "table", ObjectName = "FmVehicle", Member = "BadIdx",
                    Fields = new[] { "NoSuchField" },
                },
            },
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        // Nothing written at all - the first step's field must not reach the AOT on its own.
        Assert.Null(capture.WriteVerb);
        Assert.Contains("NOTHING was written", result.Error!.Hint);
    }

    [Fact]
    public void Add_query_range_refuses_to_guess_between_two_datasources()
    {
        var capture = new BridgeCapture(
            "<AxQuery><Name>FmVehicleQuery</Name><DataSources>" +
            "<AxQuerySimpleDataSource><Name>FmVehicle</Name></AxQuerySimpleDataSource>" +
            "<AxQuerySimpleDataSource><Name>FmRental</Name></AxQuerySimpleDataSource>" +
            "</DataSources></AxQuery>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddQueryRange,
            Kind = "query",
            ObjectName = "FmVehicleQuery",
            Member = "VehicleId",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        // A range on the wrong datasource returns the wrong rows silently, so guessing is worse
        // than refusing.
        Assert.False(result.Ok);
        Assert.Contains("--data-source", result.Error!.Message);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void Add_query_range_uses_the_only_datasource_without_being_told()
    {
        var capture = new BridgeCapture(
            "<AxQuery><Name>FmVehicleQuery</Name><DataSources>" +
            "<AxQuerySimpleDataSource><Name>FmVehicle</Name></AxQuerySimpleDataSource>" +
            "</DataSources></AxQuery>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddQueryRange,
            Kind = "query",
            ObjectName = "FmVehicleQuery",
            Member = "VehicleId",
            RangeValue = "!Closed",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var range = written.Descendants("AxQuerySimpleDataSourceRange").Single();
        Assert.Equal("VehicleId", range.Element("Field")!.Value);
        Assert.Equal("!Closed", range.Element("Value")!.Value);
    }

    [Fact]
    public void Add_entry_point_writes_a_grant_not_an_access_level()
    {
        var capture = new BridgeCapture(
            "<AxSecurityPrivilege><Name>FmVehicleMaintain</Name></AxSecurityPrivilege>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddEntryPoint,
            Kind = "securityprivilege",
            ObjectName = "FmVehicleMaintain",
            Member = "FmVehicleListPage",
            EntryPointType = "MenuItemDisplay",
            Access = "Update",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var reference = written.Descendants("AxSecurityEntryPointReference").Single();

        // There is no AccessLevel member in the security model - writing one grants nothing and
        // reads as a deliberate no-access privilege.
        Assert.Null(reference.Element("AccessLevel"));
        var grant = reference.Element("Grant")!;
        Assert.Equal(new[] { "Read", "Update" }, grant.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("MenuItemDisplay", reference.Element("ObjectType")!.Value);
    }

    // ---- contract order (the write path had no canonicalisation) --------------

    [Fact]
    public void A_created_collection_lands_in_contract_order_not_at_the_end()
    {
        // AxTable member order ends: DeleteActions, FieldGroups, Fields, FullTextIndexes,
        // Indexes, Mappings, Relations. This table declares Indexes but no Fields, so a naive
        // append puts <Fields> AFTER <Indexes> - and DataContractSerializer skips a child that
        // arrives out of turn, so the field is DROPPED on read while the write reports ok.
        var capture = new BridgeCapture(
            "<AxTable><Name>FmVehicle</Name>" +
            "<Indexes><AxTableIndex><Name>Idx</Name></AxTableIndex></Indexes></AxTable>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "LicensePlate",
            Type = "Name",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal(
            new[] { "Name", "Fields", "Indexes" },
            written.Root!.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Fact]
    public void A_property_added_to_the_root_lands_in_contract_order()
    {
        // SetProperty inserted straight after <Name>, which for most properties is too early:
        // CacheLookup sorts long after Label in the AxTable contract.
        var capture = new BridgeCapture("<AxTable><Name>FmVehicle</Name><Label>@SYS1</Label></AxTable>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "CacheLookup",
            Value = "Found",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal(
            new[] { "Name", "Label", "CacheLookup" },
            written.Root!.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    // ---- table structure ------------------------------------------------------

    [Fact]
    public void Add_index_is_unique_by_default_and_keeps_the_field_order_given()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddIndex,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "VehicleIdx",
            Fields = new[] { "VIN", "RecId" },
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var index = written.Descendants("AxTableIndex").Single(e => e.Element("Name")?.Value == "VehicleIdx");
        Assert.Equal("No", index.Element("AllowDuplicates")!.Value);
        Assert.Equal(
            new[] { "VIN", "RecId" },
            index.Element("Fields")!.Elements("AxTableIndexField").Select(f => f.Element("DataField")!.Value).ToArray());
    }

    [Fact]
    public void Add_index_refuses_a_field_the_table_does_not_have()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddIndex,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "BadIdx",
            Fields = new[] { "NoSuchField" },
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal("FIELD_NOT_FOUND", result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void Add_relation_pins_the_concrete_constraint_subtype()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddRelation,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "CustAccount",
            RelatedTable = "CustTable",
            RelatedField = "AccountNum",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var constraint = written.Descendants("AxTableRelationConstraint").Single();
        var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");
        Assert.Equal("AxTableRelationConstraintField", (string?)constraint.Attribute(xsi + "type"));
        Assert.Equal("AccountNum", constraint.Element("RelatedField")!.Value);
    }

    [Fact]
    public void Add_delete_action_refuses_an_action_the_platform_does_not_define()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddDeleteAction,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "FmVehicleLine",
            RelatedTable = "FmVehicleLine",
            DeleteAction = "CascadeAll",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal("BAD_INPUT", result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void Rename_field_rewrites_the_index_that_names_it()
    {
        var capture = new BridgeCapture(
            "<AxTable><Name>FmVehicle</Name>" +
            "<Fields><AxTableField><Name>VehicleId</Name></AxTableField></Fields>" +
            "<Indexes><AxTableIndex><Name>Idx</Name><Fields>" +
            "<AxTableIndexField><DataField>VehicleId</DataField></AxTableIndexField>" +
            "</Fields></AxTableIndex></Indexes></AxTable>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.RenameField,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "VehicleId",
            NewName = "VehicleNumber",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal("VehicleNumber", written.Descendants("AxTableField").Single().Element("Name")!.Value);
        // Leaving the index behind gives a table that cannot resolve its own index.
        Assert.Equal("VehicleNumber", written.Descendants("AxTableIndexField").Single().Element("DataField")!.Value);
    }

    [Fact]
    public void Remove_index_refuses_a_name_that_is_not_there_and_writes_nothing()
    {
        var capture = new BridgeCapture(CustomTableXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.RemoveIndex,
            Kind = "table",
            ObjectName = "FmVehicle",
            Member = "NoSuchIdx",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal("MEMBER_NOT_FOUND", result.Error!.Code);
        Assert.Null(capture.WriteVerb);
    }

    [Fact]
    public void Remove_control_takes_its_nested_controls_with_it()
    {
        var capture = new BridgeCapture(
            "<AxForm><Name>FmVehicleForm</Name><Design>" +
            "<Controls><AxFormControlGroup><Name>Grp</Name><Controls>" +
            "<AxFormControlString><Name>Inner1</Name></AxFormControlString>" +
            "<AxFormControlString><Name>Inner2</Name></AxFormControlString>" +
            "</Controls></AxFormControlGroup></Controls></Design></AxForm>");
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.RemoveControl,
            Kind = "form",
            ObjectName = "FmVehicleForm",
            Member = "Grp",
            Model = "FleetCustom",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.DoesNotContain(written.Descendants(),
            e => e.Name.LocalName.StartsWith("AxFormControl", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_operation_on_a_form_is_refused_by_the_name_the_caller_typed()
    {
        var capture = new BridgeCapture(FormXml);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddIndex,
            Kind = "form",
            ObjectName = "FmVehicleForm",
            Member = "Idx",
            Fields = new[] { "A" },
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Contains("add-index", result.Error!.Message);
        Assert.Null(capture.WriteVerb);
    }

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
        Assert.Equal("tableextension", (string?)capture.WriteArgs!["kind"]);
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
        Assert.Equal("enumextension", (string?)capture.WriteArgs!["kind"]);

        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        var value = written.Descendants("AxEnumValue").Single();
        Assert.Equal("Impounded", value.Element("Name")!.Value);
        // An extensible enum is position-based; a written <Value> breaks when another
        // model inserts a member ahead of this one.
        Assert.Null(value.Element("Value"));
    }

    [Fact]
    public void An_edt_extension_is_scaffolded_with_the_root_element_the_metamodel_declares()
    {
        var capture = new BridgeCapture(readXml: null);
        using var harness = FakeBridge.Create(capture.Respond);

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.SetProperty,
            Kind = "edt",
            ObjectName = "CustAccount",
            Member = "StringSize",
            Value = "30",
        }, _repo, harness.Client, _journalDb);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("edtextension", (string?)capture.WriteArgs!["kind"]);

        // The table this replaced had no "edt" row, so the root fell through to
        // $"Ax{kind}Extension" and emitted a mis-cased <AxedtExtension>.
        var written = XDocument.Parse((string)capture.WriteArgs!["xml"]!);
        Assert.Equal("AxEdtExtension", written.Root!.Name.LocalName);
    }

    [Fact]
    public void An_invalid_kind_from_the_bridge_points_at_the_bridge_build_not_the_platform()
    {
        using var harness = FakeBridge.Create(request =>
        {
            // An older bridge: its kind table predates the extension entries, so it
            // rejects the redirect target on whichever leg reaches it first.
            var result = new JsonObject
            {
                ["ok"] = false,
                ["error"] = "INVALID_KIND",
                ["message"] = "kind must be one of: class, table, edt, enum, form",
            };
            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = request["id"]?.DeepClone(),
                ["result"] = result,
            };
        });

        var result = ObjectModifyEngine.ModifyCore(new ObjectModifyEngine.ModifyRequest
        {
            Operation = ObjectModifyEngine.Operation.AddField,
            Kind = "table",
            ObjectName = "CustTable",
            Member = "FleetRating",
            Type = "Name",
            ExtensionSuffix = "Fleet",
        }, _repo, harness.Client, _journalDb);

        Assert.False(result.Ok);
        Assert.Equal("INVALID_KIND", result.Error!.Code);
        // #171 blamed the platform for a collection it does in fact expose. The
        // registry both halves read says this kind is writable, so the two
        // binaries disagreeing is the only thing left.
        Assert.Contains("older build", result.Error.Hint);
        Assert.Contains("TableExtensions", result.Error.Hint);
        Assert.DoesNotContain("platform build", result.Error.Hint);
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

        /// <summary>How many writes reached the bridge. A batch must produce exactly one.</summary>
        public int WriteCount { get; private set; }

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
                    WriteCount++;
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
