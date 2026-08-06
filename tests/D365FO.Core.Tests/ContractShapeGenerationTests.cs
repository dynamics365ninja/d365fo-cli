using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Every generator's output judged against the AOT's own contract, offline.
/// </summary>
/// <remarks>
/// These are regression tests for a specific failure mode rather than for specific properties:
/// an element the reader discards costs nothing at generate time and everything afterwards, so
/// each family that shipped one gets a test naming what it was. XML007 and XML008 do the
/// judging, and both are calibrated against shipped Microsoft AOT in
/// <see cref="MetadataContractsAotTests"/> — a rule that flagged correct files would make all
/// of this meaningless.
/// </remarks>
public class ContractShapeGenerationTests
{
    private static List<XppViolation> Check(XDocument doc)
    {
        var violations = new List<XppViolation>();
        ContractShapeRules.Check(doc.ToString(), violations);
        return violations;
    }

    private static void AssertClean(XDocument doc)
    {
        var violations = Check(doc);
        Assert.True(violations.Count == 0,
            "The metadata reader would not load this as written:\n  " +
            string.Join("\n  ", violations.Select(v => $"{v.Rule} {v.Excerpt} — {v.Fix}")));
    }

    [Fact]
    public void Report_has_datasets_a_parameter_group_and_an_auto_design()
    {
        var doc = XppScaffolder.Report(new ReportSpec(
            "ConFleetReport",
            Caption: "Fleet",
            Fields: ["VIN", "Odometer:int"],
            Parameters: [new ReportParameterSpec("FromDate", "DateTime")]));

        AssertClean(doc);

        var root = doc.Root!;
        // DataSets, not Datasets: the lower-case spelling was not a member and took every
        // dataset with it.
        Assert.NotNull(root.Element("DataSets"));
        Assert.Null(root.Element("Datasets"));

        // Parameters belong to the report's default group, not to the report.
        Assert.Null(root.Element("ReportParameters"));
        var parameter = root.Element("DefaultParameterGroup")!
            .Element("ReportParameterBases")!.Elements().Single();
        Assert.Equal("System.DateTime", parameter.Element("DataType")!.Value);

        // The design is a real subtype, not a bare AxReportDesign carrying invented members.
        var design = root.Element("Designs")!.Elements().Single();
        Assert.Equal("AxReportAutoDesign", design.Attribute(XName.Get("type", Xsi))!.Value);
        Assert.NotNull(design.Element("DataRegions"));
        Assert.Null(design.Element("AutoDesignSpecs"));
    }

    [Fact]
    public void Report_dataset_binds_to_its_data_provider_through_the_query()
    {
        var doc = XppScaffolder.Report(new ReportSpec("ConFleetReport", Fields: ["VIN"]));

        var dataSet = doc.Root!.Element("DataSets")!.Elements().Single();
        Assert.Equal("ReportDataProvider", dataSet.Element("DataSourceType")!.Value);
        Assert.Equal("SELECT * FROM ConFleetReportDP.ConFleetReportDPTmp", dataSet.Element("Query")!.Value);
    }

    [Fact]
    public void Report_temp_table_is_the_one_the_provider_selects_from()
    {
        var spec = new ReportSpec("ConFleetReport", Fields: ["VIN", "Odometer:int"]);
        var dataset = spec.EffectiveDatasets.Single();

        var table = XppScaffolder.ReportTmpTable(dataset);
        AssertClean(table);

        // The DP's query names <DpClass>Tmp; generating anything else leaves a model that
        // does not compile.
        Assert.Equal("ConFleetReportDPTmp", table.Root!.Element("Name")!.Value);
        Assert.Equal("TempDB", table.Root.Element("TableType")!.Value);
    }

    [Fact]
    public void Report_controller_runs_the_report_it_was_generated_for()
    {
        var doc = XppScaffolder.ReportController(new ReportSpec("ConFleetReport"));
        AssertClean(doc);

        var source = doc.Root!.Element("SourceCode")!.Element("Methods")!.Element("Method")!
            .Element("Source")!.Value;
        Assert.Contains("extends SrsReportRunController", doc.Root.Element("SourceCode")!.Element("Declaration")!.Value);
        Assert.Contains("ssrsReportStr(ConFleetReport, AutoDesign)", source);
    }

