using System.Xml.Linq;
using D365FO.Core.Eval;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The ten AOT families that had no <c>generate</c> subcommand: each shape is pinned to what
/// the installation ships (census over PackagesLocalDirectory), not to what seemed plausible.
/// </summary>
public class SmallObjectScaffolderTests
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace V2 = "Microsoft.Dynamics.AX.Metadata.V2";

    private static string Written(XDocument doc)
    {
        // The document as the writer finalises it — namespace and member order applied.
        ContractNamespaceApplier.Apply(doc);
        ContractOrderCanonicalizer.Apply(doc);
        return doc.ToString(SaveOptions.DisableFormatting);
    }

    [Fact]
    public void Configuration_key_carries_parent_and_omits_the_enabled_default()
    {
        var doc = ConfigurationScaffolder.ConfigurationKey("ConFmTrial", "@FLM1", parentKey: "Bank");
        var root = doc.Root!;
        Assert.Equal("AxConfigurationKey", root.Name.LocalName);
        Assert.Equal("Bank", root.Element("ParentKey")!.Value);
        Assert.Null(root.Element("EnabledByDefault"));

        var off = ConfigurationScaffolder.ConfigurationKey("ConFmTrial", enabledByDefault: false);
        Assert.Equal("No", off.Root!.Element("EnabledByDefault")!.Value);

        Assert.Throws<ArgumentException>(() => ConfigurationScaffolder.ConfigurationKey("ConFmTrial", parentKey: "confmtrial"));
    }

    [Fact]
    public void Workflow_category_lands_in_the_V2_namespace_with_its_module()
    {
        var doc = ConfigurationScaffolder.WorkflowCategory("ConFmWf", "PurchaseOrder", "@FLM2", "@FLM3");
        var xml = Written(doc);
        Assert.Contains("<AxWorkflowCategory", xml);
        Assert.Contains("xmlns=\"Microsoft.Dynamics.AX.Metadata.V2\"", xml);
        Assert.Contains("<Module>PurchaseOrder</Module>", xml);
        Assert.Throws<ArgumentException>(() => ConfigurationScaffolder.WorkflowCategory("X", ""));
    }

    [Fact]
    public void Resource_points_into_the_models_ResourceContent_folder_and_omits_the_image_default()
    {
        var image = ConfigurationScaffolder.Resource("ConFmLogo", "ConFmLogo.png", "Contoso");
        Assert.Null(image.Root!.Element("TypeOfResource"));
        Assert.Equal("Contoso/AxResource/ResourceContent/Images/ConFmLogo.png", image.Root.Element("RelativeUriInModelStore")!.Value);

        var xmlDoc = ConfigurationScaffolder.Resource("Profile", "Profile.xml", "Contoso", "xmldoc");
        Assert.Equal("XmlDoc", xmlDoc.Root!.Element("TypeOfResource")!.Value);
        Assert.Equal("Contoso/AxResource/ResourceContent/XmlDoc/Profile.xml", xmlDoc.Root.Element("RelativeUriInModelStore")!.Value);

        Assert.Throws<ArgumentException>(() => ConfigurationScaffolder.Resource("X", "a/b.png", "Contoso"));
        Assert.Throws<ArgumentException>(() => ConfigurationScaffolder.Resource("X", "b.png", "Contoso", "Picture"));
    }

    [Fact]
    public void Label_file_manifest_is_named_per_language_and_points_at_its_content()
    {
        var doc = ConfigurationScaffolder.LabelFile("ConFleet", "en-US", "Contoso", "Contoso");
        var root = doc.Root!;
        Assert.Equal("ConFleet_en-US", root.Element("Name")!.Value);
        Assert.Equal("ConFleet.en-US.label.txt", root.Element("LabelContentFileName")!.Value);
        Assert.Equal("ConFleet", root.Element("LabelFileId")!.Value);
        Assert.Equal("en-US", root.Element("Language")!.Value);
        Assert.Equal(@"Contoso\Contoso\AxLabelFile\LabelResources\en-US\ConFleet.en-US.label.txt",
            root.Element("RelativeUriInModelStore")!.Value);
    }

    [Fact]
    public void Form_part_has_exactly_the_three_members_every_shipped_one_carries()
    {
        var doc = NavigationScaffolder.FormPart("ConFmPart", "FMVehicle", "@FLM9");
        Assert.Equal(new[] { "Name", "Caption", "Form" }, doc.Root!.Elements().Select(e => e.Name.LocalName));
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.FormPart("ConFmPart", "FMVehicle", ""));
    }

    [Fact]
    public void Tile_writes_Type_only_when_it_is_not_Standard_and_lands_in_V1()
    {
        var standard = NavigationScaffolder.Tile("ConFmTile", "FMVehicle", label: "@FLM1", size: "wide");
        Assert.Null(standard.Root!.Element("Type"));
        Assert.Equal("Wide", standard.Root.Element("Size")!.Value);
        Assert.Contains("xmlns=\"Microsoft.Dynamics.AX.Metadata.V1\"", Written(standard));

        var count = NavigationScaffolder.Tile("ConFmCount", "FMVehicle", type: "count", query: "FMVehicleQuery");
        Assert.Equal("Count", count.Root!.Element("Type")!.Value);

        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", "FMVehicle", type: "KPI"));
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", "FMVehicle", type: "Chart"));
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", ""));
    }

    /// <summary>
    /// What a tile binds to follows from its type. 762 of the 770 the installation ships carry
    /// <c>MenuItemName</c> and all of them are Standard or Count; the eight that do not are the
    /// KPI and Link tiles, which carry <c>KPI</c> and <c>URL</c> instead
    /// (<c>QMSCAPAAvgDaysToCloseAllCases</c>, <c>CTPTechDocumentation</c>). Requiring a menu
    /// item on every tile made those two shapes unwritable while the command still offered them.
    /// </summary>
    [Fact]
    public void Tile_binds_the_target_its_type_implies_and_not_a_menu_item_for_all_four()
    {
        var kpi = NavigationScaffolder.Tile("ConFmKpi", menuItemName: null, type: "KPI",
            kpi: "QMSCAPAAvgDaysToCloseAllCases", size: "Wide");
        Assert.Null(kpi.Root!.Element("MenuItemName"));
        Assert.Equal("QMSCAPAAvgDaysToCloseAllCases", kpi.Root.Element("KPI")!.Value);

        var link = NavigationScaffolder.Tile("ConFmLink", menuItemName: null, type: "Link",
            url: "https://learn.microsoft.com/dynamics365/", tileDisplay: "TextOnly");
        Assert.Null(link.Root!.Element("MenuItemName"));
        Assert.Equal("https://learn.microsoft.com/dynamics365/", link.Root.Element("URL")!.Value);
        Assert.Equal("Link", link.Root.Element("Type")!.Value);

        // Standard and Count still have to open something, which is what 762 shipped files say.
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", null));
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", null, type: "Count", query: "FMVehicleQuery"));

        // A Link tile with no address, and a URL on a type that has no member to hold it.
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", null, type: "Link"));
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Tile("X", "FMVehicle", url: "https://example.com/"));
    }

    /// <summary>
    /// A cycle among the embedded references resolves — every parent name exists — but the tree
    /// is walked down from the roots, so nothing in the cycle is ever reached. The composite
    /// came out with an empty <c>EmbeddedDataEntities</c> while the caller was told both
    /// entities were bundled.
    /// </summary>
    [Fact]
    public void Composite_entity_reports_embedded_references_whose_parent_chain_loops()
    {
        Assert.Empty(EntityShapeScaffolder.UnrootedEmbedded(
            [("Lines", "Header"), ("Charges", "Lines")]));

        Assert.Equal(["A", "B"], EntityShapeScaffolder.UnrootedEmbedded(
            [("A", "B"), ("B", "A")]));

        Assert.Equal(["Self"], EntityShapeScaffolder.UnrootedEmbedded(
            [("Lines", "Header"), ("Self", "Self")]));
    }

    [Fact]
    public void Menu_nests_entries_under_submenus_in_declaration_order_and_keeps_elements_unnamespaced()
    {
        var doc = NavigationScaffolder.Menu("ConFleet", "@FLM540",
            submenus: [new MenuSubmenuSpec("Vehicles", "@FLM95")],
            entries:
            [
                new MenuEntrySpec("Vehicles", MenuEntryKind.MenuItem, "FMVehicle"),
                new MenuEntrySpec("Setup", MenuEntryKind.MenuItem, "FMSetup", "Action"),
                new MenuEntrySpec("Workspaces", MenuEntryKind.Tile, "FMClerkWorkspace"),
                new MenuEntrySpec(null, MenuEntryKind.MenuReference, "SysMenuHelp"),
            ],
            displayInContentArea: true);

        var xml = Written(doc);
        var root = XDocument.Parse(xml).Root!;
        XNamespace v1 = "Microsoft.Dynamics.AX.Metadata.V1";
        Assert.Equal(v1 + "AxMenu", root.Name);

        // Submenus come first, in the order declared then implied; the root-level reference last.
        var elements = root.Element(v1 + "Elements")!.Elements().ToList();
        Assert.Equal(4, elements.Count);
        Assert.All(elements, e => Assert.Equal("", e.Name.NamespaceName));
        Assert.Equal(new[] { "Vehicles", "Setup", "Workspaces", "SysMenuHelp" }, elements.Select(e => e.Element("Name")!.Value));
        Assert.Equal("AxMenuElementSubMenu", elements[0].Attribute(Xsi + "type")!.Value);
        Assert.Equal("AxMenuElementMenuReference", elements[3].Attribute(Xsi + "type")!.Value);

        var setupItem = elements[1].Element("Elements")!.Elements().Single();
        Assert.Equal("Action", setupItem.Element("MenuItemType")!.Value);
        Assert.Equal("Yes", setupItem.Element("DisplayInContentArea")!.Value);

        var vehicleItem = elements[0].Element("Elements")!.Elements().Single();
        Assert.Null(vehicleItem.Element("MenuItemType")); // Display is the default and is never written
        Assert.Equal("FMClerkWorkspace", elements[2].Element("Elements")!.Elements().Single().Element("Tile")!.Value);
    }

    [Fact]
    public void Menu_refuses_a_name_used_twice_in_one_container()
    {
        Assert.Throws<ArgumentException>(() => NavigationScaffolder.Menu("ConFleet", entries:
        [
            new MenuEntrySpec("Setup", MenuEntryKind.MenuItem, "FMSetup"),
            new MenuEntrySpec("Setup", MenuEntryKind.MenuItem, "FMSetup"),
        ]));
        // The same target under two different submenus is fine — different containers.
        NavigationScaffolder.Menu("ConFleet", entries:
        [
            new MenuEntrySpec("Setup", MenuEntryKind.MenuItem, "FMSetup"),
            new MenuEntrySpec("Admin", MenuEntryKind.MenuItem, "FMSetup"),
        ]);
    }

    [Fact]
    public void Composite_entity_declares_the_attribute_and_nests_embedded_references()
    {
        var doc = EntityShapeScaffolder.CompositeDataEntityView("ConFmComposite",
        [
            new CompositeEntityReferenceSpec("DMFTestHeaderEntity", "DMFTestHeaderEntity", null,
            [
                new CompositeEntityReferenceSpec("DMFTestLineEntity", "DMFTestLineEntity", "DMFTestHeader", []),
            ]),
        ], label: "@FLM1");

        var root = doc.Root!;
        Assert.Contains("[CompositeDataEntityView]", root.Element("SourceCode")!.Element("Declaration")!.Value);
        Assert.DoesNotContain("extends common", root.Element("SourceCode")!.Element("Declaration")!.Value);
        var rootRef = root.Element("RootDataEntities")!.Element("AxDataEntityViewReferenceRoot")!;
        Assert.Null(rootRef.Element("Relation"));
        var embedded = rootRef.Element("EmbeddedDataEntities")!.Element("AxDataEntityViewReferenceEmbedded")!;
        Assert.Equal("DMFTestHeader", embedded.Element("Relation")!.Value);

        Assert.Throws<ArgumentException>(() => EntityShapeScaffolder.CompositeDataEntityView("X", []));
        Assert.Throws<ArgumentException>(() => EntityShapeScaffolder.CompositeDataEntityView("X",
            [new CompositeEntityReferenceSpec("A", "A", null, [new CompositeEntityReferenceSpec("B", "B", null, [])])]));
    }

    [Fact]
    public void Aggregate_entity_writes_mapped_field_members_in_the_V2_namespace()
    {
        var doc = EntityShapeScaffolder.AggregateDataEntity("ConFmRentals", "FMAggregateMeasurements",
        [
            new AggregateEntityFieldSpec("NoRentals", "FMRentalCharges", "BIRCount", Measure: "NoRentals"),
            new AggregateEntityFieldSpec("VehicleColor", "FMRentalCharges", "FMColorName", Dimension: "FMVehicle", Attribute: "VehicleColor"),
        ]);

        var root = doc.Root!;
        Assert.Equal("Yes", root.Element("IsReadOnly")!.Value);
        Assert.Equal("FMAggregateMeasurements", root.Element("AggregateViewDataSource")!.Element("Measurement")!.Value);
        Assert.Equal(5, root.Element("FieldGroups")!.Elements().Count());

        var fields = root.Element("Fields")!.Elements().ToList();
        Assert.All(fields, f => Assert.Equal("d3p1:AxAggregateDataEntityMappedField", f.Attribute(Xsi + "type")!.Value));
        // Name is a base-type member (no namespace); the mapping members are the V2 subtype's.
        Assert.Equal("", fields[0].Element("Name")!.Name.NamespaceName);
        Assert.Equal("NoRentals", fields[0].Element(V2 + "Measure")!.Value);
        Assert.Null(fields[0].Element("Measure"));
        Assert.Equal(new[] { "Name", "Attribute", "Dimension", "ExtendedDataType", "MeasureGroup" },
            fields[1].Elements().Select(e => e.Name.LocalName));

        // The writer's finalisation must not strip the namespaces it did not put there.
        var written = Written(doc);
        Assert.Contains("<d3p1:MeasureGroup>FMRentalCharges</d3p1:MeasureGroup>", written);

        Assert.Throws<ArgumentException>(() => EntityShapeScaffolder.AggregateDataEntity("X", "M",
            [new AggregateEntityFieldSpec("F", "G", "Edt")]));
        Assert.Throws<ArgumentException>(() => EntityShapeScaffolder.AggregateDataEntity("X", "M",
            [new AggregateEntityFieldSpec("F", "G", "Edt", Measure: "m", Dimension: "d", Attribute: "a")]));
    }

    [Fact]
    public void Map_extension_is_the_bare_shell_the_contract_declares()
    {
        var doc = XppScaffolder.Extension("map", "AssetTransMap", "ConFm");
        Assert.Equal("AxMapExtension", doc.Root!.Name.LocalName);
        Assert.Equal("AssetTransMap.ConFm", doc.Root.Element("Name")!.Value);
        Assert.Single(doc.Root.Elements());
    }

    [Fact]
    public void Content_companions_are_the_files_under_subfolders_of_the_companion_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"d365fo-companions-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "_companions", "LabelResources", "en-US"));
            File.WriteAllText(Path.Combine(dir, "ConFleet_en-US.xml"), "<AxLabelFile><Name>ConFleet_en-US</Name></AxLabelFile>");
            File.WriteAllText(Path.Combine(dir, "_companions", "Sibling.xml"), "<AxClass><Name>Sibling</Name></AxClass>");
            File.WriteAllText(Path.Combine(dir, "_companions", "LabelResources", "en-US", "ConFleet.en-US.label.txt"), "Vehicles=Fleet vehicles");

            var content = L3ModelProvisioner.ContentCompanions(dir);
            var only = Assert.Single(content);
            Assert.Equal(Path.Combine("LabelResources", "en-US", "ConFleet.en-US.label.txt"), only.RelativeUnderCompanions);

            // Top-level XML companions stay where they were: the compiler places them by root element.
            Assert.Single(L3ModelProvisioner.Companions(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
