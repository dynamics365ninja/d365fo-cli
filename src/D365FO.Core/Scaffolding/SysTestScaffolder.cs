using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Scaffolds a minimal, ATL-ready <c>SysTestCase</c> skeleton (issue #107).
/// MVP scope only: no automatic assertion/test-logic generation, no ATL
/// Entity/Query/Specification generation — the <c>--atl</c> flag emits only
/// the <c>AtlDataRootNode</c> field and its <c>construct()</c> call inside
/// <c>setUpTestCase()</c>, leaving actual ATL object wiring to the developer
/// (or Microsoft's ATL Wizards).
/// </summary>
public static class SysTestScaffolder
{
    /// <summary>
    /// Scaffolds an <c>AxClass</c> extending <c>SysTestCase</c> with a single
    /// <c>[SysTestMethod]</c> Arrange/Act/Assert skeleton.
    /// </summary>
    /// <param name="className">Test class name (e.g. <c>CustServiceTest</c>).</param>
    /// <param name="dataAreaId">
    /// Company for <c>[SysTestCaseDataDependency('&lt;Company&gt;')]</c>. The
    /// attribute is omitted entirely when null/blank — the MVP invents no
    /// fallback company.
    /// </param>
    /// <param name="atl">
    /// When true, adds an <c>AtlDataRootNode data;</c> field and a
    /// <c>setUpTestCase()</c> override that calls <c>super();</c> then
    /// <c>data = AtlDataRootNode::construct();</c>.
    /// </param>
    /// <param name="subject">
    /// Optional subject for the generated test method name
    /// (<c>&lt;subject&gt;_scenario_expectedResult</c>). Typically the resolved
    /// <c>--method</c> value; defaults to the literal <c>subject</c> placeholder
    /// when no source method was supplied.
    /// </param>
    public static XDocument TestClass(
        string className,
        string? dataAreaId = null,
        bool atl = false,
        string? subject = null)
    {
        var testMethodSubject = string.IsNullOrWhiteSpace(subject) ? "subject" : subject;
        var testMethodName = $"{testMethodSubject}_scenario_expectedResult";

        var attributeLine = string.IsNullOrWhiteSpace(dataAreaId)
            ? ""
            : $"[SysTestCaseDataDependency('{dataAreaId}')]\n";

        var fieldDecl = atl ? "    AtlDataRootNode data;\n" : "";

        var declaration =
            attributeLine +
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

        var testMethodSrc =
            "[SysTestMethod]\n" +
            $"public void {testMethodName}()\n" +
            "{\n" +
            "    // Arrange\n" +
            "\n" +
            "    // Act\n" +
            "\n" +
            "    // Assert\n" +
            "}\n";
        methodElements.Add(new XElement("Method",
            new XElement("Name", testMethodName),
            new XElement("Source", testMethodSrc)));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("Extends", "SysTestCase"),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods", methodElements))));
    }
}
