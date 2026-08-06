using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

public class XppcDiagnosticsTests
{
    [Fact]
    public void Parses_full_dynamics_uri_line()
    {
        var log = "Compile Error: Class Method dynamics://MyModel/MyClass/myMethod: [(28,27),(28,28)]: ';' expected.";
        var d = Assert.Single(XppcDiagnostics.Parse(log));
        Assert.Equal("error", d.Severity);
        Assert.Equal("Class Method", d.Kind);
        Assert.Equal("MyModel", d.Model);
        Assert.Equal("MyClass", d.Object);
        Assert.Equal("myMethod", d.Member);
        Assert.Equal(28, d.Line);
        Assert.Equal(27, d.Column);
        Assert.Equal("';' expected.", d.Message);
        Assert.NotNull(d.Hint); // semicolon hint
    }

    [Fact]
    public void Parses_line_without_member()
    {
        var log = "Compile Warning: Table dynamics://MyModel/MyTable: [(1,1)]: Some warning text";
        var d = Assert.Single(XppcDiagnostics.Parse(log));
        Assert.Equal("warning", d.Severity);
        Assert.Equal("MyTable", d.Object);
        Assert.Null(d.Member);
    }

    [Fact]
    public void Falls_back_to_simple_severity_prefix()
    {
        var log = "Compile Fatal Error: Out of memory during AOT compilation";
        var d = Assert.Single(XppcDiagnostics.Parse(log));
        Assert.Equal("error", d.Severity);
        Assert.Null(d.Object);
        Assert.Contains("Out of memory", d.Message);
    }

    [Fact]
    public void Ignores_unrelated_lines()
    {
        var log = """
            Build started.
            Compile Error: Class Method dynamics://M/C/m: [(1,2)]: unknown type 'FooBar'
            1 Warning(s)
            """;
        var diags = XppcDiagnostics.Parse(log);
        var d = Assert.Single(diags);
        Assert.Contains("unknown type", d.Message);
        Assert.Contains("validate references", d.Hint);
    }

    /// <summary>
    /// Verbatim lines from <c>Dynamics.AX.&lt;model&gt;.xppc.log</c> on a real
    /// installation (X++ Compiler 7.0.7996.33), captured while building the L3 build
    /// oracle. Every one of them was silently discarded by the parser before this:
    /// the compile reported <c>Errors: 8</c> and the harness reported every golden
    /// clean, which is precisely the confident lie this repo's triage rubric puts
    /// first.
    /// </summary>
    [Theory]
    // Metadata-provider complaint: no source location at all.
    [InlineData(
        "MetadataProvider Error: View dynamics://View/ConFmVehicleView: On view 'ConFmVehicleView', the field 'VIN' refers to a nonexistent table or view named 'FmVehicle'.",
        "error", "ConFmVehicleView")]
    // Source/XML mismatch: located, but with no dynamics:// URL.
    [InlineData(
        "MetadataProvider Error: Class Method ConFmVehicleServiceRunHandler/run: [(3,5),(7,6)]: The method name in the source code, 'run', does not match the name in the XML file, ''.",
        "error", "ConFmVehicleServiceRunHandler")]
    // Reader crash: the object is named on the severity line, the exception follows.
    [InlineData("Unspecified Fatal Error: file /Query/ConFmVehicleQuery", "error", "ConFmVehicleQuery")]
    // A TODO the scaffolder left on purpose — neither an error nor a warning.
    [InlineData(
        "TaskListItem Information: Class Method dynamics://Class/ConFmVehicleRun/Method/run: [(34,5),(34,23)]: TODO: implement",
        "information", "ConFmVehicleRun")]
    // The metadata validator's own shape: MetaModel type / object / member.
    [InlineData(
        "Metadata Error: AxDataEntityView/ConFmVehicleEntity/PrimaryKey: The Primary Key property must be set, when the Is Public property is set to 'Yes'.",
        "error", "ConFmVehicleEntity")]
    // …where the member path can be a whole form control tree.
    [InlineData(
        "Metadata Error: AxForm/ConFmVehicleList/Design/Controls/Grid/DataGroup: Field group 'Overview' does not exist.",
        "error", "ConFmVehicleList")]
    // …and the object name can contain a dot: an extension object is Target.Suffix.
    [InlineData(
        "Metadata Error: AxEnumExtension/NoYes.Extension: Base enum 'NoYes' cannot be extended.",
        "error", "NoYes.Extension")]
    // The AOT form-pattern registry reports under its own prefix again.
    [InlineData(
        "FormPatternValidation Error: AxForm/ConFmVehicleDetails/Design/Controls/Tab/TabPageGeneral/ColumnsMode: Property 'ColumnsMode' must have value 'Fill' per pattern 'Fields and Field Groups'.",
        "error", "ConFmVehicleDetails")]
    [InlineData(
        "FormPatternValidation Fatal Error: AxForm/ConFmVehicleListPage/Design: Unable to validate pattern 'ListPage 1.1'. Message: Pattern 'ListPage 1.1' not found.",
        "error", "ConFmVehicleListPage")]
    public void Parses_the_forms_a_real_compiler_emits(string line, string severity, string obj)
    {
        var d = Assert.Single(XppcDiagnostics.Parse(line));

        Assert.Equal(severity, d.Severity);
        Assert.Equal(obj, d.Object);
    }

    [Fact]
    public void A_TODO_marker_is_not_counted_as_a_problem()
    {
        var diagnostics = XppcDiagnostics.Parse(
            "TaskListItem Information: Class Method dynamics://Class/C/Method/run: [(1,1)]: TODO: implement");

        Assert.Empty(diagnostics.Where(d => d.Severity is "error" or "warning"));
    }

    [Fact]
    public void Detects_stale_symbols()
    {
        Assert.True(XppcDiagnostics.IndicatesStaleSymbols(
            "Class Foo has not been successfully compiled since it was last changed."));
        Assert.False(XppcDiagnostics.IndicatesStaleSymbols("All good."));
    }
}
