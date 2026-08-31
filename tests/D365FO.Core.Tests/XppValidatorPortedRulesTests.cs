using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Tests for the rule wave ported from the upstream MCP server's
/// <c>validate_code(mode="syntax")</c> (upstream 1.15.0, 40 static rules): masking, the
/// compiler-facts-driven rules (FN/KW/ATTR), the CoC/TTS/report families and the
/// C#-ism/removed-statement checks.
/// </summary>
public class XppValidatorPortedRulesTests
{
    private static IReadOnlyList<XppViolation> Run(string code, string codeType = "xpp", IPropertyStatsProvider? stats = null)
        => XppValidator.Validate(code, codeType, stats);

    // ── Masking regressions (the upstream false-positive set) ────────────────

    [Fact]
    public void Single_quoted_comma_does_not_break_arity_counting()
    {
        // strFind takes 4 args; the ',' literal must count as ONE argument.
        var v = Run("int i = strFind(text, ',', 1, strLen(text));");
        Assert.DoesNotContain(v, x => x.Rule == "FN001");
    }

    [Fact]
    public void Guid_mask_in_single_quotes_is_not_a_null_coalescing_operator()
    {
        var v = Run("str mask = '????????-????-????';");
        Assert.DoesNotContain(v, x => x.Rule == "CS001");
    }

    [Fact]
    public void Sql_join_inside_string_literal_is_not_flagged()
    {
        var v = Run("str sql = ' LEFT JOIN %2 T2 ON T2.A = T1.A ';");
        Assert.DoesNotContain(v, x => x.Rule == "SEL007");
    }

    [Fact]
    public void Keyword_in_block_comment_is_not_flagged()
    {
        var v = Run("/* select forceLiterals t; left join */ int x = 1;");
        Assert.DoesNotContain(v, x => x.Rule is "SEL002" or "SEL007");
    }

    // ── SEL006–SEL010 ────────────────────────────────────────────────────────

    [Fact]
    public void Sel006_flags_index_hint_without_allowIndexHint()
    {
        var v = Run("select custTable index hint AccountIdx where custTable.AccountNum == acc;");
        Assert.Contains(v, x => x.Rule == "SEL006" && x.Severity == "warning");
    }

    [Fact]
    public void Sel006_quiet_when_allowIndexHint_present()
    {
        var v = Run("custTable.allowIndexHint(true);\nselect custTable index hint AccountIdx;");
        Assert.DoesNotContain(v, x => x.Rule == "SEL006");
    }

    [Fact]
    public void Sel007_flags_left_join_and_join_on()
    {
        var v = Run("select custTable left join salesTable;");
        Assert.Contains(v, x => x.Rule == "SEL007" && x.Severity == "error");
        var v2 = Run("select custTable join salesTable on salesTable.CustAccount == custTable.AccountNum;");
        Assert.Contains(v2, x => x.Rule == "SEL007");
    }

    [Fact]
    public void Sel007_allows_outer_join()
    {
        var v = Run("select custTable outer join salesTable where salesTable.CustAccount == custTable.AccountNum;");
        Assert.DoesNotContain(v, x => x.Rule == "SEL007");
    }

    [Fact]
    public void Sel008_flags_order_by_after_where_in_same_segment()
    {
        var v = Run("select custTable where custTable.Blocked == b order by custTable.AccountNum;");
        Assert.Contains(v, x => x.Rule == "SEL008" && x.Severity == "error");
    }

    [Fact]
    public void Sel008_allows_order_by_before_where_and_new_segment_after_join()
    {
        var v = Run("select custTable order by AccountNum where custTable.Blocked == b join salesTable order by SalesId where salesTable.CustAccount == custTable.AccountNum;");
        Assert.DoesNotContain(v, x => x.Rule == "SEL008");
    }

    [Fact]
    public void Sel009_flags_inline_container_literal_in_in_operator()
    {
        var v = Run("select t where t.Status in [MyStatus::A, MyStatus::B];");
        Assert.Contains(v, x => x.Rule == "SEL009" && x.Severity == "error");
    }

    [Fact]
    public void Sel009_allows_container_variable()
    {
        var v = Run("select t where t.Status in statuses;");
        Assert.DoesNotContain(v, x => x.Rule == "SEL009");
    }

