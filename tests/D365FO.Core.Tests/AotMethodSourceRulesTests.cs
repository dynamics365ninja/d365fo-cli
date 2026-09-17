using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// XML014 — an AxClass <c>&lt;Method&gt;</c> whose source does not declare exactly one method
/// named like its <c>&lt;Name&gt;</c> (issue #213). xppc reports both shapes as
/// "The method name in the source code, 'x', does not match the name in the XML file, 'y'".
/// The "is allowed" cases are shapes taken from shipped classes, which all compile.
/// </summary>
public class AotMethodSourceRulesTests
{
    private static IReadOnlyList<XppViolation> Check(string xml)
        => XppValidator.Validate(xml, XppValidator.CodeTypeXmlAny)
            .Where(v => v.Rule == AotMethodSourceRules.RuleMethodSourceMismatch)
            .ToList();

    private static string ClassWithMethod(string name, string source) => $$"""
        <AxClass>
          <Name>Example</Name>
          <SourceCode>
            <Declaration><![CDATA[public class Example
        {
        }]]></Declaration>
            <Methods>
              <Method>
                <Name>{{name}}</Name>
                <Source><![CDATA[{{source}}]]></Source>
              </Method>
            </Methods>
          </SourceCode>
        </AxClass>
        """;

    [Fact]
    public void Method_name_mismatch_is_reported()
    {
        var violation = Assert.Single(Check(ClassWithMethod("expectedName", "public void actualName()\n{\n}")));
        Assert.Equal("error", violation.Severity);
        Assert.Contains("actualName", violation.Fix);
        Assert.Contains("expectedName", violation.Fix);
    }

    [Fact]
    public void Method_node_with_two_declarations_is_reported()
    {
        var source = "public void firstMethod()\n{\n}\n\npublic void secondMethod()\n{\n}";
        var violation = Assert.Single(Check(ClassWithMethod("firstMethod", source)));
        Assert.Contains("exactly one", violation.Fix);
        Assert.Contains("contains 2", violation.Fix);
    }

    [Fact]
    public void Method_node_with_no_declaration_is_reported()
    {
        var violation = Assert.Single(Check(ClassWithMethod("orphan", "info(\"no header\");")));
        Assert.Contains("contains 0", violation.Fix);
    }

    [Fact]
    public void Matching_single_declaration_is_allowed()
        => Assert.Empty(Check(ClassWithMethod("expectedName", "public void expectedName()\n{\n}")));

    [Fact]
    public void Case_only_difference_is_allowed()
        // xppc accepts it; RetailSrsReportDataProviderChannelBase.InsertChannelsToTmpTable ships so.
        => Assert.Empty(Check(ClassWithMethod("DoWork", "public void doWork()\n{\n}")));

    [Theory]
    // Statements in the body that look like "type name(" — each one used to count as a declaration.
    [InlineData("public void run()\n{\n    if (!x)\n    {\n        throw error(\"@SYS1\");\n    }\n    else if (y)\n    {\n        print foo(1);\n    }\n}", "run")]
    [InlineData("public void run()\n{\n    next run();\n    super();\n}", "run")]
    // Modifiers and return types a keyword list would have to know about.
    [InlineData("internal display QMSSigningStatus qmsIsRecordSigned()\n{\n    return QMSSigningStatus::None;\n}", "qmsIsRecordSigned")]
    [InlineData("public static server client Args buildArgs(Common _record)\n{\n    return new Args();\n}", "buildArgs")]
    [InlineData("public System.String toDotNet()\n{\n    return '';\n}", "toDotNet")]
    [InlineData("delegate void OnInitialized(XppPrePostArgs _args)\n{\n}", "OnInitialized")]
    // Attributes, on their own line or on the header's line, and a doc comment above them.
    [InlineData("/// <summary>\n/// Sends the alert (see send()).\n/// </summary>\n[Hookable(false), SysObsolete('use send2()', false)]\ninternal void send()\n{\n}", "send")]
    [InlineData("[DataMemberAttribute('Id')] public str parmId(str _id = id)\n{\n    id = _id;\n    return id;\n}", "parmId")]
    // A local function is part of its method, not a second declaration.
    [InlineData("public int outer()\n{\n    int inner(int _x)\n    {\n        return _x;\n    }\n    return inner(1);\n}", "outer")]
    // Braces inside strings and comments do not unbalance the walk.
    [InlineData("public str braces()\n{\n    // }\n    return \"{\";\n}", "braces")]
    public void Shipped_method_shapes_are_allowed(string source, string name)
        => Assert.Empty(Check(ClassWithMethod(name, source)));

    [Fact]
    public void Method_written_as_a_macro_invocation_is_skipped()
        // JmgSerialNumberSpecificationContract.parmJobId: the declaration only exists after preprocessing.
        => Assert.Empty(Check(ClassWithMethod("parmJobId", "\n[DataMember('JobIdentification')]\n#ParmMethod(JobId)\n")));

    [Fact]
    public void Methods_of_other_families_are_not_checked()
    {
        var xml = """
            <AxTable>
              <Name>ExampleTable</Name>
              <SourceCode><Methods><Method>
                <Name>expectedName</Name>
                <Source><![CDATA[public void actualName()
            {
            }]]></Source>
              </Method></Methods></SourceCode>
            </AxTable>
            """;
        var violations = new List<XppViolation>();
        AotMethodSourceRules.Check(xml, violations);
        Assert.Empty(violations);
    }

    [Fact]
    public void Malformed_xml_is_left_to_the_other_rules()
    {
        var violations = new List<XppViolation>();
        AotMethodSourceRules.Check("<AxClass><Name>", violations);
        Assert.Empty(violations);
    }

    [Fact]
    public void Violation_points_at_the_method_node()
    {
        var violation = Assert.Single(Check(ClassWithMethod("expectedName", "public void actualName()\n{\n}")));
        Assert.Equal("AxClass/Methods/Method/expectedName", violation.Excerpt);
        Assert.Equal(8, violation.Line);
    }
}
