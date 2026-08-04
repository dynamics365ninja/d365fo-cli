using D365FO.Cli.Commands.Review;

namespace D365FO.Cli.Tests;

/// <summary>
/// Repro for GitHub issue #117: FIELD_WITHOUT_EDT / FIELD_WITHOUT_LABEL must
/// only evaluate real field definitions under AxTable/Fields/AxTableField*,
/// never &lt;FieldGroups&gt; contents, and must treat &lt;EnumType&gt; as an
/// equivalent, label-bearing type declaration alongside &lt;ExtendedDataType&gt;.
/// </summary>
public class ReviewDiffInspectTableXmlTests
{
    // Minimal repro from the issue: 3 well-typed fields (two via ExtendedDataType,
    // one enum field via EnumType) plus 6 field groups (the standard auto-groups
    // and one custom group) referencing those fields by <DataField>.
    private const string MinimalReproXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>MyTable</Name>
          <FieldGroups>
            <AxTableFieldGroup>
              <Name>AutoReport</Name>
              <Fields>
                <AxTableFieldGroupField><DataField>FieldA</DataField></AxTableFieldGroupField>
                <AxTableFieldGroupField><DataField>FieldB</DataField></AxTableFieldGroupField>
                <AxTableFieldGroupField><DataField>FieldC</DataField></AxTableFieldGroupField>
              </Fields>
            </AxTableFieldGroup>
            <AxTableFieldGroup>
              <Name>AutoLookup</Name>
              <Fields>
                <AxTableFieldGroupField><DataField>FieldA</DataField></AxTableFieldGroupField>
                <AxTableFieldGroupField><DataField>FieldB</DataField></AxTableFieldGroupField>
              </Fields>
            </AxTableFieldGroup>
            <AxTableFieldGroup>
              <Name>AutoSummary</Name>
              <Fields>
                <AxTableFieldGroupField><DataField>FieldA</DataField></AxTableFieldGroupField>
              </Fields>
            </AxTableFieldGroup>
            <AxTableFieldGroup>
              <Name>AutoIdentification</Name>
              <AutoPopulate>Yes</AutoPopulate>
              <Fields>
                <AxTableFieldGroupField><DataField>FieldA</DataField></AxTableFieldGroupField>
                <AxTableFieldGroupField><DataField>FieldB</DataField></AxTableFieldGroupField>
              </Fields>
            </AxTableFieldGroup>
            <AxTableFieldGroup>
              <Name>AutoBrowse</Name>
              <Fields />
            </AxTableFieldGroup>
            <AxTableFieldGroup>
              <Name>Overview</Name>
              <Label>@MyModel:MyTableLabel</Label>
              <Fields>
                <AxTableFieldGroupField><DataField>FieldA</DataField></AxTableFieldGroupField>
                <AxTableFieldGroupField><DataField>FieldB</DataField></AxTableFieldGroupField>
                <AxTableFieldGroupField><DataField>FieldC</DataField></AxTableFieldGroupField>
              </Fields>
            </AxTableFieldGroup>
          </FieldGroups>
          <Fields>
            <AxTableField xmlns="" i:type="AxTableFieldString">
              <Name>FieldA</Name>
              <ExtendedDataType>MyStringEdtA</ExtendedDataType>
              <Mandatory>Yes</Mandatory>
            </AxTableField>
            <AxTableField xmlns="" i:type="AxTableFieldEnum">
              <Name>FieldB</Name>
              <EnumType>MyEnumType</EnumType>
            </AxTableField>
            <AxTableField xmlns="" i:type="AxTableFieldString">
              <Name>FieldC</Name>
              <ExtendedDataType>MyStringEdtB</ExtendedDataType>
              <Mandatory>Yes</Mandatory>
            </AxTableField>
          </Fields>
        </AxTable>
        """;

    [Fact]
    public void FieldGroups_and_typed_fields_produce_zero_violations()
    {
        var violations = new List<object>();

        ReviewDiffCommand.InspectTableXml("Metadata/MyModel/AxTable/MyTable.xml", MinimalReproXml, violations);

        Assert.Empty(violations);
    }

    [Fact]
    public void FieldGroup_names_are_not_treated_as_fields()
    {
        var violations = new List<object>();

        ReviewDiffCommand.InspectTableXml("t.xml", MinimalReproXml, violations);

        var fieldNames = violations
            .Select(v => v.GetType().GetProperty("field")!.GetValue(v) as string)
            .ToList();

        Assert.DoesNotContain("AutoReport", fieldNames);
        Assert.DoesNotContain("AutoLookup", fieldNames);
        Assert.DoesNotContain("AutoSummary", fieldNames);
        Assert.DoesNotContain("AutoIdentification", fieldNames);
        Assert.DoesNotContain("AutoBrowse", fieldNames);
        Assert.DoesNotContain("Overview", fieldNames);
        Assert.DoesNotContain("?", fieldNames);
    }

    [Fact]
    public void Field_with_raw_type_and_no_label_still_flagged()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>MyTable</Name>
              <Fields>
                <AxTableField xmlns="" i:type="AxTableFieldString">
                  <Name>RawField</Name>
                </AxTableField>
              </Fields>
            </AxTable>
            """;
        var violations = new List<object>();

        ReviewDiffCommand.InspectTableXml("t.xml", xml, violations);

        var rules = violations.Select(v => v.GetType().GetProperty("rule")!.GetValue(v) as string).ToList();
        Assert.Contains("FIELD_WITHOUT_EDT", rules);
        Assert.Contains("FIELD_WITHOUT_LABEL", rules);
    }
}
