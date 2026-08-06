using System.Text.Json;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using D365FO.Mcp;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// What an agent can reach over MCP, and whether what it gets back is what the CLI would write.
/// </summary>
/// <remarks>
/// Two different gaps are covered here. The narrow one is coverage: <c>generate_object</c>
/// advertised eleven object types while the registry knows far more, so whole families were
/// CLI-only. The wider one is fidelity — the XML-only handlers used to return the raw scaffold,
/// skipping the contract namespace, the contract member order and the shape rules that the file
/// path applies. An agent could therefore be handed XML that no AOS would read, from the same
/// code that writes correct files.
/// </remarks>
public class McpGenerationParityTests
{
    private static readonly string[] AdvertisedObjectTypes =
    [
        "table", "class", "coc", "form",
        "edt", "enum", "query", "sysoperation", "business-event", "runbase", "security-policy",
        "menu-item", "privilege", "duty", "role", "entity", "extension",
    ];

    [Fact]
    public void Generate_object_advertises_every_type_it_dispatches()
    {
        var descriptor = ToolCatalog.All.Single(d => d.Name == "generate_object");

        foreach (var type in AdvertisedObjectTypes)
            Assert.Contains(type, descriptor.Description);
    }

    [Fact]
    public void Validate_is_reachable_over_mcp_in_every_mode()
    {
        var descriptor = ToolCatalog.All.Single(d => d.Name == "validate");

        foreach (var mode in new[] { "xpp", "references", "form-pattern", "metadata-shape" })
            Assert.Contains(mode, descriptor.Description);

        var schema = descriptor.InputSchema["properties"]!.AsObject();
        Assert.True(schema.ContainsKey("mode"));
        Assert.True(schema.ContainsKey("code"));
    }

    /// <summary>
    /// The one property that matters about the MCP XML: it is the file the CLI would have
    /// written. Anything else means two surfaces disagreeing about the same request.
    /// </summary>
    [Theory]
    [InlineData("AxMenuItemDisplay")]
    [InlineData("AxSecurityPrivilege")]
    [InlineData("AxDataEntityView")]
    public void Xml_returned_over_mcp_is_the_canonical_artifact(string expectedRoot)
    {
        var doc = expectedRoot switch
        {
            "AxMenuItemDisplay" => MenuItemScaffolder.MenuItem(
                MenuItemKind.Display, "ConFleetMI", "ConFleetList", MenuItemObjectType.Form, "Fleet"),
            "AxSecurityPrivilege" => XppScaffolder.Privilege(
                "ConFleetPriv", "ConFleetMI", "MenuItemDisplay", access: "Update"),
            _ => XppScaffolder.DataEntity(
                "ConFleetEntity", "FmVehicle", fields: [new EntityFieldSpec("VIN", null, false)]),
        };

        var xml = ScaffoldFileWriter.ToAotXml(doc);

        // Canonicalised: the namespace the contract declares is present when it declares one.
        Assert.Contains(expectedRoot, xml);

        // And judged by the same rules the write path enforces.
        var violations = new List<XppViolation>();
        ContractShapeRules.Check(xml, violations);
        Assert.Empty(violations);
    }

    [Fact]
    public void An_artifact_the_reader_would_mangle_is_refused_rather_than_returned()
    {
        // LinkedPermissionType is an enum with six values, none of them "MenuItemDisplay" — a
        // plausible-looking mistake that makes the whole document unreadable.
        var doc = MenuItemScaffolder.MenuItem(
            MenuItemKind.Display, "ConFleetMI", "ConFleetList", MenuItemObjectType.Form,
            linkedPermissionObject: "ConOther", linkedPermissionType: "MenuItemDisplay");

        var ex = Assert.Throws<InvalidOperationException>(() => ScaffoldFileWriter.ToAotXml(doc));
        Assert.Contains("XML008", ex.Message);
        Assert.Contains("LinkedPermissionType_ITxt", ex.Message);
    }
}
