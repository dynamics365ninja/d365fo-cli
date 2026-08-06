using System.Xml.Linq;
using D365FO.Core.Metadata;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #162, security half: duty and role depth beyond reference lists, the policy's
/// <c>ConstrainedTables</c> tree, and the property-modification payloads that make a duty/role
/// extension able to change the base object rather than only add to it.
/// </summary>
/// <remarks>
/// Every assertion here is paired with a contract check: the point is not that the scaffolder
/// emits some element, it is that the element is one the AOT type actually declares. An
/// invented member is dropped in silence, so a test that only asserts on our own output would
/// pass just as happily for a file the provider reads as empty.
/// </remarks>
public class SecurityDepthTests
{
    private static XElement Root(XDocument doc) => doc.Root!;

    private static string? Value(XElement root, string name) =>
        root.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static IReadOnlyList<XppViolation> Shape(XDocument doc)
    {
        var v = new List<XppViolation>();
        var xml = doc.ToString(SaveOptions.DisableFormatting);
        ObjectShapeRules.Check(xml, v);
        ContractShapeRules.Check(xml, v);
        return v;
    }

    // ── duty depth ───────────────────────────────────────────────────────────

    [Fact]
    public void A_duty_carries_description_enabled_and_context_string()
    {
        var doc = XppScaffolder.Duty(
            "ConVehicleMaintainDuty", ["ConVehiclePriv"], label: "@Con:Duty",
            description: "Maintain fleet vehicles", enabled: false, contextString: "ConFleet");

        var root = Root(doc);
        Assert.Equal("Maintain fleet vehicles", Value(root, "Description"));
        Assert.Equal("No", Value(root, "Enabled"));
        Assert.Equal("ConFleet", Value(root, "ContextString"));
        Assert.Equal("@Con:Duty", Value(root, "Label"));
        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void Duty_depth_names_only_real_members()
    {
        var contract = MetadataContracts.Find("AxSecurityDuty")!;
        foreach (var member in new[] { "Description", "Enabled", "ContextString", "Label", "Privileges" })
            Assert.True(MetadataContracts.AcceptsMember(contract, member), member);
    }

    [Fact]
    public void An_unasked_for_duty_property_is_left_out_entirely()
    {
        // The AOT default is enabled; writing Enabled=Yes everywhere would make every generated
        // duty differ from the shipped ones for no reason.
        var root = Root(XppScaffolder.Duty("ConVehicleDuty", ["ConVehiclePriv"]));

        Assert.Null(Value(root, "Enabled"));
        Assert.Null(Value(root, "Description"));
        Assert.Null(Value(root, "ContextString"));
    }

    [Fact]
    public void A_privilege_reference_can_be_listed_but_switched_off()
    {
        var doc = XppScaffolder.Duty(
            "ConVehicleDuty", [],
            privilegeRefs: [
                new XppScaffolder.SecurityReferenceSpec("ConVehicleRead"),
                new XppScaffolder.SecurityReferenceSpec("ConVehicleWrite", Enabled: false),
            ]);

        var refs = Root(doc).Element("Privileges")!.Elements().ToArray();
        Assert.Equal(2, refs.Length);
        Assert.Null(refs[0].Element("Enabled"));
        Assert.Equal("No", refs[1].Element("Enabled")!.Value);

        // Enabled precedes Name in AxSecurityPrivilegeReference's contract; the other order
        // loses the flag on read.
        Assert.Equal(["Enabled", "Name"], refs[1].Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Empty(Shape(doc));
    }

    // ── role depth ───────────────────────────────────────────────────────────

    [Fact]
    public void A_role_can_compose_other_roles()
    {
        var doc = XppScaffolder.Role(
            "ConFleetManager", duties: ["ConVehicleMaintainDuty"], subRoles: ["ConFleetViewer"],
            contextString: "ConFleet", enabled: false, canBeDeletedFromUI: false);

        var root = Root(doc);
        var subRoles = root.Element("SubRoles")!.Elements().ToArray();
        Assert.Equal("AxSecurityRoleReference", Assert.Single(subRoles).Name.LocalName);
        Assert.Equal("ConFleetViewer", subRoles[0].Element("Name")!.Value);
        Assert.Equal("ConFleet", Value(root, "ContextString"));
        Assert.Equal("No", Value(root, "Enabled"));
        Assert.Equal("No", Value(root, "CanBeDeletedFromUI"));
        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void Role_depth_names_only_real_members()
    {
        var contract = MetadataContracts.Find("AxSecurityRole")!;
        foreach (var member in new[] { "SubRoles", "ContextString", "Enabled", "CanBeDeletedFromUI", "Description" })
            Assert.True(MetadataContracts.AcceptsMember(contract, member), member);
    }

    // ── extension property modifications ─────────────────────────────────────

    [Fact]
    public void A_duty_extension_can_override_a_property_of_the_base_duty()
    {
        var doc = XppScaffolder.SecurityDutyExtension(
            "MaintainVehicleServiceDuty", "Con", ["ConVehiclePriv"],
            [new XppScaffolder.PropertyModificationSpec("Enabled", "No")]);

        var mods = Root(doc).Element("PropertyModifications")!.Elements().ToArray();
        var mod = Assert.Single(mods);
        Assert.Equal("AxPropertyModification", mod.Name.LocalName);
        Assert.Equal("Enabled", mod.Element("Name")!.Value);
        Assert.Equal("No", mod.Element("Value")!.Value);
        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void A_role_extension_can_override_a_property_of_the_base_role()
    {
        var doc = XppScaffolder.SecurityRoleExtension(
            "FleetManager", "Con", duties: ["ConVehicleDuty"], privileges: null,
            propertyModifications: [
                new XppScaffolder.PropertyModificationSpec("Description", "Contoso fleet manager"),
                new XppScaffolder.PropertyModificationSpec("ContextString", "ConFleet"),
            ]);

        var mods = Root(doc).Element("PropertyModifications")!.Elements().ToArray();
        Assert.Equal(2, mods.Length);
        Assert.Equal(["Description", "ContextString"], mods.Select(m => m.Element("Name")!.Value).ToArray());
        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void An_extension_with_no_overrides_still_emits_the_empty_collection()
    {
        // Shipped extensions carry the element; dropping it would make every generated file
        // differ from the real ones without changing what the provider reads.
        Assert.NotNull(Root(XppScaffolder.SecurityDutyExtension("D", "Con")).Element("PropertyModifications"));
        Assert.NotNull(Root(XppScaffolder.SecurityRoleExtension("R", "Con")).Element("PropertyModifications"));
    }

    // ── policy constrained tables ────────────────────────────────────────────

    [Fact]
    public void A_policy_can_name_the_tables_it_reaches_beyond_the_primary_one()
    {
        var doc = SecurityPolicyScaffolder.Policy(
            "ConFleetPolicy", "FmVehicle", "ConFleetPolicyQuery",
            constrainedTables: [
                new SecurityPolicyScaffolder.ConstrainedEntity("FmVehicleService", Constrained: false,
                    Children: [new SecurityPolicyScaffolder.ConstrainedEntity("FmVehicleServiceLine")]),
            ]);

        var entities = Root(doc).Element("ConstrainedTables")!.Elements().ToArray();
        var header = Assert.Single(entities);
        Assert.Equal("AxSecurityPolicyConstrainedEntity", header.Name.LocalName);
        Assert.Equal("No", header.Element("Constrained")!.Value);
        Assert.Equal("FmVehicleService", header.Element("Name")!.Value);

        var line = Assert.Single(header.Element("ConstrainedTables")!.Elements());
        Assert.Equal("FmVehicleServiceLine", line.Element("Name")!.Value);
        Assert.Equal("Yes", line.Element("Constrained")!.Value);

        // Constrained precedes Name in the contract — the other order loses the flag.
        Assert.Equal("Constrained", header.Elements().First().Name.LocalName);
        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void A_policy_without_constrained_tables_is_unchanged()
    {
        var doc = SecurityPolicyScaffolder.Policy("ConFleetPolicy", "FmVehicle", "ConFleetPolicyQuery");
        Assert.Empty(Root(doc).Element("ConstrainedTables")!.Elements());
        Assert.Empty(Shape(doc));
    }

    [Fact]
    public void AxSecurityPolicy_has_no_PolicyGroup_member()
    {
        // Recorded as a test, not a comment: "PolicyGroup" was on the list of things this
        // scaffolder was missing, and the metadata assembly says the type has no such member.
        // Emitting one would be XML007 — dropped on read, file still looks right.
        var contract = MetadataContracts.Find("AxSecurityPolicy")!;

        Assert.False(MetadataContracts.AcceptsMember(contract, "PolicyGroup"));
        Assert.True(MetadataContracts.AcceptsMember(contract, "ConstrainedTables"));
    }
}
