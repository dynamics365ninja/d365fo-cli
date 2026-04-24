using D365FO.Core.Scaffolding;
using System.Xml.Linq;
using Xunit;

namespace D365FO.Cli.Tests;

public class ScaffoldingSmokeTests
{
    [Fact]
    public void DataEntity_contains_table_datasource_and_public_names()
    {
        var doc = XppScaffolder.DataEntity("CustEntity", "CustTable");
        var root = doc.Root!;
        Assert.Equal("AxDataEntityView", root.Name.LocalName);
        Assert.Equal("CustEntity", root.Element("Name")!.Value);
        Assert.Equal("CustEntity", root.Element("PublicEntityName")!.Value);
        Assert.Equal("CustEntitys", root.Element("PublicCollectionName")!.Value);
        var ds = root.Element("DataSources")!.Elements().First();
        Assert.Equal("AxQuerySimpleRootDataSource", ds.Name.LocalName);
        Assert.Equal("CustTable", ds.Element("Table")!.Value);
    }

    [Fact]
    public void Extension_produces_dotted_name_for_target_and_suffix()
    {
        var doc = XppScaffolder.Extension("Table", "CustTable", "Contoso");
        Assert.Equal("AxTableExtension", doc.Root!.Name.LocalName);
        Assert.Equal("CustTable.Contoso", doc.Root.Element("Name")!.Value);
    }

    [Fact]
    public void Extension_rejects_unknown_kind()
    {
        Assert.Throws<ArgumentException>(() => XppScaffolder.Extension("Bogus", "X", "Y"));
    }

    [Fact]
    public void Privilege_round_trips_entry_point_fields()
    {
        var doc = XppScaffolder.Privilege("PurchOrderReadPriv", "PurchTableForm", "MenuItemDisplay",
            entryPointObject: "PurchTable", access: "Read");
        Assert.Equal("PurchOrderReadPriv", doc.Root!.Element("Name")!.Value);
        var ep = doc.Root.Element("EntryPoints")!.Element("AxSecurityEntryPointReference")!;
        Assert.Equal("PurchTableForm", ep.Element("Name")!.Value);
        Assert.Equal("PurchTable", ep.Element("ObjectName")!.Value);
        Assert.Equal("MenuItemDisplay", ep.Element("ObjectType")!.Value);
        Assert.Equal("Read", ep.Element("AccessLevel")!.Value);
    }

    [Fact]
    public void Duty_lists_given_privileges()
    {
        var doc = XppScaffolder.Duty("PurchOrderMaintainDuty", new[] { "PrivA", "PrivB" });
        var refs = doc.Root!.Element("PrivilegeReferences")!.Elements().ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal("PrivA", refs[0].Element("Name")!.Value);
        Assert.Equal("PrivB", refs[1].Element("Name")!.Value);
    }

    [Fact]
    public void EventHandler_emits_expected_attribute_for_form_kind()
    {
        var doc = XppScaffolder.EventHandler("Contoso_CustTable_Handler", "Table", "CustTable", "inserted");
        var decl = doc.Descendants("Declaration").Single().Value;
        Assert.Contains("DataEventHandler", decl, StringComparison.Ordinal);
        Assert.Contains("tableStr(CustTable)", decl, StringComparison.Ordinal);
        Assert.Contains("DataEventType::inserted", decl, StringComparison.Ordinal);
    }

    [Fact]
    public void Role_lists_referenced_duties_and_privileges()
    {
        var doc = XppScaffolder.Role("ContosoOperatorRole",
            duties: new[] { "DutyA", "DutyB" },
            privileges: new[] { "PrivC" },
            label: "Operator", description: "Fleet operator");
        Assert.Equal("AxSecurityRole", doc.Root!.Name.LocalName);
        Assert.Equal("ContosoOperatorRole", doc.Root.Element("Name")!.Value);
        var duties = doc.Root.Element("Duties")!.Elements().ToList();
        Assert.Equal(2, duties.Count);
        Assert.Equal("DutyA", duties[0].Element("Name")!.Value);
        var privs = doc.Root.Element("Privileges")!.Elements().ToList();
        Assert.Single(privs);
    }

    [Fact]
    public void AddToRole_is_idempotent_and_merges_new_refs()
    {
        var doc = XppScaffolder.Role("R", duties: new[] { "D1" });
        var changed1 = XppScaffolder.AddToRole(doc, duties: new[] { "D1" });
        Assert.False(changed1);
        var changed2 = XppScaffolder.AddToRole(doc, duties: new[] { "D2" }, privileges: new[] { "P1" });
        Assert.True(changed2);
        Assert.Equal(2, doc.Root!.Element("Duties")!.Elements().Count());
        Assert.Single(doc.Root.Element("Privileges")!.Elements());
    }
}
