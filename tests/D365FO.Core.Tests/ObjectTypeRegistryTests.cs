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

        // The 16 kinds the bridge shipped with, so a refactor cannot quietly drop one.
        Assert.Equal(
            new[]
            {
                "class", "dataentityview", "dataentityviewextension", "edt", "edtextension",
                "enum", "enumextension", "form", "formextension", "map", "query",
                "queryextension", "table", "tableextension", "view", "viewextension",
            },
            collections.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
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
        Assert.Contains("AxEdtExtension", abstractRoots);
        Assert.Contains("AxQuery", abstractRoots);

        var xsi = ObjectTypeRegistry.XsiRequiredRoots();
        foreach (var root in abstractRoots)
            Assert.Contains(root, xsi);
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
}
