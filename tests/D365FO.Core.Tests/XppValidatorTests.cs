using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

public class XppValidatorTests
{
    private static IReadOnlyList<XppViolation> Run(string code, string codeType = "xpp", IPropertyStatsProvider? stats = null)
        => XppValidator.Validate(code, codeType, stats);

    // ── SEL rules ────────────────────────────────────────────────────────────

    [Fact]
    public void Sel001_flags_today()
    {
        var v = Run("transDate d = today();");
        Assert.Contains(v, x => x.Rule == "SEL001" && x.Severity == "error");
    }

    [Fact]
    public void Sel001_skips_commented_today()
    {
        var v = Run("// transDate d = today();");
        Assert.DoesNotContain(v, x => x.Rule == "SEL001");
    }

    [Fact]
    public void Sel002_flags_forceLiterals()
    {
        var v = Run("select forceLiterals custTable;");
        Assert.Contains(v, x => x.Rule == "SEL002" && x.Severity == "error");
    }

    [Fact]
    public void Sel003_flags_crossCompany_on_join()
    {
        var v = Run("select custTable join crossCompany salesTable;");
        Assert.Contains(v, x => x.Rule == "SEL003" && x.Severity == "error");
    }

    [Fact]
    public void Sel004_flags_nested_while_select_without_join()
    {
        var code = """
            while select custTable
            {
                while select salesTable
                {
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "SEL004" && x.Severity == "warning");
    }

    [Fact]
    public void Sel004_quiet_when_join_present()
    {
        var code = """
            while select custTable
                join salesTable
            {
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "SEL004");
    }

    [Fact]
    public void Sel005_flags_function_call_in_where()
    {
        var v = Run("select custTable where custTable.AccountNum == getCurrentAccount();");
        Assert.Contains(v, x => x.Rule == "SEL005");
    }

    [Fact]
    public void Sel005_allows_intrinsics_in_where()
    {
        var v = Run("select inventDim where inventDim.InventDimId == queryRange(fieldNum(InventDim, InventDimId));");
        Assert.DoesNotContain(v, x => x.Rule == "SEL005" && x.Excerpt.Contains("fieldNum"));
    }

    [Fact]
    public void Sel005_clause_closes_at_statement_terminator()
    {
        var code = """
            int certCount = (select count(RecId) from certTable
                where certTable.Status == status).RecId;
            info(strFmt("Found %1 certificates", certCount));
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "SEL005" && x.Excerpt.Contains("info"));
        Assert.DoesNotContain(v, x => x.Rule == "SEL005" && x.Excerpt.Contains("strFmt"));
    }

    [Fact]
    public void Sel005_state_does_not_leak_into_later_methods()
    {
        var code = """
            public void run1()
            {
                select custTable where custTable.Blocked == blockedStatus;
            }

            public void run2()
            {
                this.doWork(someArg);
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "SEL005" && x.Excerpt.Contains("doWork"));
        Assert.DoesNotContain(v, x => x.Rule == "SEL005" && x.Excerpt.Contains("run2"));
    }

    [Fact]
    public void Sel005_ignores_calls_before_where_on_same_line()
    {
        var v = Run("select count(RecId) from certTable where certTable.Status == status;");
        Assert.DoesNotContain(v, x => x.Rule == "SEL005");
    }

