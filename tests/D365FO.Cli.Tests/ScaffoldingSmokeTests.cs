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
        // The entity's data sources belong to its embedded query, not to the entity: a
        // <DataSources> element on AxDataEntityView is not a member and is dropped on read.
        Assert.Null(root.Element("DataSources"));
        var ds = root.Element("ViewMetadata")!.Element("DataSources")!.Elements().First();
        Assert.Equal("AxQuerySimpleRootDataSource", ds.Name.LocalName);
        Assert.Equal("CustTable", ds.Element("Table")!.Value);
    }

    [Fact]
    public void DataEntity_defaults_data_management_off_with_empty_staging_table()
    {
        var doc = XppScaffolder.DataEntity("CustEntity", "CustTable");
        var root = doc.Root!;
        Assert.Equal("No", root.Element("DataManagementEnabled")!.Value);
        Assert.Equal("", root.Element("DataManagementStagingTable")!.Value);
    }

    [Fact]
    public void DataEntity_opt_in_data_management_names_staging_table()
    {
        var doc = XppScaffolder.DataEntity("CustEntity", "CustTable", dataManagementEnabled: true);
        var root = doc.Root!;
        Assert.Equal("Yes", root.Element("DataManagementEnabled")!.Value);
        Assert.Equal("CustEntityStaging", root.Element("DataManagementStagingTable")!.Value);

        var custom = XppScaffolder.DataEntity("CustEntity", "CustTable",
            dataManagementEnabled: true, dataManagementStagingTable: "MyStaging");
        Assert.Equal("MyStaging", custom.Root!.Element("DataManagementStagingTable")!.Value);
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
        // Access is a <Grant> of per-operation permissions; AccessLevel is not a member of
        // AxSecurityEntryPointReference and left the privilege granting nothing at all.
        Assert.Null(ep.Element("AccessLevel"));
        Assert.Equal(new[] { "Read" }, ep.Element("Grant")!.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void Privilege_data_entity_view_emits_canonical_permission_order()
    {
        var doc = XppScaffolder.Privilege("CustEntityViewPriv", null, null,
            dataEntity: "CustCustomerEntity", dataEntityAccess: "view");
        var perm = doc.Root!.Element("DataEntityPermissions")!
            .Element("AxSecurityDataEntityPermission")!;
        // Serializer-canonical child order: Grant FIRST, then Name, Fields, Methods.
        Assert.Equal(new[] { "Grant", "Name", "Fields", "Methods" },
            perm.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Equal("CustCustomerEntity", perm.Element("Name")!.Value);
        var grant = perm.Element("Grant")!.Elements().Select(e => e.Name.LocalName).ToArray();
        Assert.Equal(new[] { "Read" }, grant);
    }

    [Fact]
    public void Privilege_data_entity_maintain_emits_alphabetical_crud_grant()
    {
        var doc = XppScaffolder.Privilege("CustEntityMaintainPriv", null, null,
            dataEntity: "CustCustomerEntity", dataEntityAccess: "maintain");
        var grant = doc.Root!.Element("DataEntityPermissions")!
            .Element("AxSecurityDataEntityPermission")!
            .Element("Grant")!.Elements().Select(e => e.Name.LocalName).ToArray();
        // CRUD elements alphabetical, matching the Microsoft serializer.
        Assert.Equal(new[] { "Correct", "Create", "Delete", "Read", "Update" }, grant);
        Assert.All(doc.Root.Element("DataEntityPermissions")!
            .Element("AxSecurityDataEntityPermission")!
            .Element("Grant")!.Elements(), e => Assert.Equal("Allow", e.Value));
    }

    [Fact]
    public void Privilege_without_data_entity_omits_permissions_block()
    {
        var doc = XppScaffolder.Privilege("PurchOrderReadPriv", "PurchTableForm", "MenuItemDisplay");
        Assert.Null(doc.Root!.Element("DataEntityPermissions"));
    }

    [Theory]
    [InlineData("Table", "AxTableExtension")]
    [InlineData("Form", "AxFormExtension")]
    [InlineData("Enum", "AxEnumExtension")]
    public void Extension_uses_the_dotted_name_convention(string kind, string expectedRoot)
    {
        var doc = XppScaffolder.Extension(kind, "CustTable", "Contoso");
        Assert.Equal(expectedRoot, doc.Root!.Name.LocalName);
        Assert.Equal("CustTable.Contoso", doc.Root.Element("Name")!.Value);
    }

    [Fact]
    public void EdtExtension_carries_no_subtype_discriminator()
    {
        // The metadata assembly declares exactly one, concrete AxEdtExtension and no
        // AxEdt*Extension subtypes. This used to emit i:type="AxEdtStringExtension" — a type
        // that exists in no D365FO build — and the write guard demanded it, so every EDT
        // extension produced was unreadable by the metadata provider.
        var doc = XppScaffolder.Extension("Edt", "CustAccount", "Contoso", _ => "String");
        var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");

        Assert.Equal("AxEdtExtension", doc.Root!.Name.LocalName);
        Assert.Null(doc.Root.Attribute(xsi + "type"));
        // Shipped extensions declare the xsi namespace and express changes as property overrides.
        Assert.Equal(xsi.NamespaceName, (string?)doc.Root.Attribute(XNamespace.Xmlns + "i"));
        Assert.NotNull(doc.Root.Element("ArrayElements"));
        Assert.NotNull(doc.Root.Element("PropertyModifications"));

        var path = Path.Combine(Path.GetTempPath(), $"edtext-{Guid.NewGuid():N}.xml");
        try
        {
            ScaffoldFileWriter.Write(doc, path, overwrite: true);
            var written = File.ReadAllText(path);
            Assert.Contains("<AxEdtExtension", written);
            Assert.DoesNotContain("AxEdtStringExtension", written);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// The base-type resolver still exists for <c>generate table</c>'s field subtypes; an EDT
    /// extension simply has no subtype to resolve, whatever the target's base type is.
    /// </summary>
    [Fact]
    public void EdtExtension_shape_does_not_vary_with_the_targets_base_type()
    {
        var xsi = XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance");
        var stringDoc = XppScaffolder.Extension("Edt", "CustAccount", "Contoso", _ => "String");
        var intDoc = XppScaffolder.Extension("Edt", "CustQty", "Contoso", _ => "Int64");

        Assert.Null(stringDoc.Root!.Attribute(xsi + "type"));
        Assert.Null(intDoc.Root!.Attribute(xsi + "type"));
        Assert.Equal(stringDoc.Root.Elements().Select(e => e.Name.LocalName),
                     intDoc.Root.Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public void SecurityDutyExtension_adds_privileges_to_base_duty()
    {
        var doc = XppScaffolder.SecurityDutyExtension("BatchJobMaintain", "Contoso", new[] { "MyPriv" });
        var root = doc.Root!;
        Assert.Equal("AxSecurityDutyExtension", root.Name.LocalName);
        Assert.Equal("BatchJobMaintain.Contoso", root.Element("Name")!.Value);
        var priv = root.Element("Privileges")!.Elements("AxSecurityPrivilegeReference").Single();
        Assert.Equal("MyPriv", priv.Element("Name")!.Value);
        Assert.NotNull(root.Element("PropertyModifications"));
    }

    [Fact]
    public void SecurityRoleExtension_adds_duties_and_privileges_to_base_role()
    {
        var doc = XppScaffolder.SecurityRoleExtension("SystemUser", "Contoso",
            duties: new[] { "MyDuty" }, privileges: new[] { "MyPriv" });
        var root = doc.Root!;
        Assert.Equal("AxSecurityRoleExtension", root.Name.LocalName);
        Assert.Equal("SystemUser.Contoso", root.Element("Name")!.Value);
        Assert.Equal("MyDuty", root.Element("Duties")!.Elements().Single().Element("Name")!.Value);
        Assert.Equal("MyPriv", root.Element("Privileges")!.Elements().Single().Element("Name")!.Value);
        Assert.NotNull(root.Element("DirectAccessPermissions"));
    }

    [Theory]
    [InlineData("TransDateTime", "AxTableFieldUtcDateTime")]
    [InlineData("EventDateTime", "AxTableFieldUtcDateTime")]
    [InlineData("StartDate", "AxTableFieldDate")]
    [InlineData("SomethingElse", "AxTableFieldString")]
    public void Table_field_discriminator_infers_datetime_edts_without_index(string edt, string expectedType)
    {
        var doc = XppScaffolder.Table("MyTable", null, new[] { new TableFieldSpec("F1", edt, null, false) });
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var field = doc.Root!.Element("Fields")!.Elements("AxTableField").Single();
        Assert.Equal(expectedType, field.Attribute(xsi + "type")!.Value);
    }

    [Fact]
    public void Duty_lists_given_privileges()
    {
        var doc = XppScaffolder.Duty("PurchOrderMaintainDuty", new[] { "PrivA", "PrivB" });
        var refs = doc.Root!.Element("Privileges")!.Elements().ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal("PrivA", refs[0].Element("Name")!.Value);
        Assert.Equal("PrivB", refs[1].Element("Name")!.Value);
    }

    [Fact]
    public void EventHandler_emits_expected_attribute_for_form_kind()
    {
        var doc = XppScaffolder.EventHandler("Contoso_CustTable_Handler", "Table", "CustTable", "inserted");

        // The handler lives in an XML <Method> with its own <Name>, not inline in
        // <Declaration>: the compiler rejects the latter ("The method name in the
        // source code … does not match the name in the XML file, ''"), which is what
        // `eval verify-build` caught on L2-event-handler-basic.
        var method = doc.Descendants("Method").Single();
        Assert.Equal("OnEvent", method.Element("Name")!.Value);
        Assert.DoesNotContain("OnEvent", doc.Descendants("Declaration").Single().Value, StringComparison.Ordinal);

        var source = method.Element("Source")!.Value;
        Assert.Contains("DataEventHandler", source, StringComparison.Ordinal);
        Assert.Contains("tableStr(CustTable)", source, StringComparison.Ordinal);
        Assert.Contains("DataEventType::inserted", source, StringComparison.Ordinal);
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
