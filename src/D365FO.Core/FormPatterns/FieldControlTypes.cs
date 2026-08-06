namespace D365FO.Core.FormPatterns;

/// <summary>
/// The control type a bound field gets, derived from the field's own concrete
/// <c>AxTableField*</c> type rather than guessed from its EDT's name.
/// </summary>
/// <remarks>
/// <para>
/// Issue #164 / R5, the one item of the predecessor's form-engine material that was a real
/// defect rather than a missing convenience. Every field control the form templates emitted was
/// an <c>AxFormStringControl</c> with <c>&lt;Type&gt;String&lt;/Type&gt;</c>, whatever the field
/// actually was: a quantity, a date, a status enum. The form loads, and then the control renders
/// and validates as text — a `Qty` field that accepts "abc", a date with no picker, an enum with
/// no list.
/// </para>
/// <para>
/// <b>Where the mapping comes from.</b> Mined from 1,200 shipped <c>AxForm</c> files against
/// 1,500 shipped <c>AxTable</c> files on a live installation, joining each bound control to the
/// field it names through its datasource. The result is not a judgement call — every non-enum
/// case is unanimous or near-unanimous in the sample:
/// </para>
/// <list type="table">
/// <item><description><c>AxTableFieldString</c> → <c>AxFormStringControl</c> (518 of 518)</description></item>
/// <item><description><c>AxTableFieldReal</c> → <c>AxFormRealControl</c> (119 of 119)</description></item>
/// <item><description><c>AxTableFieldDate</c> → <c>AxFormDateControl</c> (55 of 55)</description></item>
/// <item><description><c>AxTableFieldInt</c> → <c>AxFormIntegerControl</c> (26 of 27)</description></item>
/// <item><description><c>AxTableFieldUtcDateTime</c> → <c>AxFormDateTimeControl</c> (20 of 20)</description></item>
/// <item><description><c>AxTableFieldTime</c> → <c>AxFormTimeControl</c> (9 of 9)</description></item>
/// <item><description><c>AxTableFieldInt64</c> → <c>AxFormInt64Control</c> (6 of 6)</description></item>
/// <item><description><c>AxTableFieldGuid</c> → <c>AxFormGuidControl</c> (2 of 2)</description></item>
/// </list>
/// <para>
/// The enum case is the only one that splits, and it splits cleanly along a line that is worth
/// knowing: a <c>NoYes</c>-typed field is a <c>CheckBox</c> (111 of 111) and every other enum is
/// a <c>ComboBox</c> (106 of 108, the remaining two being <c>RadioButton</c>). So the enum's
/// identity, not just its kind, decides the control.
/// </para>
/// </remarks>
public static class FieldControlTypes
{
    /// <summary>What the templates emitted for every field before this existed.</summary>
    public const string DefaultControl = "AxFormStringControl";

    private static readonly IReadOnlyDictionary<string, (string AxType, string TypeElement)> ByFieldType =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["AxTableFieldString"] = ("AxFormStringControl", "String"),
            ["AxTableFieldMemo"] = ("AxFormStringControl", "String"),
            ["AxTableFieldReal"] = ("AxFormRealControl", "Real"),
            ["AxTableFieldDate"] = ("AxFormDateControl", "Date"),
            ["AxTableFieldInt"] = ("AxFormIntegerControl", "Integer"),
            ["AxTableFieldInt64"] = ("AxFormInt64Control", "Int64"),
            ["AxTableFieldUtcDateTime"] = ("AxFormDateTimeControl", "DateTime"),
            ["AxTableFieldTime"] = ("AxFormTimeControl", "Time"),
            ["AxTableFieldGuid"] = ("AxFormGuidControl", "Guid"),
            ["AxTableFieldContainer"] = ("AxFormImageControl", "Image"),
        };

    /// <summary>
    /// Enums whose fields are rendered as a checkbox rather than a drop-down.
    /// </summary>
    /// <remarks>
    /// Two-valued yes/no enums only. This is a list rather than "any enum with two values"
    /// because the platform has plenty of two-valued enums that are genuinely drop-downs
    /// (<c>Gender</c>, <c>DebCredProposal</c>) — the checkbox is about the enum <em>meaning</em>
    /// a boolean, which is a naming convention the platform holds to.
    /// </remarks>
    private static readonly IReadOnlySet<string> CheckBoxEnums =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "NoYes", "NoYesId", "NoYesCombo" };

    /// <summary>
    /// The concrete form-control type and its <c>&lt;Type&gt;</c> value for a bound field.
    /// </summary>
    /// <param name="axTableFieldType">
    /// The field's <c>i:type</c> (<c>AxTableFieldString</c>, <c>AxTableFieldEnum</c>, …), or null
    /// when the caller could not resolve it.
    /// </param>
    /// <param name="enumType">
    /// The enum the field is typed as, when <paramref name="axTableFieldType"/> is
    /// <c>AxTableFieldEnum</c>. Decides checkbox versus combo box.
    /// </param>
    /// <returns>
    /// The control to emit. Falls back to a string control when the field type is unknown —
    /// the same thing the templates always did, so an unresolvable field is no worse off than
    /// before, and a resolvable one is now right.
    /// </returns>
    public static (string AxType, string TypeElement) For(string? axTableFieldType, string? enumType = null)
    {
        if (string.IsNullOrWhiteSpace(axTableFieldType)) return (DefaultControl, "String");

        if (string.Equals(axTableFieldType, "AxTableFieldEnum", StringComparison.Ordinal))
        {
            return !string.IsNullOrWhiteSpace(enumType) && CheckBoxEnums.Contains(enumType!)
                ? ("AxFormCheckBoxControl", "CheckBox")
                : ("AxFormComboBoxControl", "ComboBox");
        }

        return ByFieldType.TryGetValue(axTableFieldType!, out var hit) ? hit : (DefaultControl, "String");
    }

    /// <summary>
    /// The same answer from an EDT's primitive base type, for callers that have the index but
    /// not the field element.
    /// </summary>
    /// <remarks>
    /// The index records an EDT's <c>BaseType</c> (String, Integer, Real, Date, …), which is the
    /// same fact the field's <c>i:type</c> encodes — <c>XppScaffolder</c> already uses it to pin
    /// <c>AxTableField{Suffix}</c> when it generates a table. Going through the field type keeps
    /// one mapping rather than two that can disagree.
    /// </remarks>
    public static (string AxType, string TypeElement) ForEdtBaseType(string? edtBaseType, string? enumType = null)
        => For(edtBaseType is null ? null : "AxTableField" + NormalizeBaseType(edtBaseType), enumType);

    private static string NormalizeBaseType(string baseType) => baseType.Trim() switch
    {
        "Integer" => "Int",
        "DateTime" => "UtcDateTime",
        var other => other,
    };
}