    [Fact]
    public void Privilege_entry_point_grants_permissions_rather_than_an_access_level()
    {
        var doc = XppScaffolder.Privilege("ConFleetPriv", "ConFleetMI", "MenuItemDisplay", access: "Update");
        AssertClean(doc);

        var entryPoint = doc.Root!.Element("EntryPoints")!.Elements().Single();
        Assert.Null(entryPoint.Element("AccessLevel"));
        Assert.Equal(["Read", "Update"], entryPoint.Element("Grant")!.Elements().Select(e => e.Name.LocalName));
    }

    [Theory]
    [InlineData("Read", new[] { "Read" })]
    [InlineData("Update", new[] { "Read", "Update" })]
    [InlineData("Create", new[] { "Create", "Read", "Update" })]
    [InlineData("Delete", new[] { "Correct", "Create", "Delete", "Read", "Update" })]
    public void Access_levels_expand_cumulatively(string access, string[] expected)
    {
        var grant = XppScaffolder.SecurityGrant(access);
        Assert.Equal(expected, grant.Elements().Select(e => e.Name.LocalName));
        Assert.All(grant.Elements(), e => Assert.Equal("Allow", e.Value));
    }

    [Fact]
    public void Data_entity_fields_declare_the_mapped_subtype_that_carries_the_mapping()
    {
        var doc = XppScaffolder.DataEntity(
            "ConFleetEntity", "FmVehicle",
            fields: [new EntityFieldSpec("VIN", null, IsMandatory: true)],
            keyFields: ["VIN"]);

        AssertClean(doc);

        var field = doc.Root!.Element("Fields")!.Elements().Single();
        // Without the discriminator the concrete base is instantiated and both mapping members
        // are dropped, leaving a column bound to nothing.
        Assert.Equal("AxDataEntityViewMappedField", field.Attribute(XName.Get("type", Xsi))!.Value);
        Assert.Equal("VIN", field.Element("DataField")!.Value);
        Assert.Equal("FmVehicle", field.Element("DataSource")!.Value);
        // The member is Mandatory; IsMandatory was silently discarded.
        Assert.Equal("Yes", field.Element("Mandatory")!.Value);

        // Data sources belong to the embedded query, not to the entity.
        Assert.Null(doc.Root.Element("DataSources"));
        Assert.Equal("FmVehicle",
            doc.Root.Element("ViewMetadata")!.Element("DataSources")!.Elements().Single().Element("Table")!.Value);
        Assert.Equal("EntityKey", doc.Root.Element("PrimaryKey")!.Value);
    }

    [Fact]
    public void Menu_item_carries_the_properties_that_target_it_at_something_specific()
    {
        var doc = MenuItemScaffolder.MenuItem(
            MenuItemKind.Display, "ConFleetMI", "ConFleetList", MenuItemObjectType.Form,
            label: "Fleet",
            enumTypeParameter: "NoYes", enumParameter: "Yes",
            parameters: "mode=edit", configurationKey: "FmModule", query: "FmVehicleQuery",
            neededPermission: "Update",
            linkedPermissionObject: "ConFleetListMI", linkedPermissionType: "Form");

        AssertClean(doc);

        var root = doc.Root!;
        Assert.Equal("NoYes", root.Element("EnumTypeParameter")!.Value);
        Assert.Equal("FmVehicleQuery", root.Element("Query")!.Value);
        // NeededPermission is a form-control member, not a menu-item one: an access level here
        // becomes the item's own permission flags.
        Assert.Null(root.Element("NeededPermission"));
        Assert.Equal("Yes", root.Element("ReadPermissions")!.Value);
        Assert.Equal("Yes", root.Element("UpdatePermissions")!.Value);
        Assert.Null(root.Element("DeletePermissions"));
    }

    [Fact]
    public void Number_sequence_edt_does_not_claim_a_module_it_cannot_hold()
    {
        var doc = NumberSequenceScaffolder.Edt("ConDemoNum", "ConDemo");
        AssertClean(doc);

        Assert.Equal("AxEdt", doc.Root!.Name.LocalName);
        Assert.Equal("AxEdtString", doc.Root.Attribute(XName.Get("type", Xsi))!.Value);
        // The module association is made in loadModule(), not on the EDT.
        Assert.Null(doc.Root.Element("NumberSequenceModule"));
        Assert.Equal("Num", doc.Root.Element("Extends")!.Value);
    }

    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";
}
