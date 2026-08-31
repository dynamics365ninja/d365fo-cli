using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Scaffolds an ATL-ready <c>SysTestCase</c> skeleton (issue #107) — the red half of a
/// red/green cycle.
///
/// Every generated test method FAILS on purpose (<c>this.fail(...)</c>): a scaffolded test
/// that passes before the behaviour exists has proven nothing about the assertion inside it,
/// and the framework gives no other signal that the developer has not written it yet.
///
/// Only API the platform actually has is emitted (upstream read it from SysTestCase /
/// SysTestAssert in ApplicationFoundation and compiled the result under xppc with 0 errors):
/// <list type="bullet">
/// <item>the asserts come from SysTestAssert, which SysTestCase extends;</item>
/// <item>an expected exception is DECLARED with <c>parmExceptionExpected(true)</c> before the
/// call — there is no <c>assertExpectedException</c> in X++;</item>
/// <item><c>SysTestTarget</c>'s second argument is a <c>UtilElementType</c>, not a method
/// name;</item>
/// <item>rollback is the framework default, so there is no attribute to add and no cleanup to
/// write.</item>
/// </list>
///
/// ATL scope: the <c>--atl</c> flag emits only the <c>AtlDataRootNode</c> field and its
/// <c>construct()</c> call inside <c>setUpTestCase()</c>, leaving actual ATL object wiring to
/// the developer (or Microsoft's ATL Wizards).
/// </summary>
public static class SysTestScaffolder
{
    /// <summary>
    /// Scaffolds an <c>AxClass</c> extending <c>SysTestCase</c> with one red-first
    /// <c>[SysTestMethod]</c> per subject.
    /// </summary>
    /// <param name="className">Test class name (e.g. <c>CustServiceTest</c>).</param>
    /// <param name="dataAreaId">
    /// Company for <c>[SysTestCaseDataDependency('&lt;Company&gt;')]</c>. The attribute is
    /// omitted entirely when null/blank — no fallback company is invented.
    /// </param>
    /// <param name="atl">
    /// When true, adds an <c>AtlDataRootNode data;</c> field and a <c>setUpTestCase()</c>
    /// override that calls <c>super();</c> then <c>data = AtlDataRootNode::construct();</c>.
    /// </param>
    /// <param name="subjects">
    /// Subjects for the generated test methods (<c>&lt;subject&gt;_scenario_expectedResult</c>
    /// each). Typically the resolved <c>--method</c> values; defaults to a single literal
    /// <c>subject</c> placeholder when no source method was supplied.
    /// </param>
    /// <param name="targetName">
    /// The class/table under test. When given, the class carries
    /// <c>[SysTestTarget(classStr|tableStr(&lt;name&gt;), UtilElementType::Class|Table)]</c>
    /// so tooling can find the tests that cover the target.
    /// </param>
    /// <param name="targetIsTable">Whether <paramref name="targetName"/> is a table.</param>
    public static XDocument TestClass(
        string className,
        string? dataAreaId = null,
        bool atl = false,
        IReadOnlyList<string>? subjects = null,
        string? targetName = null,
        bool targetIsTable = false)
    {
        var effectiveSubjects = subjects is { Count: > 0 } ? subjects : ["subject"];

        var attributeLines = "";
        if (!string.IsNullOrWhiteSpace(dataAreaId))
            attributeLines += $"[SysTestCaseDataDependency('{dataAreaId}')]\n";
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            attributeLines += targetIsTable
                ? $"[SysTestTarget(tableStr({targetName}), UtilElementType::Table)]\n"
                : $"[SysTestTarget(classStr({targetName}), UtilElementType::Class)]\n";
        }

        var fieldDecl = atl ? "    AtlDataRootNode data;\n" : "";

        var declaration =
            attributeLines +
            $"public class {className} extends SysTestCase\n" +
            "{\n" +
            fieldDecl +
            "}\n";

        var methodElements = new List<XElement>();

        if (atl)
        {
            var setUpTestCaseSrc =
                "public void setUpTestCase()\n" +
                "{\n" +
                "    super();\n" +
                "\n" +
                "    data = AtlDataRootNode::construct();\n" +
                "}\n";
            methodElements.Add(new XElement("Method",
                new XElement("Name", "setUpTestCase"),
                new XElement("Source", setUpTestCaseSrc)));
        }

        foreach (var subject in effectiveSubjects)
        {
            var testMethodName = $"{subject}_scenario_expectedResult";
            var testMethodSrc =
                "[SysTestMethod]\n" +
                $"public void {testMethodName}()\n" +
                "{\n" +
                "    // Arrange\n" +
                "\n" +
                "    // Act\n" +
                "\n" +
                "    // Assert — replace the fail with the assertion this test exists for,\n" +
                "    // e.g. this.assertEquals(expected, actual, 'what should hold').\n" +
                "    // Red-first: a scaffolded test that passes before the behaviour exists\n" +
                "    // has proven nothing. An EXPECTED exception is declared, not asserted:\n" +
                "    // this.parmExceptionExpected(true) before the call under test.\n" +
                $"    this.fail('{testMethodName} is not implemented yet.');\n" +
                "}\n";
            methodElements.Add(new XElement("Method",
                new XElement("Name", testMethodName),
                new XElement("Source", testMethodSrc)));
        }

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods", methodElements))));
    }
}
