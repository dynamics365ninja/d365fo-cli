using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Shape tests for the AxView / AxMap writers. Expectations are ground-truthed
/// against shipped standard-model files on a real AOS — see the scaffolders' own
/// remarks for the files used.
/// </summary>
public class ViewMapScaffoldingTests
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static ViewFieldSpec Bound(string name, string ds, string? field = null)
        => new(name, DataSource: ds, DataField: field ?? name);

    // ---- View ----

    [Fact]
    public void View_has_the_root_shape_the_metadata_reader_expects()
    {
        var doc = ViewScaffolder.View("FmOpenRentals", "FmOpenRentalsQuery",
            new[] { Bound("VehicleId", "FmVehicle") });
        var root = doc.Root!;

        Assert.Equal("AxView", root.Name.LocalName);
        Assert.Equal(Xsi.NamespaceName, root.GetNamespaceOfPrefix("i")!.NamespaceName);
        Assert.Equal("FmOpenRentals", root.Element("Name")!.Value);
        Assert.Equal("FmOpenRentalsQuery", root.Element("Query")!.Value);

        // Element order follows the shipped views: identity, then source, then
        // scalar properties, then the collections.
        var names = root.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(
            new[] { "Name", "SourceCode", "Query", "FieldGroups", "Fields", "Indexes", "Mappings", "Relations", "StateMachines", "ViewMetadata" },
            names);
    }

    [Fact]
    public void View_bound_field_uses_AxViewFieldBound_not_a_per_primitive_subtype()
    {
        // 21 846 of the bound fields on the reference AOS are AxViewFieldBound and
        // none carry a primitive suffix — unlike AxTableField, a bound view field
        // takes its type from the column it projects.
        var doc = ViewScaffolder.View("V", "Q", new[] { Bound("AccountNum", "CustTable", "AccountNum") });
        var field = doc.Root!.Element("Fields")!.Elements().Single();

        Assert.Equal("AxViewField", field.Name.LocalName);
        Assert.Equal("AxViewFieldBound", field.Attribute(Xsi + "type")!.Value);
        Assert.Equal("AccountNum", field.Element("Name")!.Value);
        Assert.Equal("AccountNum", field.Element("DataField")!.Value);
        Assert.Equal("CustTable", field.Element("DataSource")!.Value);
    }

    [Fact]
    public void View_computed_field_carries_its_type_in_the_discriminator_and_a_ViewMethod()
    {
        var doc = ViewScaffolder.View("V", "Q", new[]
        {
            new ViewFieldSpec("Total", ViewMethod: "getTotalSQL", ComputedType: "Real"),
        });
        var field = doc.Root!.Element("Fields")!.Elements().Single();

        Assert.Equal("AxViewFieldComputedReal", field.Attribute(Xsi + "type")!.Value);
        Assert.Equal("getTotalSQL", field.Element("ViewMethod")!.Value);
        Assert.Null(field.Element("DataSource"));
    }

    [Fact]
    public void View_computed_field_without_a_type_is_rejected()
    {
        // The AOT encodes the type in i:type and cannot infer it from the method,
        // so guessing here would emit metadata that does not build.
        var ex = Assert.Throws<ArgumentException>(() => ViewScaffolder.View("V", "Q", new[]
        {
            new ViewFieldSpec("Total", ViewMethod: "getTotalSQL"),
        }));
        Assert.Contains("explicit type", ex.Message);
    }

    [Fact]
    public void View_bound_field_missing_its_source_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ViewScaffolder.View("V", "Q", new[] { new ViewFieldSpec("AccountNum") }));
        Assert.Contains("data source", ex.Message);
    }

    [Fact]
    public void View_without_a_query_is_rejected()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ViewScaffolder.View("V", "", new[] { Bound("A", "T") }));
        Assert.Contains("AxQuery", ex.Message);
    }

    [Fact]
    public void View_emits_no_Ranges_and_no_invented_Title()
    {
        // Ranges belong on the backing query, and a title the caller never asked for
        // is metadata that cannot be justified.
        var doc = ViewScaffolder.View("V", "Q", new[] { Bound("A", "T") });
        Assert.Null(doc.Root!.Element("Ranges"));
        Assert.Null(doc.Root!.Element("TitleField1"));
        Assert.Null(doc.Root!.Element("TitleField2"));
    }

    [Fact]
    public void View_configuration_key_and_label_are_omitted_when_unset()
    {
        var doc = ViewScaffolder.View("V", "Q", new[] { Bound("A", "T") });
        Assert.Null(doc.Root!.Element("ConfigurationKey"));
        Assert.Null(doc.Root!.Element("Label"));

        var gated = ViewScaffolder.View("V", "Q", new[] { Bound("A", "T") }, label: "@Fleet:V", configurationKey: "FmModule");
        Assert.Equal("FmModule", gated.Root!.Element("ConfigurationKey")!.Value);
        Assert.Equal("@Fleet:V", gated.Root!.Element("Label")!.Value);
    }

    // ---- Map ----

    [Fact]
    public void Map_has_the_root_shape_the_metadata_reader_expects()
    {
        var doc = MapScaffolder.Map("FmAddressMap", new[] { new MapFieldSpec("Street", "Name") });
        var root = doc.Root!;

        Assert.Equal("AxMap", root.Name.LocalName);
        Assert.Equal(Xsi.NamespaceName, root.GetNamespaceOfPrefix("i")!.NamespaceName);
        Assert.Equal(
            new[] { "Name", "SourceCode", "FieldGroups", "Fields", "Mappings" },
            root.Elements().Select(e => e.Name.LocalName).ToArray());
    }

    [Theory]
    [InlineData("String", "AxMapFieldString")]
    [InlineData("Int64", "AxMapFieldInt64")]
    [InlineData("Real", "AxMapFieldReal")]
    [InlineData("Enum", "AxMapFieldEnum")]
    [InlineData("Date", "AxMapFieldDate")]
    [InlineData("UtcDateTime", "AxMapFieldUtcDateTime")]
    public void Map_field_discriminator_follows_the_resolved_EDT_base_type(string baseType, string expected)
    {
        var doc = MapScaffolder.Map("M", new[] { new MapFieldSpec("F", "SomeEdt") },
            edtBaseTypeResolver: _ => baseType);
        var field = doc.Root!.Element("Fields")!.Elements().Single();

        Assert.Equal("AxMapBaseField", field.Name.LocalName);
        Assert.Equal(expected, field.Attribute(Xsi + "type")!.Value);
        Assert.Equal("SomeEdt", field.Element("ExtendedDataType")!.Value);
    }

    [Fact]
    public void Map_field_type_agrees_with_the_table_field_writer_on_the_same_EDT()
    {
        // Both families share XppScaffolder.ConcreteFieldSuffix precisely so a map
        // field and a table field on one EDT can never disagree about its type.
        var map = MapScaffolder.Map("M", new[] { new MapFieldSpec("Amount", "AmountMST") });
        var table = XppScaffolder.Table("T", fields: new[] { new TableFieldSpec("Amount", "AmountMST", null, false) });

        var mapType = map.Root!.Element("Fields")!.Elements().Single().Attribute(Xsi + "type")!.Value;
        var tableType = table.Root!.Element("Fields")!.Elements().First().Attribute(Xsi + "type")!.Value;

        Assert.Equal("AxMapFieldReal", mapType);
        Assert.Equal("AxTableFieldReal", tableType);
    }

    [Fact]
    public void Map_mapping_emits_MappingTable_and_connection_pairs()
    {
        var doc = MapScaffolder.Map("M",
            new[] { new MapFieldSpec("Street", "Name"), new MapFieldSpec("City", "Name") },
            new[]
            {
                new MapTableMappingSpec("CustTable", new[]
                {
                    new MapFieldConnection("Street", "AddressStreet"),
                    new MapFieldConnection("City"), // defaults to the same name
                }),
            });

        var mapping = doc.Root!.Element("Mappings")!.Elements().Single();
        Assert.Equal("AxTableMapping", mapping.Name.LocalName);
        Assert.Equal("CustTable", mapping.Element("MappingTable")!.Value);

        var connections = mapping.Element("Connections")!.Elements().ToList();
        Assert.Equal(2, connections.Count);
        Assert.Equal("Street", connections[0].Element("MapField")!.Value);
        Assert.Equal("AddressStreet", connections[0].Element("MapFieldTo")!.Value);
        Assert.Equal("City", connections[1].Element("MapField")!.Value);
        Assert.Equal("City", connections[1].Element("MapFieldTo")!.Value);
    }

    [Fact]
    public void Map_rejects_a_mapping_that_connects_a_field_the_map_does_not_declare()
    {
        // Otherwise the map compiles to something that silently never matches.
        var ex = Assert.Throws<ArgumentException>(() => MapScaffolder.Map("M",
            new[] { new MapFieldSpec("Street", "Name") },
            new[] { new MapTableMappingSpec("CustTable", new[] { new MapFieldConnection("Nonexistent") }) }));

        Assert.Contains("Nonexistent", ex.Message);
    }

    [Fact]
    public void Map_without_fields_is_rejected()
        => Assert.Throws<ArgumentException>(() => MapScaffolder.Map("M", Array.Empty<MapFieldSpec>()));

    // ---- Writer integration ----

    [Fact]
    public void Both_scaffolds_pass_the_scaffold_writer_guards()
    {
        // AxView and AxMap are in the writer's xmlns:i-required set because their
        // fields are polymorphic; the generators must satisfy their own guard.
        var dir = Path.Combine(Path.GetTempPath(), $"d365fo-viewmap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var viewPath = Path.Combine(dir, "V.xml");
            ScaffoldFileWriter.Write(ViewScaffolder.View("V", "Q", new[] { Bound("A", "T") }), viewPath, overwrite: true);
            Assert.Equal("AxView", XDocument.Load(viewPath).Root!.Name.LocalName);

            var mapPath = Path.Combine(dir, "M.xml");
            ScaffoldFileWriter.Write(MapScaffolder.Map("M", new[] { new MapFieldSpec("F", "Name") }), mapPath, overwrite: true);
            Assert.Equal("AxMap", XDocument.Load(mapPath).Root!.Name.LocalName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
