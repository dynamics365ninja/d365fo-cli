using System.Xml.Linq;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// What a table keeps pointing at when one of its elements is removed or added again.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class TableElementModifyTests : IDisposable
{
    private const string Table = """
        <AxTable>
          <Name>ConVehicle</Name>
          <TitleField1>Plate</TitleField1>
          <DeleteActions>
            <AxTableDeleteAction><Name>ConLine</Name><DeleteAction>Cascade</DeleteAction><Relation></Relation><Table>ConLine</Table></AxTableDeleteAction>
          </DeleteActions>
          <FieldGroups>
            <AxTableFieldGroup><Name>AutoReport</Name><Fields /></AxTableFieldGroup>
            <AxTableFieldGroup><Name>Overview</Name><Fields>
              <AxTableFieldGroupField><DataField>Plate</DataField></AxTableFieldGroupField>
              <AxTableFieldGroupField><DataField>Make</DataField></AxTableFieldGroupField>
            </Fields></AxTableFieldGroup>
          </FieldGroups>
          <Fields>
            <AxTableField><Name>Plate</Name></AxTableField>
            <AxTableField><Name>Make</Name></AxTableField>
          </Fields>
          <Indexes>
            <AxTableIndex><Name>PlateIdx</Name><Fields><AxTableIndexField><DataField>plate</DataField></AxTableIndexField></Fields></AxTableIndex>
          </Indexes>
          <Relations>
            <AxTableRelation><Name>Owner</Name><Constraints>
              <AxTableRelationConstraint><Name>Plate</Name><Field>Plate</Field><RelatedField>Plate</RelatedField></AxTableRelationConstraint>
            </Constraints><RelatedTable>ConOwner</RelatedTable></AxTableRelation>
          </Relations>
        </AxTable>
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tblmodify-{Guid.NewGuid():N}");
    private readonly string? _prevCustomModels = Environment.GetEnvironmentVariable("D365FO_CUSTOM_MODELS");
    private readonly MetadataRepository _repo;
    private string? _written;

    public TableElementModifyTests()
    {
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", "FleetCustom");
        Directory.CreateDirectory(_root);
        _repo = new MetadataRepository(Path.Combine(_root, "index.sqlite"));
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", _prevCustomModels);
        SqlitePool.ReleaseFor(Path.Combine(_root, "index.sqlite"));
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    /// <summary><paramref name="baseXml"/> answers a read of the table itself when it differs from <paramref name="readXml"/>.</summary>
    private ToolResult<object> Modify(
        ObjectModifyEngine.ModifyRequest request, string model = "FleetCustom", string? readXml = Table,
        string? baseXml = null)
    {
        using var harness = ObjectModifyEngineTests.FakeBridge.Create(message =>
        {
            var method = (string?)message["method"];
            var result = new System.Text.Json.Nodes.JsonObject { ["ok"] = true };
            if (method == "readObjectXml")
            {
                var xml = baseXml is not null && (string?)message["params"]!["name"] == "ConVehicle" ? baseXml : readXml;
                if (xml is null) result = new() { ["ok"] = false, ["error"] = "NOT_FOUND" };
                else result["xml"] = xml;
            }
            if (method is "updateObject" or "createObject") _written = (string?)message["params"]!["xml"];
            return new() { ["jsonrpc"] = "2.0", ["id"] = message["id"]?.DeepClone(), ["result"] = result };
        });
        return ObjectModifyEngine.ModifyCore(request with { Model = model }, _repo, harness.Client,
            Path.Combine(_root, "journal.sqlite"));
    }

    private static ObjectModifyEngine.ModifyRequest Request(ObjectModifyEngine.Operation operation, string member)
        => new() { Operation = operation, Kind = "table", ObjectName = "ConVehicle", Member = member };

    private XElement Written => XDocument.Parse(_written!).Root!;

    private static string[] FieldsOf(XElement root, string collection, string group)
        => root.Element(collection)!.Elements().Single(g => g.Element("Name")!.Value == group)
            .Descendants("DataField").Select(d => d.Value).ToArray();

    [Fact]
    public void Removing_a_field_takes_it_out_of_the_field_groups_and_names_what_else_uses_it()
    {
        var result = Modify(Request(ObjectModifyEngine.Operation.RemoveField, "Plate"));

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(["Make"], FieldsOf(Written, "FieldGroups", "Overview"));
        var warnings = string.Join("\n", result.Warnings ?? []);
        Assert.Contains("index 'PlateIdx'", warnings);
        Assert.Contains("relation 'Owner'", warnings);
        Assert.Contains("TitleField1", warnings);
        // What changes how the table behaves is reported, not rewritten.
        Assert.Equal("plate", Written.Element("Indexes")!.Descendants("DataField").Single().Value);
    }

    [Fact]
    public void A_second_delete_action_for_the_same_table_is_refused()
    {
        var result = Modify(Request(ObjectModifyEngine.Operation.AddDeleteAction, "ConLine")
            with { RelatedTable = "ConLine", DeleteAction = "Restricted" });

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.AlreadyExists, result.Error!.Code);
        Assert.Null(_written);
    }

    [Fact]
    public void Adding_fields_to_an_existing_group_puts_in_the_missing_ones()
    {
        var result = Modify(Request(ObjectModifyEngine.Operation.AddFieldGroup, "AutoReport")
            with { Fields = ["Plate", "Make"] });

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(["Plate", "Make"], FieldsOf(Written, "FieldGroups", "AutoReport"));
        Assert.Single(Written.Element("FieldGroups")!.Elements(), g => g.Element("Name")!.Value == "AutoReport");
    }

    [Fact]
    public void A_group_that_already_holds_every_field_is_reported_as_existing()
    {
        var result = Modify(Request(ObjectModifyEngine.Operation.AddFieldGroup, "Overview")
            with { Fields = ["Plate"] });

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.AlreadyExists, result.Error!.Code);
    }

    [Fact]
    public void On_an_extension_a_base_tables_auto_group_is_extended_not_declared_again()
    {
        var result = Modify(
            Request(ObjectModifyEngine.Operation.AddFieldGroup, "AutoReport") with { Fields = ["ConPlate"] },
            model: "ApplicationSuite", readXml: null);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("AxTableExtension", Written.Name.LocalName);
        Assert.Equal(["ConPlate"], FieldsOf(Written, "FieldGroupExtensions", "AutoReport"));
        Assert.Empty(Written.Element("FieldGroups")?.Elements() ?? []);
    }

    [Fact]
    public void On_an_extension_any_group_of_the_base_table_is_extended()
    {
        var result = Modify(
            Request(ObjectModifyEngine.Operation.AddFieldGroup, "overview") with { Fields = ["ConPlate"], Label = "Mine" },
            model: "ApplicationSuite", readXml: null, baseXml: Table);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(["ConPlate"], FieldsOf(Written, "FieldGroupExtensions", "Overview"));
        Assert.Empty(Written.Element("FieldGroups")?.Elements() ?? []);
        // A group extension has no label of its own.
        Assert.Contains(result.Warnings ?? [], w => w.Contains("label", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void On_an_extension_a_group_the_base_table_does_not_have_is_declared()
    {
        var result = Modify(
            Request(ObjectModifyEngine.Operation.AddFieldGroup, "ConFleetGroup") with { Fields = ["ConPlate"] },
            model: "ApplicationSuite", readXml: null, baseXml: Table);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(["ConPlate"], FieldsOf(Written, "FieldGroups", "ConFleetGroup"));
        Assert.Empty(Written.Element("FieldGroupExtensions")?.Elements() ?? []);
    }

    [Fact]
    public void A_group_is_not_declared_when_the_base_table_cannot_be_read()
    {
        var result = Modify(
            Request(ObjectModifyEngine.Operation.AddFieldGroup, "Overview") with { Fields = ["ConPlate"] },
            model: "ApplicationSuite", readXml: null);

        Assert.False(result.Ok);
        Assert.Null(_written);
    }

    [Fact]
    public void An_extended_group_can_be_removed_from_the_extension()
    {
        const string extension = """
            <AxTableExtension><Name>ConVehicle.FleetCustom</Name><FieldGroupExtensions>
              <AxTableFieldGroupExtension><Name>Overview</Name><Fields>
                <AxTableFieldGroupField><DataField>ConPlate</DataField></AxTableFieldGroupField>
              </Fields></AxTableFieldGroupExtension>
            </FieldGroupExtensions><FieldGroups /></AxTableExtension>
            """;
        _repo.ApplyExtract(ExtractBatch.Empty("FleetCustom") with
        {
            IsCustom = true,
            Extensions = [new ExtractedObjectExtension("Table", "ConVehicle", "ConVehicle.FleetCustom", null)],
        });

        var result = Modify(Request(ObjectModifyEngine.Operation.RemoveFieldGroup, "Overview"),
            model: "ApplicationSuite", readXml: extension, baseXml: Table);

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Empty(Written.Element("FieldGroupExtensions")!.Elements());
    }
}
