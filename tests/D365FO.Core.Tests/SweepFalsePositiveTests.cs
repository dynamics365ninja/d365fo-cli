using D365FO.Core.Metadata;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The rules that fired on Microsoft's own shipped code, one test each.
/// </summary>
/// <remarks>
/// <para>
/// The first full sweep of this machine's installation (<c>d365fo oracle sweep</c>, 242 858
/// files) reported 4 674 errors. Every one was the validator being wrong rather than the
/// platform: the bar is zero errors on shipped X++ precisely because a rule that fires on
/// correct code teaches the caller to ignore findings, which costs more than the rule earns.
/// </para>
/// <para>
/// Each test below pins one of those, with the count it accounted for. The positive half — the
/// code the rule exists for — is asserted alongside, so a "fix" that silences the rule
/// altogether fails here rather than passing quietly.
/// </para>
/// </remarks>
public class SweepFalsePositiveTests
{
    private static IReadOnlyList<XppViolation> Xpp(string code) =>
        XppValidator.Validate(code, XppValidator.CodeTypeXpp);

    private static IReadOnlyList<XppViolation> Xml(string xml) =>
        XppValidator.Validate(xml, XppValidator.CodeTypeXmlAny);

    // ── XML007: 4 574 findings ────────────────────────────────────────────

    /// <summary>
    /// A member name that is also a type name is a member.
    /// <c>AxQueryExtensionEmbeddedDataSource</c> declares <c>DataSource</c> as an
    /// <c>AxQuerySimpleEmbeddedDataSource</c>; the catalog also holds an unrelated type called
    /// <c>DataSource</c> with three members, and reading the element as that type reported every
    /// real property under it as unknown.
    /// </summary>
    [Fact]
    public void Member_named_after_another_type_is_read_as_the_member()
    {
        const string xml = """
            <AxDataEntityViewExtension>
              <Name>SomeEntity.Model</Name>
              <DataSources>
                <AxQueryExtensionEmbeddedDataSource>
                  <Parent>InventParameters</Parent>
                  <DataSource>
                    <Name>QMSHcmWorker</Name>
                    <DynamicFields>Yes</DynamicFields>
                    <IsReadOnly>Yes</IsReadOnly>
                    <Table>HcmWorker</Table>
                    <JoinMode>OuterJoin</JoinMode>
                  </DataSource>
                </AxQueryExtensionEmbeddedDataSource>
              </DataSources>
            </AxDataEntityViewExtension>
            """;

        Assert.DoesNotContain(Xml(xml), v => v.Rule == "XML007");
    }

    [Fact]
    public void The_contract_for_a_declared_member_beats_a_type_of_the_same_name()
    {
        var parent = MetadataContracts.Find("AxQueryExtensionEmbeddedDataSource");
        Assert.NotNull(parent);

        var element = System.Xml.Linq.XElement.Parse("<DataSource><Name>x</Name></DataSource>");
        var governing = MetadataContracts.GoverningContract(element, parent);

        Assert.Equal("AxQuerySimpleEmbeddedDataSource", governing?.Name);
    }

    /// <summary>
    /// A member the type really does not declare is still reported — the fix is about which
    /// contract governs the element, not about softening the rule.
    /// </summary>
    [Fact]
    public void An_unknown_member_is_still_reported()
    {
        const string xml = "<AxEdt><Name>Foo</Name><NotAMemberOfEdt>1</NotAMemberOfEdt></AxEdt>";
        Assert.Contains(Xml(xml), v => v.Rule == "XML007" && v.Severity == "error");
    }

    /// <summary>
    /// The BP suppression list every model ships: its entries carry <c>ItemSpecific</c> and
    /// <c>Line</c>, which <c>AxIgnoreDiagnosticItem</c> genuinely does not declare (seven
    /// members, reflected off the metadata assembly on a live installation). The file is written
    /// and read by the best-practice tooling rather than by the metadata serializer, so "dropped
    /// on read" does not follow — 1 463 findings in one model.
    /// </summary>
    [Fact]
    public void A_best_practice_suppression_list_is_not_judged_against_the_metadata_contract()
    {
        const string xml = """
            <IgnoreDiagnostics>
              <Name>Model_BPSuppressions</Name>
              <Items>
                <Diagnostic>
                  <DiagnosticType>BestPractices</DiagnosticType>
                  <Moniker>BPErrorPrivilegeNotCoveredByDuty</Moniker>
                  <Line>12</Line>
                  <ItemSpecific><Fields><ElementName>X</ElementName></Fields></ItemSpecific>
                </Diagnostic>
              </Items>
            </IgnoreDiagnostics>
            """;

        Assert.DoesNotContain(Xml(xml), v => v.Rule == "XML007");
    }

    // ── ATTR001: 72 findings ──────────────────────────────────────────────

    /// <summary>
    /// Masking blanks a comment's content and its closing delimiter but keeps the opening
    /// <c>/*</c>, so a commented attribute argument reached the literal test as "false /*".
    /// </summary>
    [Fact]
    public void A_comment_between_attribute_arguments_is_not_part_of_the_argument()
    {
        const string code = """
            [SysODataAction("DebugModeStartSession", false /* static method */)]
            public static void debugModeStartSession()
            {
            }
            """;

        Assert.DoesNotContain(Xpp(code), v => v.Rule == "ATTR001");
    }