    [Fact]
    public void Sel005_still_flags_call_in_multiline_where()
    {
        var code = """
            select custTable
                where custTable.AccountNum == getCurrentAccount()
            {
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "SEL005" && x.Excerpt.Contains("getCurrentAccount"));
    }

    // ── COC rules ────────────────────────────────────────────────────────────

    [Fact]
    public void Coc001_flags_default_param_in_wrapper()
    {
        var code = """
            [ExtensionOf(classStr(SalesFormLetter))]
            final class SalesFormLetter_Extension
            {
                public void salute(str message = "Hi")
                {
                    next salute(message);
                }
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC001" && x.Severity == "error");
    }

    [Fact]
    public void Coc001_allows_parm_accessors()
    {
        var code = """
            [ExtensionOf(classStr(SomeContract))]
            final class SomeContract_Extension
            {
                public str parmName(str _name = name)
                {
                    return name;
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule == "COC001");
    }

    [Fact]
    public void Coc002_flags_non_final_extension()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            class CustTable_Extension
            {
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC002" && x.Severity == "error");
    }

    [Fact]
    public void Coc003_flags_bad_extension_name()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            final class CustTableMyStuff
            {
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "COC003" && x.Severity == "error");
    }

    [Fact]
    public void Coc_rules_quiet_on_clean_extension()
    {
        var code = """
            [ExtensionOf(tableStr(CustTable))]
            final class CustTable_Extension
            {
                public void validateWrite()
                {
                    next validateWrite();
                }
            }
            """;
        var v = Run(code);
        Assert.DoesNotContain(v, x => x.Rule.StartsWith("COC"));
    }

    // ── BP rules ─────────────────────────────────────────────────────────────

    [Fact]
    public void Bp001_flags_hardcoded_info_string()
    {
        var v = Run("info(\"Customer was created.\");");
        Assert.Contains(v, x => x.Rule == "BP001" && x.Severity == "error");
    }

    [Fact]
    public void Bp001_allows_label_reference()
    {
        var v = Run("info(\"@MyModel:CustomerCreated\");");
        Assert.DoesNotContain(v, x => x.Rule == "BP001");
    }

    [Fact]
    public void Bp002_flags_doInsert()
    {
        var v = Run("custTable.doInsert();");
        Assert.Contains(v, x => x.Rule == "BP002" && x.Severity == "warning");
    }

    [Fact]
    public void Bp003_flags_generic_doc_comment()
    {
        var code = """
            /// MyHelper class.
            class MyHelper
            {
            }
            """;
        var v = Run(code);
        Assert.Contains(v, x => x.Rule == "BP003");
    }

    // ── XML rules ────────────────────────────────────────────────────────────

    [Fact]
    public void Xml001_flags_missing_alternate_key()
    {
        var xml = "<AxTable><Name>MyTable</Name><Indexes/></AxTable>";
        var v = Run(xml, "xml-table");
        Assert.Contains(v, x => x.Rule == "XML001" && x.Severity == "error");
    }

    [Fact]
    public void Xml001_quiet_with_alternate_key()
    {
        var xml = "<AxTable><Name>MyTable</Name><Indexes><AxTableIndex><AlternateKey>Yes</AlternateKey></AxTableIndex></Indexes></AxTable>";
        var v = Run(xml, "xml-table");
        Assert.DoesNotContain(v, x => x.Rule == "XML001");
    }

    [Fact]
    public void Xml001_quiet_for_empty_table_extension()
    {
        // `d365fo generate extension Table <target>` always emits this exact
        // shell — a table extension that adds zero fields/indexes of its own
        // (a common, valid pattern: placeholder shell, CoC/event-handler
        // target, etc.). It inherits the base table's alternate key
        // automatically; there is nothing in the extension's own delta to
        // require one. Real BPCheckAlternateKeyAbsent's documented
        // precondition (Customization Analysis Report docs) is "tables that
        // have a unique index" — an index-less extension never meets it.
        var xml = "<AxTableExtension><Name>FmVehicle.Extension</Name></AxTableExtension>";
        var v = Run(xml, "xml-any");
        Assert.DoesNotContain(v, x => x.Rule == "XML001");
    }

    [Fact]
    public void Xml001_flags_table_extension_that_adds_an_index_without_alternate_key()
    {
        // Once the extension actually introduces its own AxTableIndex, the
        // real rule's precondition ("has a unique index") is met for that
        // delta — an extension-added index without an alternate key should
        // still be flagged.
        var xml = "<AxTableExtension><Name>FmVehicle.Extension</Name>" +
                   "<Indexes><AxTableIndex><Name>NewIdx</Name></AxTableIndex></Indexes></AxTableExtension>";
        var v = Run(xml, "xml-any");
        Assert.Contains(v, x => x.Rule == "XML001" && x.Severity == "error");
    }

    [Fact]
    public void Xml002_and_003_fire_with_static_defaults()
    {
        var xml = "<AxTable><Name>MyTable</Name><Fields/></AxTable>";
        var v = Run(xml, "xml-table");
        Assert.Contains(v, x => x.Rule == "XML002");
        Assert.Contains(v, x => x.Rule == "XML003");
        // ClusteredIndex default is OFF without mined stats
        Assert.DoesNotContain(v, x => x.Rule == "XML005");
    }

    [Fact]
    public void Xml004_flags_field_without_edt()
    {
        var xml = """
            <AxTable><Name>T</Name><Label>@X:L</Label><TableGroup>Main</TableGroup>
            <Fields>
              <AxTableField><Name>BadField</Name></AxTableField>
              <AxTableField><Name>GoodField</Name><ExtendedDataType>Name</ExtendedDataType></AxTableField>
            </Fields>
            <AlternateKey>Yes</AlternateKey></AxTable>
            """;
        var v = Run(xml, "xml-table");
        Assert.Contains(v, x => x.Rule == "XML004" && x.Excerpt.Contains("BadField"));
        Assert.DoesNotContain(v, x => x.Rule == "XML004" && x.Excerpt.Contains("GoodField"));
    }

    [Fact]
    public void Xml005_fires_when_stats_prove_standard_usage()
    {
        var stats = new FakeStats(ratio: 0.95, total: 1000);
        var xml = "<AxTable><Name>T</Name><Label>@X:L</Label><TableGroup>Main</TableGroup><AlternateKey>Yes</AlternateKey></AxTable>";
        var v = Run(xml, "xml-table", stats);
        Assert.Contains(v, x => x.Rule == "XML005");
        Assert.Contains(v, x => x.Rule == "XML005" && x.Fix.Contains("95%"));
    }

    [Fact]
    public void Property_rules_quiet_when_stats_say_uncommon()
    {
        var stats = new FakeStats(ratio: 0.10, total: 1000);
        var xml = "<AxTable><Name>T</Name><AlternateKey>Yes</AlternateKey></AxTable>";
        var v = Run(xml, "xml-table", stats);
        Assert.DoesNotContain(v, x => x.Rule == "XML002");
        Assert.DoesNotContain(v, x => x.Rule == "XML003");
    }

    [Fact]
    public void Xpp_code_type_skips_xml_rules()
    {
        var v = Run("info(\"@SYS1\");", "xpp");
        Assert.DoesNotContain(v, x => x.Rule.StartsWith("XML"));
    }

    private sealed class FakeStats(double ratio, long total) : IPropertyStatsProvider
    {
        public (long Present, long Total, double Ratio) GetPropertyPresenceRatio(string nodeType, string property)
            => ((long)(ratio * total), total, ratio);

        public IReadOnlyList<(string Value, long Count)> GetPropertyValueDistribution(string nodeType, string property, int limit = 5)
            => new[] { ("Main", 600L), ("Transaction", 300L), ("Parameter", 100L) };
    }
}
