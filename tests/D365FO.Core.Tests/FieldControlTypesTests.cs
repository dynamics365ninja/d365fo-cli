using D365FO.Core.FormPatterns;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #164 / R5 — the control type a bound field gets, derived from the field's own type.
/// </summary>
/// <remarks>
/// The mapping is mined from shipped forms (see <see cref="FieldControlTypes"/> for the sample
/// sizes). What is pinned here is the mapping itself and, more importantly, the fallback: a
/// field the caller cannot resolve must still produce a form, because the index is a cache and
/// generation has to work without one.
/// </remarks>
public class FieldControlTypesTests
{
    [Theory]
    [InlineData("AxTableFieldString", "AxFormStringControl", "String")]
    [InlineData("AxTableFieldReal", "AxFormRealControl", "Real")]
    [InlineData("AxTableFieldDate", "AxFormDateControl", "Date")]
    [InlineData("AxTableFieldInt", "AxFormIntegerControl", "Integer")]
    [InlineData("AxTableFieldInt64", "AxFormInt64Control", "Int64")]
    [InlineData("AxTableFieldUtcDateTime", "AxFormDateTimeControl", "DateTime")]
    [InlineData("AxTableFieldTime", "AxFormTimeControl", "Time")]
    [InlineData("AxTableFieldGuid", "AxFormGuidControl", "Guid")]
    public void A_field_gets_the_control_shipped_forms_give_it(string fieldType, string axType, string typeElement)
        => Assert.Equal((axType, typeElement), FieldControlTypes.For(fieldType));

    [Fact]
    public void A_NoYes_enum_is_a_checkbox_and_every_other_enum_is_a_combo_box()
    {
        Assert.Equal(("AxFormCheckBoxControl", "CheckBox"), FieldControlTypes.For("AxTableFieldEnum", "NoYes"));
        Assert.Equal(("AxFormCheckBoxControl", "CheckBox"), FieldControlTypes.For("AxTableFieldEnum", "NoYesId"));
        Assert.Equal(("AxFormComboBoxControl", "ComboBox"), FieldControlTypes.For("AxTableFieldEnum", "SalesStatus"));

        // An enum field whose enum we could not resolve is still not a text box.
        Assert.Equal(("AxFormComboBoxControl", "ComboBox"), FieldControlTypes.For("AxTableFieldEnum"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AxTableFieldSomethingNew")]
    public void An_unresolvable_field_falls_back_to_the_string_control(string? fieldType)
        => Assert.Equal((FieldControlTypes.DefaultControl, "String"), FieldControlTypes.For(fieldType));

    [Theory]
    [InlineData("String", "AxFormStringControl")]
    [InlineData("Integer", "AxFormIntegerControl")]
    [InlineData("Real", "AxFormRealControl")]
    [InlineData("Date", "AxFormDateControl")]
    [InlineData("DateTime", "AxFormDateTimeControl")]
    [InlineData("Int64", "AxFormInt64Control")]
    [InlineData("Guid", "AxFormGuidControl")]
    public void An_EDT_base_type_resolves_the_same_way_a_field_type_does(string baseType, string expected)
        => Assert.Equal(expected, FieldControlTypes.ForEdtBaseType(baseType).AxType);

    // ── the templates actually use it ────────────────────────────────────────

    [Fact]
    public void A_generated_form_types_its_controls_from_the_resolver()
    {
        var xml = XppScaffolder.Form(
            "ConVehicleListPage", "FmVehicle", FormPattern.SimpleList,
            gridFields: ["VehicleId", "PurchaseDate", "IsActive", "Mileage"],
            controlTypeResolver: field => field switch
            {
                "PurchaseDate" => FieldControlTypes.For("AxTableFieldDate"),
                "IsActive" => FieldControlTypes.For("AxTableFieldEnum", "NoYes"),
                "Mileage" => FieldControlTypes.For("AxTableFieldReal"),
                _ => FieldControlTypes.For("AxTableFieldString"),
            });

        Assert.Contains("i:type=\"AxFormDateControl\"", xml);
        Assert.Contains("<Type>Date</Type>", xml);
        Assert.Contains("i:type=\"AxFormCheckBoxControl\"", xml);
        Assert.Contains("<Type>CheckBox</Type>", xml);
        Assert.Contains("i:type=\"AxFormRealControl\"", xml);
        Assert.Contains("i:type=\"AxFormStringControl\"", xml);
    }

    [Fact]
    public void Without_a_resolver_a_generated_form_is_byte_for_byte_what_it_always_was()
    {
        // The resolver is additive: no index, no change. Anything else would make every
        // existing golden move for a feature that could not be applied.
        var before = XppScaffolder.Form(
            "ConVehicleListPage", "FmVehicle", FormPattern.SimpleList,
            gridFields: ["VehicleId", "PurchaseDate"]);

        Assert.Contains("i:type=\"AxFormStringControl\"", before);
        Assert.DoesNotContain("AxFormDateControl", before);
    }

    [Fact]
    public void A_resolver_that_throws_does_not_take_the_form_down_with_it()
    {
        var xml = XppScaffolder.Form(
            "ConVehicleListPage", "FmVehicle", FormPattern.SimpleList,
            gridFields: ["VehicleId"],
            controlTypeResolver: _ => throw new InvalidOperationException("index is gone"));

        Assert.Contains("i:type=\"AxFormStringControl\"", xml);
    }
}
