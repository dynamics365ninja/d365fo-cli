using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// R6 (issue #161) — the request-vs-written diff. The defect class it exists for is an option
/// that is accepted, reported as a success, and simply not in the file.
/// </summary>
public class PropertyHonestyTests
{
    private const string Table =
        "<AxTable><Name>ConVehicle</Name><Label>@Con:VehicleLabel</Label><TableGroup>Main</TableGroup>" +
        "<Fields><AxTableField><Name>Plate</Name><ExtendedDataType>Name</ExtendedDataType>" +
        "<Mandatory>Yes</Mandatory></AxTableField></Fields></AxTable>";

    [Fact]
    public void A_value_that_reached_the_document_is_not_a_gap()
    {
        Assert.Empty(PropertyHonesty.Reconcile([("--label", "@Con:VehicleLabel")], Table));
    }

    [Fact]
    public void A_value_that_reached_nothing_is_reported_with_its_option()
    {
        var gaps = PropertyHonesty.Reconcile([("--configuration-key", "ConFleetKey")], Table);

        var gap = Assert.Single(gaps);
        Assert.Equal("--configuration-key", gap.Option);
        Assert.Equal("ConFleetKey", gap.Missing);
        Assert.Contains("not in the generated object", gap.ToString());
    }

    [Fact]
    public void A_composite_spec_is_judged_piece_by_piece()
    {
        // The field arrived and so did its EDT — but nothing carried the second field.
        var gaps = PropertyHonesty.Reconcile(
            [("--field", "Plate:Name:mandatory"), ("--field", "Colour:ConColour")],
            Table);

        Assert.Equal(
            new[] { "Colour", "ConColour" },
            gaps.Select(g => g.Missing).Order().ToArray());
    }

    [Fact]
    public void Element_names_count_as_much_as_values()
    {
        // "mandatory" arrives as <Mandatory>, whose value is "Yes" — searching values alone
        // would report a gap for a request that was honoured.
        Assert.Empty(PropertyHonesty.Reconcile([("--field", "Plate:Name:mandatory")], Table));
    }

    [Fact]
    public void Matching_ignores_case()
    {
        // --pattern main lands as <TableGroup>Main</TableGroup>.
        Assert.Empty(PropertyHonesty.Reconcile([("--pattern", "main")], Table));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("No")]
    [InlineData("default")]
    [InlineData("x")]
    public void Values_that_select_a_shape_rather_than_supply_one_are_not_traced(string value)
    {
        Assert.Empty(PropertyHonesty.Reconcile([("--flagish", value)], Table));
    }

    [Fact]
    public void An_empty_request_says_nothing()
    {
        Assert.Empty(PropertyHonesty.Reconcile([], Table));
        Assert.Empty(PropertyHonesty.Reconcile([("--label", "  ")], Table));
    }

    [Fact]
    public void An_unparseable_document_falls_back_to_its_raw_text()
    {
        // A caller holding broken XML has a bigger problem than this; it should not also get a
        // wall of gaps for values that are plainly there.
        const string broken = "<AxTable><Name>ConVehicle</Name>";
        Assert.Empty(PropertyHonesty.Reconcile([("<NAME>", "ConVehicle")], broken));
    }
}
