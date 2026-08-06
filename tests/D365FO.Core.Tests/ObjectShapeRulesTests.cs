using D365FO.Core.Eval;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// XML009–XML013 — the per-family root-shape rules that close audit plan §1.4 / finding G9
/// (issue #163): the offline approximation of what the bridge's <c>Handlers.WriteArtifact</c>
/// rejects, driven by the object-type registry and the contract catalog rather than by an
/// AxTable-shaped hand-written list.
/// </summary>
public class ObjectShapeRulesTests
{
    private static IReadOnlyList<XppViolation> Check(string xml, string? path = null)
    {
        var v = new List<XppViolation>();
        ObjectShapeRules.Check(xml, v, path);
        return v;
    }

    private static string[] Rules(IReadOnlyList<XppViolation> v) => v.Select(x => x.Rule).Distinct().Order().ToArray();

    // ── XML009: the root has to name a real AOT type ─────────────────────────

    [Theory]
    [InlineData("AxTable")]
    [InlineData("AxClass")]
    [InlineData("AxEnum")]
    [InlineData("AxQuerySimple")]
    [InlineData("AxSecurityDuty")]
    [InlineData("AxSecurityRole")]
    [InlineData("AxSecurityPrivilege")]
    [InlineData("AxDataEntityView")]
    [InlineData("AxMap")]
    [InlineData("AxView")]
    public void Real_roots_are_accepted(string root)
    {
        var xsi = " xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"";
        Assert.Empty(Check($"<{root}{xsi}><Name>ConThing</Name></{root}>"));
    }

    [Fact]
    public void An_invented_root_is_XML009()
    {
        var v = Check("<AxTabel><Name>ConThing</Name></AxTabel>");

        Assert.Equal([ObjectShapeRules.RuleUnknownRoot], Rules(v));
        Assert.Contains("AxTable", v[0].Fix);
    }

    [Fact]
    public void A_root_the_registry_marks_as_absent_from_any_shipped_AOT_is_XML009()
    {
        // The G1 failure mode: a name that reads perfectly well and matches no folder on any
        // AOS. A workspace is an AxForm carrying the Workspace pattern — there is no AxWorkspace
        // folder to put one in, so an object written as one is invisible to everything.
        var v = Check("<AxWorkspace><Name>ConVehicleWorkspace</Name></AxWorkspace>");

        Assert.Equal([ObjectShapeRules.RuleUnknownRoot], Rules(v));
        Assert.Contains("matches no folder", v[0].Fix);
    }

    [Fact]
    public void The_root_G1_actually_shipped_is_XML009()
    {
        // AxWorkflowType was generated and read for years and names no MetaModel type at all.
        var v = Check("<AxWorkflowType><Name>ConVehicleReview</Name></AxWorkflowType>");

        Assert.Equal([ObjectShapeRules.RuleUnknownRoot], Rules(v));
        Assert.Contains("No AOT type is named AxWorkflowType", v[0].Fix);
    }

    [Fact]
    public void A_non_Ax_root_is_left_alone()
    {
        // Form-pattern fragments, .rnrproj files, anything the user pipes in — not AOT
        // documents, so the rules have nothing to say about them.
        Assert.Empty(Check("<Project><ItemGroup /></Project>"));
    }

    // ── XML010: abstract roots need a concrete i:type ────────────────────────

    [Fact]
    public void An_abstract_root_without_a_discriminator_is_XML010()
    {
        var v = Check("<AxEdt xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>ConPlate</Name></AxEdt>");

        Assert.Contains(ObjectShapeRules.RuleAbstractRoot, Rules(v));
        Assert.Contains("AxEdtString", v.First(x => x.Rule == ObjectShapeRules.RuleAbstractRoot).Fix);
    }

    [Fact]
    public void An_abstract_root_with_a_concrete_discriminator_is_accepted()
    {
        Assert.Empty(Check(
            "<AxEdt xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:type=\"AxEdtString\">" +
            "<Name>ConPlate</Name></AxEdt>"));
    }

    [Fact]
    public void A_discriminator_naming_no_type_is_XML010()
    {
        var v = Check(
            "<AxEdt xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:type=\"AxEdtText\">" +
            "<Name>ConPlate</Name></AxEdt>");

        Assert.Contains(ObjectShapeRules.RuleAbstractRoot, Rules(v));
        Assert.Contains("No AOT type is named AxEdtText", v.First(x => x.Rule == ObjectShapeRules.RuleAbstractRoot).Fix);
    }

    [Fact]
    public void A_discriminator_from_another_branch_is_XML010()
    {
        // AxTable is a real type, and it is not an EDT — a discriminator the reader cannot use
        // to select anything under AxEdt.
        var v = Check(
            "<AxEdt xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:type=\"AxTable\">" +
            "<Name>ConPlate</Name></AxEdt>");

        Assert.Contains(ObjectShapeRules.RuleAbstractRoot, Rules(v));
        Assert.Contains("does not derive from AxEdt", v.First(x => x.Rule == ObjectShapeRules.RuleAbstractRoot).Fix);
    }

    // ── XML011: the schema-instance declaration ──────────────────────────────

