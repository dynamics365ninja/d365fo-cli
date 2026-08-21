using System.Text.Json;
using System.Xml.Linq;
using D365FO.Cli.Commands.Generate;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>generate extension Table --field</c> (#178). Adding a field to a table extension
/// used to be reachable only through <c>modify add-field</c>, which requires the
/// Windows-only metadata bridge — so on a machine without a D365FO install, or in CI,
/// there was no way to produce one at all.
/// </summary>
// Captures Console.Out, which is process-global — see GenerateWorkflowCommandTests.
[Collection("EnvIndexDb")]
public class GenerateExtensionFieldTests
{
    private static (int Exit, JsonDocument Json) Run(GenerateExtensionCommand.Settings settings)
    {
        var saved = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            var exit = new GenerateExtensionCommand().Execute(null!, settings);
            return (exit, JsonDocument.Parse(writer.ToString()));
        }
        finally
        {
            Console.SetOut(saved);
        }
    }

    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d365fo-extfield-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Fields_land_in_the_written_table_extension()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "VendInvoiceInfoLine.DSV.xml");
            var (exit, json) = Run(new GenerateExtensionCommand.Settings
            {
                Kind = "Table",
                Target = "VendInvoiceInfoLine",
                Suffix = "DSV",
                Fields = new[] { "QuantityD:Qty:mandatory", "CommentD:Description" },
                Out = path,
                Output = "json",
            });

            Assert.Equal(0, exit);
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(2, json.RootElement.GetProperty("data").GetProperty("fields").GetArrayLength());

            var root = XDocument.Load(path).Root!;
            Assert.Equal("AxTableExtension", root.Name.LocalName);

            var fields = root.Element("Fields")!.Elements("AxTableField").ToList();
            Assert.Equal(new[] { "QuantityD", "CommentD" }, fields.Select(f => f.Element("Name")!.Value));
            Assert.Equal("Yes", fields[0].Element("Mandatory")!.Value);
            Assert.Null(fields[1].Element("Mandatory"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_field_without_an_edt_is_refused_before_anything_is_written()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "VendInvoiceInfoLine.DSV.xml");
            var (exit, json) = Run(new GenerateExtensionCommand.Settings
            {
                Kind = "Table",
                Target = "VendInvoiceInfoLine",
                Suffix = "DSV",
                Fields = new[] { "QuantityD" },
                Out = path,
                Output = "json",
            });

            Assert.NotEqual(0, exit);
            Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("has no EDT", json.RootElement.GetProperty("error").GetProperty("message").GetString());
            // A field with no EDT would scaffold as AxTableFieldString against a
            // <ExtendedDataType>Name</ExtendedDataType> nobody asked for.
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData("Form")]
    [InlineData("Enum")]
    [InlineData("Edt")]
    public void Fields_on_a_kind_that_cannot_hold_them_are_refused(string kind)
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "Something.DSV.xml");
            var (exit, json) = Run(new GenerateExtensionCommand.Settings
            {
                Kind = kind,
                Target = "SomeTarget",
                Suffix = "DSV",
                Fields = new[] { "QuantityD:Qty" },
                Out = path,
                Output = "json",
            });

            Assert.NotEqual(0, exit);
            var message = json.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            Assert.Contains("--field applies to Table extensions only", message);
            // Dropped-on-read is the failure mode worth naming: the provider accepts the
            // document and simply loses the members it has no home for.
            Assert.Contains("dropped on read", message);
            Assert.False(File.Exists(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void The_bare_skeleton_is_unchanged_when_no_field_is_passed()
    {
        var dir = NewDir();
        try
        {
            var path = Path.Combine(dir, "VendInvoiceInfoLine.DSV.xml");
            var (exit, _) = Run(new GenerateExtensionCommand.Settings
            {
                Kind = "Table",
                Target = "VendInvoiceInfoLine",
                Suffix = "DSV",
                Out = path,
                Output = "json",
            });

            Assert.Equal(0, exit);
            Assert.Null(XDocument.Load(path).Root!.Element("Fields"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