    [Fact]
    public void Sel010_flags_select_expression_on_aliased_buffer()
    {
        var code = """
            void run()
            {
                CustGroup cg;
                str name = (select firstOnly cg).Name;
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "SEL010" && x.Severity == "error");
    }

    [Fact]
    public void Sel010_allows_expression_naming_the_table()
    {
        var code = """
            void run()
            {
                str name = (select firstOnly CustGroup).Name;
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "SEL010");
    }

    [Fact]
    public void Sel010_flags_validTimeState_with_call_argument()
    {
        var v = Run("select validTimeState(DateTimeUtil::utcNow()) hcmPosition;");
        Assert.Contains(v, x => x.Rule == "SEL010");
    }

    [Fact]
    public void Sel010_allows_validTimeState_with_variables()
    {
        var v = Run("select validTimeState(asOf) hcmPosition;");
        Assert.DoesNotContain(v, x => x.Rule == "SEL010");
    }

    // ── COC004–COC006 ────────────────────────────────────────────────────────

    [Fact]
    public void Coc004_flags_next_inside_conditional()
    {
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public boolean validate()
                {
                    boolean ret = true;
                    if (ret)
                    {
                        ret = next validate();
                    }
                    return ret;
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC004" && x.Severity == "error");
    }

    [Fact]
    public void Coc004_flags_duplicate_next()
    {
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public void run()
                {
                    next run();
                    next run();
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC004" && x.Fix.Contains("exactly one"));
    }

    [Fact]
    public void Coc004_quiet_on_unconditional_single_next()
    {
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public boolean validate()
                {
                    boolean ret = next validate();
                    if (!ret)
                    {
                        ret = checkFailed("@M:Label");
                    }
                    return ret;
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "COC004");
    }

    [Fact]
    public void Coc005_flags_this_checkFailed_on_table_buffer()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            final class CustTable_Extension
            {
                public boolean validateWrite()
                {
                    boolean ret = next validateWrite();
                    ret = this.checkFailed("@M:Label");
                    return ret;
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC005" && x.Severity == "error");
    }

    [Fact]
    public void Coc005_quiet_on_class_extension()
    {
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public void run()
                {
                    next run();
                    this.error("legal on a RunBase descendant");
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "COC005");
    }

    [Fact]
    public void Coc006_flags_select_of_own_record_by_recid()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            final class CustTable_Extension
            {
                public void update()
                {
                    CustTable original;
                    select original where original.RecId == this.RecId;
                    next update();
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC006" && x.Severity == "warning");
    }

    [Fact]
    public void Coc006_flags_static_find_on_own_recid()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            final class CustTable_Extension
            {
                public void update()
                {
                    CustTable original = CustTable::findRecId(this.RecId);
                    next update();
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC006");
    }

    [Fact]
    public void Coc001_flags_default_param_on_modifierless_wrapper()
    {
        // A CoC template that strips access modifiers is the most likely source of
        // this defect — the bare-declaration form must be caught too.
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                void salute(str message = "Hi")
                {
                    next salute(message);
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC001");
    }

    [Fact]
    public void Coc001_allows_default_param_on_added_method_without_next()
    {
        // A brand-new method an extension class merely adds may carry defaults —
        // the platform ships 20 such classes. Only a wrapper (calls next) may not.
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public CustAccount findByJobId(str _jobId = "")
                {
                    return _jobId;
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "COC001");
    }

    [Fact]
    public void Coc002_accepts_static_extension_method_class()
    {
        // static = an extension-method class; the platform ships them with [ExtensionOf].
        var code = """
            [ExtensionOf(classStr(TaxCalculationAdjustment))]
            public static class TaxCalculationAdjustment_Extension
            {
                public static void helper(TaxCalculationAdjustment _target)
                {
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "COC002");
    }

    // ── BP004–BP006 ──────────────────────────────────────────────────────────

    [Fact]
    public void Bp004_flags_print_and_breakpoint()
    {
        var v = Run("print custTable.AccountNum;\nbreakpoint;");
        Assert.Equal(2, v.Count(x => x.Rule == "BP004"));
    }

    [Fact]
    public void Bp005_flags_enum2Symbol_in_message_builder()
    {
        var v = Run("error(strFmt(\"@M:Label\", enum2Symbol(enumNum(MyStatus), status)));");
        Assert.Contains(v, x => x.Rule == "BP005" && x.Severity == "warning");
    }

    [Fact]
    public void Bp005_allows_enum2str_in_message()
    {
        var v = Run("error(strFmt(\"@M:Label\", enum2Str(status)));");
        Assert.DoesNotContain(v, x => x.Rule == "BP005");
    }

    [Fact]
    public void Bp006_flags_removed_statements()
    {
        var v = Run("pause;\nwindow 10, 10;\ntableLock CustTable;\nchangeSite(1);");
        Assert.True(v.Count(x => x.Rule == "BP006") >= 4);
        Assert.All(v.Where(x => x.Rule == "BP006"), x => Assert.Equal("error", x.Severity));
    }

    [Fact]
    public void Bp001_flags_single_quoted_hardcoded_string()
    {
        var v = Run("error('Hardcoded message');");
        Assert.Contains(v, x => x.Rule == "BP001");
    }

    [Fact]
    public void Bp001_skips_member_calls()
    {
        // Only the Global functions carry the label obligation.
        var v = Run("logger.error(\"diagnostic text\");");
        Assert.DoesNotContain(v, x => x.Rule == "BP001");
    }

    // ── FN001 / FN002 ────────────────────────────────────────────────────────

    [Fact]
    public void Fn001_flags_wrong_arity_on_runtime_function()
    {
        // enum2Str takes the value alone — the 2-argument shape is the documented confusion.
        var v = Run("str s = enum2Str(enumNum(MyStatus), status);");
        Assert.Contains(v, x => x.Rule == "FN001" && x.Severity == "error");
    }

    [Fact]
    public void Fn001_accepts_optional_trailing_argument_range()
    {
        // date2Str: the compiler accepts 7 or 8 arguments.
        var v7 = Run("str s = date2Str(d, 123, 2, 1, 2, 1, 4);");
        var v8 = Run("str s = date2Str(d, 123, 2, 1, 2, 1, 4, DateFlags::FormatAll);");
        Assert.DoesNotContain(v7, x => x.Rule == "FN001");
        Assert.DoesNotContain(v8, x => x.Rule == "FN001");
    }

    [Fact]
    public void Fn001_skips_variadic_functions()
    {
        var v = Run("str s = strFmt(\"%1 %2 %3\", a, b, c);");
        Assert.DoesNotContain(v, x => x.Rule == "FN001");
    }

    [Fact]
    public void Fn001_flags_wrong_intrinsic_arity()
    {
        var v = Run("str s = classStr(MyClass, extra);");
        Assert.Contains(v, x => x.Rule == "FN001");
    }

    [Fact]
    public void Fn001_skips_method_calls_and_own_statics()
    {
        var v = Run("x.year(1, 2, 3);\nMyDateHelper::year(1, 2, 3);");
        Assert.DoesNotContain(v, x => x.Rule == "FN001");
    }

    [Fact]
    public void Fn001_skips_method_declarations()
    {
        // `public IntEditAdaptor Year()` is a declaration, not a call to year().
        var v = Run("public IntEditAdaptor Year()\n{\n}");
        Assert.DoesNotContain(v, x => x.Rule == "FN001");
    }

    [Fact]
    public void Fn002_flags_ax2012_function_that_no_longer_exists()
    {
        var v = Run("transDate d = dateMin(a, b);");
        Assert.Contains(v, x => x.Rule == "FN002" && x.Severity == "error");
    }

    // ── TTS rules ────────────────────────────────────────────────────────────

    [Fact]
    public void Tts001_flags_unbalanced_ttsbegin()
    {
        var code = """
            void run()
            {
                ttsbegin;
                custTable.insert();
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "TTS001" && x.Severity == "warning");
    }

    [Fact]
    public void Tts001_counts_per_method_not_across_file()
    {
        // One method opening two transactions and another closing… each balanced
        // region individually is fine; separate methods must not be conflated.
        var code = """
            void a()
            {
                ttsbegin;
                ttscommit;
            }
            void b()
            {
                ttsbegin;
                ttscommit;
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "TTS001");
    }

    [Fact]
    public void Tts001_credits_ttsabort()
    {
        var code = """
            void run()
            {
                ttsbegin;
                if (bad)
                {
                    ttsabort;
                }
                else
                {
                    ttscommit;
                }
            }
            """;
        // begins=1, commits=1 → balanced already; also 1 begin vs abort-only path is legal.
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "TTS001");
    }

    [Fact]
    public void Tts002_flags_dead_catch_inside_tts()
    {
        var code = """
            ttsbegin;
            try
            {
                custTable.update();
            }
            catch (Exception::Error)
            {
                info("never reached");
            }
            ttscommit;
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "TTS002" && x.Severity == "warning");
    }

    [Fact]
    public void Tts002_allows_updateconflict_and_duplicatekey_catches()
    {
        var code = """
            ttsbegin;
            try
            {
                custTable.update();
            }
            catch (Exception::UpdateConflict)
            {
                retryCount++;
                if (retryCount > 3) throw error("@M:Label");
                retry;
            }
            ttscommit;
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "TTS002");
    }

    [Fact]
    public void Tts003_flags_unguarded_retry()
    {
        var code = """
            try
            {
                this.process();
            }
            catch (Exception::Deadlock)
            {
                retry;
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "TTS003" && x.Severity == "warning");
    }

    [Fact]
    public void Tts003_quiet_when_retry_is_guarded()
    {
        var code = """
            try
            {
                this.process();
            }
            catch (Exception::Deadlock)
            {
                retryCount++;
                if (retryCount > maxRetries)
                {
                    throw error("@M:Label");
                }
                retry;
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "TTS003");
    }

    // ── CS001 / MAC001 / KW001 ───────────────────────────────────────────────

    [Fact]
    public void Cs001_flags_csharp_constructs()
    {
        Assert.Contains(Run("str s = $\"total: {x}\";"), x => x.Rule == "CS001");
        Assert.Contains(Run("var f = x => x + 1;"), x => x.Rule == "CS001");
        Assert.Contains(Run("foreach (var item in items) {}"), x => x.Rule == "CS001");
        Assert.Contains(Run("string name;"), x => x.Rule == "CS001");
        Assert.Contains(Run("bool flag;"), x => x.Rule == "CS001");
        Assert.Contains(Run("public override void run() {}"), x => x.Rule == "CS001");
        Assert.Contains(Run("private protected void helper() {}"), x => x.Rule == "CS001");
        Assert.Contains(Run("catch (System.ArgumentException ex)"), x => x.Rule == "CS001");
    }

    [Fact]
    public void Cs001_quiet_on_valid_xpp()
    {
        var v = Run("boolean flag; str name; int64 big; real amount;");
        Assert.DoesNotContain(v, x => x.Rule == "CS001");
    }

    [Fact]
    public void Mac001_flags_directive_with_space()
    {
        var v = Run("#define MAX_RETRIES(5)");
        Assert.Contains(v, x => x.Rule == "MAC001" && x.Severity == "error");
    }

    [Fact]
    public void Mac001_allows_dot_form()
    {
        var v = Run("#define.MaxRetries(5)\n#localmacro.MyBlock\n#endmacro");
        Assert.DoesNotContain(v, x => x.Rule == "MAC001");
    }

    [Fact]
    public void Kw001_flags_variable_named_after_reserved_word()
    {
        // `having` is reserved without being implemented — the compiler reports the
        // failure on the token that follows, never on the name.
        var v = Run("int having;");
        Assert.Contains(v, x => x.Rule == "KW001" && x.Severity == "error");
    }

    [Fact]
    public void Kw001_allows_ordinary_names_and_exempted_in()
    {
        var v = Run("int count1; str in;");
        Assert.DoesNotContain(v, x => x.Rule == "KW001" && x.Excerpt.Contains("count1"));
        // `in` is reserved but exempted.
        Assert.DoesNotContain(v, x => x.Rule == "KW001" && x.Excerpt.Contains("str in"));
    }

    // ── ATTR001 / ATTR002 / EXT001 ───────────────────────────────────────────

    [Fact]
    public void Attr001_flags_non_literal_attribute_argument()
    {
        var code = """
            [SysEntryPointAttribute(myVariable + 1)]
            public void run()
            {
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "ATTR001" && x.Severity == "error");
    }

    [Fact]
    public void Attr001_allows_literals_intrinsics_enums_and_macros()
    {
        var code = """
            [SRSReportParameterAttribute(classStr(MyContract))]
            [DataMemberAttribute('SalesId')]
            [SysObsolete("Use MyNewClass.", false, 31\12\2026)]
            [MyAttr(MyEnum::Value)]
            [OtherAttr(#MyMacro)]
            public void run()
            {
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "ATTR001");
    }

    [Fact]
    public void Attr001_skips_container_literals_and_multi_assignment()
    {
        var code = """
            [DatabaseLogType::Update, tableNum(CustTable), fieldNum(CustTable, AccountNum)]
            [a, b] = f();
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "ATTR001");
    }

    [Fact]
    public void Attr002_flags_sysobsolete_with_missing_arguments()
    {
        var code = """
            [SysObsolete("Use MyNewClass.")]
            public void run()
            {
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "ATTR002" && x.Severity == "warning");
    }

    [Fact]
    public void Ext001_flags_extension_class_neither_coc_nor_static()
    {
        var code = """
            public class CustTable_Extension
            {
                public void helper()
                {
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "EXT001" && x.Severity == "error");
    }

    [Fact]
    public void Ext001_flags_instance_method_in_static_extension_class()
    {
        var code = """
            public static class CustTable_Extension
            {
                public str helper(CustTable _target)
                {
                    return _target.AccountNum;
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "EXT001" && x.Excerpt.Contains("helper"));
    }

    [Fact]
    public void Ext001_quiet_on_proper_static_extension_class()
    {
        var code = """
            public static class CustTable_Extension
            {
                public static str helper(CustTable _target)
                {
                    return _target.AccountNum;
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "EXT001");
    }

    // ── RPT rules ────────────────────────────────────────────────────────────

    [Fact]
    public void Rpt001_flags_dp_reading_contract_without_parameter_attribute()
    {
        var code = """
            [SRSReportDataSetAttribute(tableStr(MyTmp))]
            public class MyDP extends SRSReportDataProviderBase
            {
                public void processReport()
                {
                    MyContract contract = this.parmDataContract() as MyContract;
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "RPT001" && x.Severity == "error");
    }

    [Fact]
    public void Rpt001_exempts_preprocess_dp()
    {
        var code = """
            public class MyDP extends SRSReportDataProviderPreProcessTempDB
            {
                public void processReport()
                {
                    MyContract contract = this.parmDataContract() as MyContract;
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "RPT001");
    }

    [Fact]
    public void Rpt002_flags_dp_without_dataset_getter()
    {
        var code = """
            [SRSReportParameterAttribute(classStr(MyContract))]
            public class MyDP extends SRSReportDataProviderBase
            {
                public void processReport()
                {
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "RPT002" && x.Severity == "warning");
    }

    [Fact]
    public void Rpt101_flags_report_without_design()
    {
        var v = Run("<AxReport><Name>MyReport</Name></AxReport>", "xml-report");
        Assert.Contains(v, x => x.Rule == "RPT101" && x.Severity == "error");
    }

    [Fact]
    public void Rpt102_flags_dataset_without_query()
    {
        var xml = "<AxReport><Name>R</Name><DataSets><AxReportDataSet><Name>DS</Name></AxReportDataSet></DataSets>" +
                  "<Designs><AxReportDesign><Name>AutoDesign</Name></AxReportDesign></Designs></AxReport>";
        var v = Run(xml, "xml-report");
        Assert.Contains(v, x => x.Rule == "RPT102" && x.Severity == "warning");
    }

    [Fact]
    public void Xml_report_code_type_skips_xpp_keyword_rules()
    {
        // RDL in CDATA is full of SQL — the X++ rules must not run over a report document.
        var xml = "<AxReport><Name>R</Name><Designs><AxReportDesign><Name>AutoDesign</Name>" +
                  "<Source><![CDATA[SELECT * FROM T1 LEFT JOIN T2 ON T1.A = T2.A]]></Source>" +
                  "</AxReportDesign></Designs></AxReport>";
        var v = Run(xml, "xml-report");
        Assert.DoesNotContain(v, x => x.Rule == "SEL007");
    }

    [Fact]
    public void Normalize_recognises_xml_report_aliases()
    {
        Assert.Equal(XppValidator.CodeTypeXmlReport, XppValidator.NormalizeCodeType("xml-report"));
        Assert.Equal(XppValidator.CodeTypeXmlReport, XppValidator.NormalizeCodeType("XmlReport"));
    }
}