    [Fact]
    public void An_i_type_with_no_namespace_in_scope_is_XML011()
    {
        // Written without xmlns:i the discriminators are unknown attributes and every field
        // reads back as the abstract base (issue #91).
        var v = Check(
            "<AxTable><Name>ConVehicle</Name><Fields>" +
            "<AxTableField xmlns:z=\"http://www.w3.org/2001/XMLSchema-instance\" z:type=\"AxTableFieldString\">" +
            "<Name>Plate</Name></AxTableField></Fields></AxTable>");

        Assert.Contains(ObjectShapeRules.RuleXsiNamespace, Rules(v));
    }

    [Fact]
    public void An_enum_without_the_declaration_is_XML011_even_though_nothing_uses_it()
    {
        // Issue #70: the reader refuses the file outright, with no i:type anywhere in it.
        var v = Check("<AxEnum><Name>ConVehicleStatus</Name></AxEnum>");

        Assert.Equal([ObjectShapeRules.RuleXsiNamespace], Rules(v));
        Assert.Contains("refuses to open", v[0].Fix);
    }

    [Fact]
    public void Any_prefix_bound_to_the_schema_instance_uri_satisfies_XML011()
    {
        Assert.Empty(Check("<AxEnum xmlns:whatever=\"http://www.w3.org/2001/XMLSchema-instance\"><Name>ConVehicleStatus</Name></AxEnum>"));
    }

    // ── XML012: the contract namespace ───────────────────────────────────────

    [Fact]
    public void A_menu_item_outside_its_V1_namespace_is_XML012()
    {
        var v = Check("<AxMenuItemDisplay><Name>ConVehicleMenuItem</Name></AxMenuItemDisplay>");

        Assert.Equal([ObjectShapeRules.RuleContractNamespace], Rules(v));
        Assert.Contains("Microsoft.Dynamics.AX.Metadata.V1", v[0].Fix);
    }

    [Fact]
    public void A_menu_item_in_its_V1_namespace_is_accepted()
    {
        Assert.Empty(Check(
            "<AxMenuItemDisplay xmlns=\"Microsoft.Dynamics.AX.Metadata.V1\"><Name>ConVehicleMenuItem</Name></AxMenuItemDisplay>"));
    }

    [Fact]
    public void A_table_carrying_a_namespace_it_does_not_contract_into_is_XML012()
    {
        var v = Check(
            "<AxTable xmlns=\"Microsoft.Dynamics.AX.Metadata.V2\" xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<Name>ConVehicle</Name></AxTable>");

        Assert.Equal([ObjectShapeRules.RuleContractNamespace], Rules(v));
        Assert.Contains("Drop the xmlns", v[0].Fix);
    }

    // ── XML013: the AOT folder a file sits in ────────────────────────────────

    [Fact]
    public void A_class_document_in_the_AxTable_folder_is_XML013()
    {
        var path = Path.Combine("C:", "Model", "Model", "AxTable", "ConVehicleHelper.xml");
        var v = Check("<AxClass><Name>ConVehicleHelper</Name></AxClass>", path);

        Assert.Equal([ObjectShapeRules.RuleFolderMismatch], Rules(v));
        Assert.Contains("AxClass", v[0].Fix);
    }

    [Fact]
    public void A_document_in_its_own_folder_is_accepted()
    {
        var path = Path.Combine("C:", "Model", "Model", "AxClass", "ConVehicleHelper.xml");
        Assert.Empty(Check("<AxClass><Name>ConVehicleHelper</Name></AxClass>", path));
    }

    [Fact]
    public void A_document_outside_the_AOT_says_nothing_about_its_folder()
    {
        // A scaffold parked at an arbitrary --out path is not in the AOT and is not a defect.
        var path = Path.Combine("C:", "scratch", "ConVehicleHelper.xml");
        Assert.Empty(Check("<AxClass><Name>ConVehicleHelper</Name></AxClass>", path));
    }

    [Fact]
    public void A_document_with_no_path_says_nothing_about_its_folder()
    {
        Assert.Empty(Check("<AxClass><Name>ConVehicleHelper</Name></AxClass>"));
    }

    // ── The CI sweep §1.4 actually asked for ─────────────────────────────────

    [Fact]
    public void Every_reviewed_golden_passes_the_per_family_root_rules()
    {
        var root = EvalPaths.FindRepoRoot();
        Assert.NotNull(root);
        var goldens = Path.Combine(EvalPaths.GoldensDir(root!));
        Assert.True(Directory.Exists(goldens), $"goldens directory not found at {goldens}");

        var offenders = new List<string>();
        var checkedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(goldens, "*.xml", SearchOption.AllDirectories))
        {
            checkedFiles++;
            var violations = new List<XppViolation>();
            ObjectShapeRules.Check(File.ReadAllText(file), violations, file);
            foreach (var v in violations)
                offenders.Add($"{Path.GetRelativePath(goldens, file)}: {v.Rule} {v.Excerpt} — {v.Fix}");
        }

        Assert.True(checkedFiles > 0, "no golden documents were found to check");
        Assert.Empty(offenders);
    }
}
