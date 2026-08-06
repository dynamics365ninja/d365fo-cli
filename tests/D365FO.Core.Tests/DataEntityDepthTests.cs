using System.Xml.Linq;
using D365FO.Core.Metadata;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #162, data-entity half: relations, computed columns, and the DMF staging table that
/// <c>DataManagementEnabled=Yes</c> promises and nothing used to generate.
/// </summary>
public class DataEntityDepthTests
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static IReadOnlyList<XppViolation> Shape(XDocument doc)
    {
        var v = new List<XppViolation>();
        var xml = doc.ToString(SaveOptions.DisableFormatting);
        ObjectShapeRules.Check(xml, v);
        ContractShapeRules.Check(xml, v);
        return v;
    }

    private static XDocument Entity(
        IEnumerable<EntityRelationSpec>? relations = null,
        IEnumerable<EntityComputedFieldSpec>? computed = null)
        => XppScaffolder.DataEntity(
            "ConVehicleEntity", "FmVehicle",
            fields: [new EntityFieldSpec("VehicleId", "VehicleId", true)],
            keyFields: ["VehicleId"],
            relations: relations,
            computedFields: computed);

    // ── relations ────────────────────────────────────────────────────────────

    [Fact]
    public void A_relation_joins_two_entities_on_a_field_pair()
    {
        var doc = Entity(relations: [
            new EntityRelationSpec(
                "Customer", "CustCustomerV3Entity",
                [new EntityRelationConstraintSpec("CustomerAccount", "CustomerAccount")],
                Cardinality: "ZeroOne",
                RelationshipType: "Association"),
        ]);

        var relation = Assert.Single(doc.Root!.Element("Relations")!.Elements());
        Assert.Equal("AxDataEntityViewRelation", relation.Name.LocalName);
        Assert.Equal("Customer", relation.Element("Name")!.Value);
        Assert.Equal("CustCustomerV3Entity", relation.Element("RelatedDataEntity")!.Value);
        Assert.Equal("ZeroOne", relation.Element("Cardinality")!.Value);
        Assert.Equal("Association", relation.Element("RelationshipType")!.Value);

        var constraint = Assert.Single(relation.Element("Constraints")!.Elements());
        Assert.Equal("AxDataEntityViewRelationConstraint", constraint.Name.LocalName);
        Assert.Equal("AxDataEntityViewRelationConstraintField", constraint.Attribute(Xsi + "type")!.Value);
        Assert.Equal("CustomerAccount", constraint.Element("Field")!.Value);
        Assert.Equal("CustomerAccount", constraint.Element("RelatedField")!.Value);

        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void A_constraint_without_its_discriminator_would_lose_both_fields()
    {
        // The reason the i:type above is not decoration: the base constraint declares Name and
        // Tags only, so an unpinned constraint reads back joining on nothing.
        var baseContract = MetadataContracts.Find("AxDataEntityViewRelationConstraint")!;
        Assert.False(MetadataContracts.AcceptsMember(baseContract, "Field"));
        Assert.False(MetadataContracts.AcceptsMember(baseContract, "RelatedField"));

        var fieldContract = MetadataContracts.Find("AxDataEntityViewRelationConstraintField")!;
        Assert.True(MetadataContracts.AcceptsMember(fieldContract, "Field"));
        Assert.True(MetadataContracts.AcceptsMember(fieldContract, "RelatedField"));
    }

    [Fact]
    public void An_entity_with_no_relations_emits_no_collection()
    {
        Assert.Null(Entity().Root!.Element("Relations"));
    }

    // ── computed columns ─────────────────────────────────────────────────────

    [Fact]
    public void A_computed_field_is_an_unmapped_field_bound_to_a_method()
    {
        var doc = Entity(computed: [
            new EntityComputedFieldSpec("VehicleDisplayName", "computeVehicleDisplayName", "Name"),
        ]);

        var computed = doc.Root!.Element("Fields")!.Elements()
            .Single(f => f.Element("Name")!.Value == "VehicleDisplayName");

        Assert.Equal("AxDataEntityViewUnmappedField", computed.Attribute(Xsi + "type")!.Value);
        Assert.Equal("computeVehicleDisplayName", computed.Element("ComputedFieldMethod")!.Value);
        Assert.Equal("Name", computed.Element("ExtendedDataType")!.Value);
        Assert.Equal("Yes", computed.Element("IsComputedField")!.Value);

        // And the mapped field beside it is untouched.
        var mapped = doc.Root.Element("Fields")!.Elements()
            .Single(f => f.Element("Name")!.Value == "VehicleId");
        Assert.Equal("AxDataEntityViewMappedField", mapped.Attribute(Xsi + "type")!.Value);

        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void The_mapped_field_type_has_no_computed_members_at_all()
    {
        // Which is why the computed field has to be the unmapped subtype rather than a mapped
        // field with extra elements: on AxDataEntityViewMappedField those members do not exist.
        var mapped = MetadataContracts.Find("AxDataEntityViewMappedField")!;
        Assert.False(MetadataContracts.AcceptsMember(mapped, "ComputedFieldMethod"));
        Assert.False(MetadataContracts.AcceptsMember(mapped, "IsComputedField"));
    }

    // ── staging table ────────────────────────────────────────────────────────

    [Fact]
    public void The_staging_table_carries_the_entity_fields_and_the_DMF_bookkeeping_columns()
    {
        var doc = XppScaffolder.EntityStagingTable(
            "ConVehicleEntityStaging",
            [new EntityFieldSpec("VehicleId", "VehicleId", true), new EntityFieldSpec("Plate", null, false)]);

        Assert.Equal("AxTable", doc.Root!.Name.LocalName);

        var names = doc.Root.Element("Fields")!.Elements()
            .Select(f => f.Element("Name")!.Value).ToArray();

        Assert.Contains("VehicleId", names);
        Assert.Contains("Plate", names);
        foreach (var required in new[] { "DefinitionGroup", "ExecutionId", "IsSelected", "TransferStatus" })
            Assert.Contains(required, names);

        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void The_staging_table_types_its_framework_columns_the_way_the_DMF_expects()
    {
        // Ground-truthed against 398 of 400 shipped *Staging tables sampled from
        // PackagesLocalDirectory on a real AOS.
        var doc = XppScaffolder.EntityStagingTable("ConVehicleEntityStaging", []);
        var fields = doc.Root!.Element("Fields")!.Elements()
            .ToDictionary(f => f.Element("Name")!.Value);

        Assert.Equal("DMFDefinitionGroupName", fields["DefinitionGroup"].Element("ExtendedDataType")!.Value);
        Assert.Equal("AxTableFieldString", fields["DefinitionGroup"].Attribute(Xsi + "type")!.Value);
        Assert.Equal("DMFExecutionId", fields["ExecutionId"].Element("ExtendedDataType")!.Value);

        // IsSelected and TransferStatus are enum columns, not EDT ones.
        foreach (var (field, enumName) in new[] { ("IsSelected", "DMFIsSelected"), ("TransferStatus", "DMFTransferStatus") })
        {
            Assert.Equal("AxTableFieldEnum", fields[field].Attribute(Xsi + "type")!.Value);
            Assert.Equal(enumName, fields[field].Element("EnumType")!.Value);
            Assert.Null(fields[field].Element("ExtendedDataType"));
        }
    }

    [Fact]
    public void The_staging_table_carries_the_shape_the_DMF_looks_for()
    {
        var doc = XppScaffolder.EntityStagingTable(
            "ConVehicleEntityStaging", [new EntityFieldSpec("VehicleId", "VehicleId", true)]);
        var root = doc.Root!;

        Assert.Equal("Staging", root.Element("TableGroup")!.Value);
        Assert.Equal("No", root.Element("SaveDataPerCompany")!.Value);
        Assert.Equal("StagingIdx", root.Element("PrimaryIndex")!.Value);
        Assert.Equal("StagingIdx", root.Element("ReplacementKey")!.Value);

        var index = Assert.Single(root.Element("Indexes")!.Elements());
        Assert.Equal("StagingIdx", index.Element("Name")!.Value);
        Assert.Equal("Yes", index.Element("AlternateKey")!.Value);

        // The run comes before the entity's own key: a staging row is unique within a run.
        Assert.Equal(
            new[] { "DefinitionGroup", "ExecutionId", "VehicleId" },
            index.Element("Fields")!.Elements().Select(f => f.Element("DataField")!.Value).ToArray());

        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void The_staging_table_refuses_an_empty_name()
    {
        Assert.Throws<ArgumentException>(() => XppScaffolder.EntityStagingTable("  "));
    }

    [Fact]
    public void The_entity_and_its_staging_table_agree_on_the_name()
    {
        var entity = XppScaffolder.DataEntity(
            "ConVehicleEntity", "FmVehicle",
            fields: [new EntityFieldSpec("VehicleId", "VehicleId", true)],
            keyFields: ["VehicleId"],
            dataManagementEnabled: true);

        var declared = entity.Root!.Element("DataManagementStagingTable")!.Value;
        Assert.Equal("ConVehicleEntityStaging", declared);

        var staging = XppScaffolder.EntityStagingTable(declared);
        Assert.Equal(declared, staging.Root!.Element("Name")!.Value);
    }
}