    [Fact]
    public void An_attribute_argument_that_is_a_variable_is_still_reported()
    {
        const string code = """
            [SysObsolete(replacementMessage, false, 31\12\2026)]
            public void old()
            {
            }
            """;

        Assert.Contains(Xpp(code), v => v.Rule == "ATTR001");
    }

    // ── FN001: 7 findings ─────────────────────────────────────────────────

    [Fact]
    public void New_Info_constructs_a_class_rather_than_calling_the_predefined_info()
    {
        const string code = """
            public void run()
            {
                Info infolog = new Info();
            }
            """;

        Assert.DoesNotContain(Xpp(code), v => v.Rule == "FN001");
    }

    [Fact]
    public void The_predefined_function_called_with_no_arguments_is_still_reported()
    {
        const string code = """
            public void run()
            {
                if (info())
                {
                }
            }
            """;

        Assert.Contains(Xpp(code), v => v.Rule == "FN001");
    }

    // ── SEL010: 15 findings ───────────────────────────────────────────────

    [Fact]
    public void ValidTimeState_as_a_method_declaration_is_not_a_select_clause()
    {
        const string code = """
            public SysDaQueryObject validTimeState(SysDaValidTimeState _validTimeState = null)
            {
                return this;
            }
            """;

        Assert.DoesNotContain(Xpp(code), v => v.Rule == "SEL010");
    }

    [Fact]
    public void ValidTimeState_called_on_an_object_is_not_a_select_clause()
    {
        const string code = """
            public void build()
            {
                query.validTimeState(new SysDaValidTimeStateDateRange(fromDate, toDate));
            }
            """;

        Assert.DoesNotContain(Xpp(code), v => v.Rule == "SEL010");
    }

    [Fact]
    public void ValidTimeState_given_an_expression_inside_a_select_is_still_reported()
    {
        const string code = """
            public void read()
            {
                select validTimeState(DateTimeUtil::utcNow()) custTable;
            }
            """;

        Assert.Contains(Xpp(code), v => v.Rule == "SEL010");
    }

    // ── CS001: 3 findings ─────────────────────────────────────────────────

    /// <summary>
    /// The platform's Commerce pricing classes alias CLR types into X++ scope
    /// (<c>using string = System.String;</c>), which makes <c>string</c> a declared name in that
    /// file rather than the C# keyword the rule is about.
    /// </summary>
    [Fact]
    public void An_aliased_clr_type_is_not_a_csharp_ism()
    {
        const string code = """
            using string = System.String;
            using decimal = System.Decimal;

            public class GUPThing
            {
            }

            public string constructGroupKey()
            {
                string groupKey = '';
                decimal amount = 0;
                return groupKey;
            }
            """;

        Assert.DoesNotContain(Xpp(code), v => v.Rule == "CS001");
    }

    [Fact]
    public void The_csharp_string_type_without_an_alias_is_still_reported()
    {
        const string code = """
            public void run()
            {
                string groupKey = '';
            }
            """;

        Assert.Contains(Xpp(code), v => v.Rule == "CS001");
    }

    // ── SEL008: 1 finding ─────────────────────────────────────────────────

    /// <summary>
    /// A select inside a <c>#localmacro</c> has no terminating semicolon, so the statement scan
    /// ran on into the next macro and paired one macro's <c>where</c> with the next macro's
    /// <c>order by</c>.
    /// </summary>
    [Fact]
    public void A_select_does_not_run_past_a_precompiler_directive()
    {
        const string code = """
            public void find()
            {
                #localmacro.queryByType
                    select noFetch docuRef
                        where docuRef.refRecId == common.recId
                #endmacro

                #localmacro.querySortByCreatedDateTime
                    select noFetch docuRef
                        order by CreatedDateTime desc
                        where docuRef.refRecId == common.recId
                #endmacro
            }
            """;

        Assert.DoesNotContain(Xpp(code), v => v.Rule == "SEL008");
    }

    [Fact]
    public void Order_by_after_the_where_of_the_same_select_is_still_reported()
    {
        const string code = """
            public void find()
            {
                select custTable where custTable.AccountNum != '' order by AccountNum;
            }
            """;

        Assert.Contains(Xpp(code), v => v.Rule == "SEL008");
    }

    // ── XML002 / XML003: 229 findings in one model ────────────────────────

    /// <summary>
    /// Both are majority conventions mined from standard models, and the minority is Microsoft's
    /// own. A convention most artefacts follow is not a defect in the ones that do not, so
    /// neither may be an error.
    /// </summary>
    [Fact]
    public void The_table_property_conventions_are_warnings_not_errors()
    {
        const string xml = "<AxTable><Name>FmThing</Name><Fields /></AxTable>";
        var violations = XppValidator.Validate(xml, XppValidator.CodeTypeXmlTable);

        var label = Assert.Single(violations, v => v.Rule == "XML002");
        var group = Assert.Single(violations, v => v.Rule == "XML003");
        Assert.Equal("warning", label.Severity);
        Assert.Equal("warning", group.Severity);
    }
}
