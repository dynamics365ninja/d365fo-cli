using System.Reflection;
using D365FO.Core.ObjectTypes;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Guards the registry that replaced four independently drifting kind tables
/// (audit finding G2). The drift that motivated it shipped as G1: the generator wrote
/// <c>AxWorkflow</c>, the extractor read <c>AxWorkflowType</c>, and the folder a real
/// AOS actually uses is <c>AxWorkflowTemplate</c> — three names, none of them agreeing.
/// </summary>
public class ObjectTypeRegistryTests
{
    [Fact]
    public void Kinds_are_unique_and_already_normalized()
    {
        var kinds = ObjectTypeRegistry.All.Select(t => t.Kind).ToList();

        Assert.Equal(kinds.Count, kinds.Distinct(StringComparer.Ordinal).Count());
        Assert.All(kinds, k => Assert.Equal(k, ObjectTypeRegistry.NormalizeKind(k)));
    }

    [Fact]
    public void Root_elements_are_unique()
    {
        var roots = ObjectTypeRegistry.All.Select(t => t.RootElement).ToList();

        Assert.Equal(roots.Count, roots.Distinct(StringComparer.Ordinal).Count());
        Assert.All(roots, r => Assert.StartsWith("Ax", r, StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_accepts_kind_root_element_and_folder_spellings()
    {
        Assert.Equal("table", ObjectTypeRegistry.Find("table")!.Kind);
        Assert.Equal("table", ObjectTypeRegistry.Find("AxTable")!.Kind);
        Assert.Equal("menuitemdisplay", ObjectTypeRegistry.Find("menu-item-display")!.Kind);
        Assert.Equal("queryextension", ObjectTypeRegistry.Find("AxQuerySimpleExtension")!.Kind);
        Assert.Null(ObjectTypeRegistry.Find("AxNonsense"));
    }

    /// <summary>The name at the heart of G1 must resolve to the folder that exists, not the two that never did.</summary>
    [Fact]
    public void Workflow_type_resolves_to_AxWorkflowTemplate()
    {
        var t = ObjectTypeRegistry.Find("workflowtemplate")!;

        Assert.Equal("AxWorkflowTemplate", t.RootElement);
        Assert.Equal("AxWorkflowTemplate", t.AotSubfolder);
        Assert.Equal("workflow", t.GenerateCommand);
        Assert.Null(ObjectTypeRegistry.Find("AxWorkflow"));
    }

    [Fact]
    public void Folder_constants_all_name_a_registered_subfolder()
    {
        var folders = ObjectTypeRegistry.All.Select(t => t.AotSubfolder).ToHashSet(StringComparer.Ordinal);

        var consts = typeof(ObjectTypeRegistry.Folders)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .ToList();

        Assert.NotEmpty(consts);
        foreach (var c in consts)
        {
            var value = (string)c.GetRawConstantValue()!;
            Assert.True(folders.Contains(value),
                $"ObjectTypeRegistry.Folders.{c.Name} = \"{value}\" is not a registered AOT subfolder.");
        }
    }

    [Fact]
    public void Folders_that_exist_on_no_AOS_are_marked_and_never_used_as_model_markers()
    {
        var phantom = ObjectTypeRegistry.All.Where(t => !t.ExistsInStandardAot).ToList();

        // The census these are drawn from found no such folder in any package.
        Assert.Contains(phantom, t => t.RootElement == "AxWorkspace");
        Assert.Contains(phantom, t => t.RootElement == "AxReportSsrs");

        var markers = ObjectTypeRegistry.ModelMarkerFolders();
        foreach (var t in phantom)
            Assert.DoesNotContain(t.AotSubfolder, markers);
    }

    [Fact]
    public void Model_marker_folders_are_real_indexed_folders()
    {
        var markers = ObjectTypeRegistry.ModelMarkerFolders();

        Assert.NotEmpty(markers);
        Assert.Equal(markers.Length, markers.Distinct(StringComparer.Ordinal).Count());
        foreach (var m in markers)
        {
            var t = ObjectTypeRegistry.Find(m)!;
            Assert.True(t.ExistsInStandardAot);
            Assert.True(t.Indexed);
        }
    }

    [Fact]
    public void Bridge_tables_cover_exactly_the_kinds_with_a_provider_collection()
    {
        var expected = ObjectTypeRegistry.All.Where(t => t.ProviderCollection is not null).ToList();

        var collections = ObjectTypeRegistry.BridgeCollections();
        var types = ObjectTypeRegistry.BridgeMetaModelTypes("Microsoft.Dynamics.AX.Metadata.MetaModel.");

        Assert.Equal(expected.Count, collections.Count);
        Assert.Equal(expected.Count, types.Count);
        foreach (var t in expected)
        {
            Assert.Equal(t.ProviderCollection, collections[t.Kind]);
            Assert.Equal("Microsoft.Dynamics.AX.Metadata.MetaModel." + t.MetaModelType, types[t.Kind]);
        }

        // The 16 kinds the bridge shipped with must all survive; the rest were added once
        // IMetadataProvider's own property list was read off the assembly.
        foreach (var original in new[]
                 {
                     "class", "dataentityview", "dataentityviewextension", "edt", "edtextension",
                     "enum", "enumextension", "form", "formextension", "map", "query",
                     "table", "tableextension", "view", "viewextension",
                 })
            Assert.True(collections.ContainsKey(original), $"bridge lost kind '{original}'");

        // Every collection name must be one IMetadataProvider actually exposes. The bridge
        // used to map query extensions to "QueryExtensions" and the type AxQueryExtension —
        // neither exists; the provider property is QuerySimpleExtensions.
        Assert.Equal("QuerySimpleExtensions", collections["queryextension"]);
        Assert.Equal("AxQuerySimpleExtension", ObjectTypeRegistry.Find("queryextension")!.MetaModelType);

        // Families the audit flagged as never reaching the provider (G3) now do.
        foreach (var (kind, collection) in new[]
                 {
                     ("report", "Reports"),
                     ("workflowtemplate", "WorkflowTemplates"),
                     ("menuitemdisplay", "MenuItemDisplays"),
                     ("securityrole", "SecurityRoles"),
                     ("securityprivilege", "SecurityPrivileges"),
                     ("service", "Services"),
                 })
            Assert.Equal(collection, collections[kind]);
    }

    /// <summary>
    /// Every shipped query file is <c>&lt;AxQuery i:type="AxQuerySimple"&gt;</c>; the
    /// generator used to emit a bare abstract root, which the metadata reader rejects.
    /// </summary>
    [Fact]
    public void Abstract_roots_also_require_the_xsi_declaration()
    {
        var abstractRoots = ObjectTypeRegistry.AbstractRoots();

        Assert.Contains("AxEdt", abstractRoots);
        Assert.Contains("AxQuery", abstractRoots);
        // AxEdtExtension looks like AxEdt's twin but is concrete, and the assembly declares
        // no AxEdt*Extension subtypes — pinning one names a type that does not exist.
        Assert.DoesNotContain("AxEdtExtension", abstractRoots);

        var xsi = ObjectTypeRegistry.XsiRequiredRoots();
        foreach (var root in abstractRoots)
            Assert.Contains(root, xsi);
    }

    [Fact]
    public void Contract_namespaces_match_what_the_MetaModel_types_declare()
    {
        // Ground-truthed against Microsoft.Dynamics.AX.Metadata.dll: a file written in the
        // wrong namespace is rejected before a single property is read.
        Assert.Equal(ObjectTypeRegistry.NsV6, ObjectTypeRegistry.Find("form")!.ContractNamespace);
        Assert.Equal(ObjectTypeRegistry.NsV6, ObjectTypeRegistry.Find("formextension")!.ContractNamespace);
        Assert.Equal(ObjectTypeRegistry.NsV2, ObjectTypeRegistry.Find("report")!.ContractNamespace);
        Assert.Equal(ObjectTypeRegistry.NsV2, ObjectTypeRegistry.Find("workflowtemplate")!.ContractNamespace);
        Assert.Equal(ObjectTypeRegistry.NsV1, ObjectTypeRegistry.Find("menuitemdisplay")!.ContractNamespace);
        Assert.Equal(ObjectTypeRegistry.NsV1, ObjectTypeRegistry.Find("tile")!.ContractNamespace);

        // Everything else contracts into the empty namespace.
        Assert.Equal(string.Empty, ObjectTypeRegistry.Find("table")!.ContractNamespace);
        Assert.Equal(string.Empty, ObjectTypeRegistry.Find("class")!.ContractNamespace);
        Assert.Equal(string.Empty, ObjectTypeRegistry.Find("securityrole")!.ContractNamespace);
    }

    [Fact]
    public void Xsi_required_roots_keep_the_ground_truthed_set()
    {
        var xsi = ObjectTypeRegistry.XsiRequiredRoots();

        // Polymorphic children (issue #91) or a reader that rejects the file outright (#70).
        Assert.Contains("AxTable", xsi);
        Assert.Contains("AxView", xsi);
        Assert.Contains("AxMap", xsi);
        Assert.Contains("AxEnum", xsi);
        // Written without it today and read back fine — see ScaffoldFileWriter.
        Assert.DoesNotContain("AxClass", xsi);
        Assert.DoesNotContain("AxForm", xsi);
    }

    [Fact]
    public void Every_generate_subcommand_maps_to_at_least_one_registered_type()
    {
        // Subcommands that compose several object types are represented by the type they
        // create; the ones missing here emit only AxClass artifacts (covered by "class").
        var commands = ObjectTypeRegistry.All
            .Where(t => t.GenerateCommand is not null)
            .Select(t => t.GenerateCommand!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var expected in new[]
                 {
                     "table", "class", "form", "edt", "enum", "query", "view", "map", "entity",
                     "extension", "menu-item", "report", "role", "duty", "privilege",
                     "security-policy", "workflow", "custom-service",
                 })
            Assert.Contains(expected, commands);
    }

    // ---- extension relation (#171) -------------------------------------------

    [Theory]
    [InlineData("table", "AxTableExtension", "TableExtensions")]
    [InlineData("form", "AxFormExtension", "FormExtensions")]
    [InlineData("enum", "AxEnumExtension", "EnumExtensions")]
    [InlineData("edt", "AxEdtExtension", "EdtExtensions")]
    [InlineData("view", "AxViewExtension", "ViewExtensions")]
    [InlineData("dataentityview", "AxDataEntityViewExtension", "DataEntityViewExtensions")]
    public void ExtensionOf_resolves_the_root_and_collection(string baseKind, string root, string collection)
    {
        var extension = ObjectTypeRegistry.ExtensionOf(baseKind);

        Assert.NotNull(extension);
        Assert.Equal(root, extension!.RootElement);
        Assert.Equal(collection, extension.ProviderCollection);
    }

    [Fact]
    public void ExtensionOf_query_names_the_type_that_actually_exists()
    {
        // The hand-maintained table this replaced said "AxQueryExtension". No MetaModel
        // assembly declares one — the real type is AxQuerySimpleExtension.
        Assert.Equal("AxQuerySimpleExtension", ObjectTypeRegistry.ExtensionOf("query")!.RootElement);
    }

    [Fact]
    public void ExtensionOf_returns_null_for_types_with_no_extension_form_and_does_not_nest()
    {
        Assert.Null(ObjectTypeRegistry.ExtensionOf("class"));
        Assert.Null(ObjectTypeRegistry.ExtensionOf("tableextension"));
        Assert.Null(ObjectTypeRegistry.ExtensionOf("nosuchkind"));
        Assert.Null(ObjectTypeRegistry.ExtensionOf(""));
    }

    /// <summary>
    /// The failure in #171 was a redirect the CLI planned to a kind the bridge would not
    /// accept. Both halves read this registry, so an extension the CLI can target must
    /// carry the provider collection the bridge resolves it through.
    /// </summary>
    [Fact]
    public void Every_extension_the_modify_path_can_target_is_bridge_writable()
    {
        var bridgeKinds = ObjectTypeRegistry.BridgeCollections();

        foreach (var baseKind in new[] { "table", "form", "edt", "enum" })
        {
            var extension = ObjectTypeRegistry.ExtensionOf(baseKind);
            Assert.NotNull(extension);
            Assert.True(
                bridgeKinds.ContainsKey(extension!.Kind),
                $"'{extension.Kind}' is reachable by redirecting a {baseKind} write, " +
                "but the bridge would answer INVALID_KIND for it.");
        }
    }
}
