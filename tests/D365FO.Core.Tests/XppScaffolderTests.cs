using System.Linq;
using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Regression coverage for <see cref="XppScaffolder"/> emitters whose output is
/// raw X++ source text, not just XML shape — a hand-authored string literal can
/// silently drift out of brace-balance without any XML validator catching it.
/// </summary>
public class XppScaffolderTests
{
    /// <summary>
    /// Found via the eval catalog (L2-event-handler-basic): the Declaration
    /// literal ended in "}}\n" (method-close + a stray extra class-close) instead
    /// of "}\n", producing an unbalanced-brace class body for every
    /// `generate event-handler` call.
    /// </summary>
    [Theory]
    [InlineData("Form")]
    [InlineData("FormDataSource")]
    [InlineData("Table")]
    [InlineData("Class")]
    public void EventHandler_DeclarationBracesAreBalanced(string sourceKind)
    {
        var doc = XppScaffolder.EventHandler("ConTestHandler", sourceKind, "TestSource", "OnInitialized", "run");
        var src = doc.Descendants("Declaration").Single().Value;

        var opens = src.Count(c => c == '{');
        var closes = src.Count(c => c == '}');
        Assert.Equal(opens, closes);
        Assert.EndsWith("}\n", src);
        Assert.DoesNotContain("}}", src);
    }
}

/// <summary>
/// Regression coverage for <see cref="NumberSequenceScaffolder"/>.
/// </summary>
public class NumberSequenceScaffolderTests
{
    /// <summary>
    /// Found via the eval catalog (L2-numberseq-basic): the scaffolded EDT root
    /// was a bare &lt;AxEdtString&gt; with no xmlns:i / i:type discriminator,
    /// which <see cref="ScaffoldFileWriter"/>'s own write-time gate rejects
    /// (WRITE_FAILED) — every `generate number-sequence` call failed after
    /// already having written the module-extension class to disk.
    /// </summary>
    [Fact]
    public void Edt_HasXsiNamespaceAndTypeDiscriminator()
    {
        var doc = NumberSequenceScaffolder.Edt("ConDemoNum", "ConDemo");
        var root = doc.Root!;

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        Assert.Equal("AxEdtString", root.Attribute(xsi + "type")?.Value);
        Assert.Equal(xsi.NamespaceName, root.Attribute(XNamespace.Xmlns + "i")?.Value);
    }

}

/// <summary>
/// Fields carried by a table extension (#178). Before this, the only way to put a field
/// on one was <c>modify add-field</c>, which is bridge-only — so the offline path stopped
/// at an empty skeleton.
/// </summary>
public class XppScaffolderExtensionTests
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    [Fact]
    public void Extension_without_fields_still_emits_the_bare_table_skeleton()
    {
        var root = XppScaffolder.Extension("table", "VendInvoiceInfoLine", "DSV").Root!;

        Assert.Equal("AxTableExtension", root.Name.LocalName);
        Assert.Equal("VendInvoiceInfoLine.DSV", root.Element("Name")!.Value);
        // A shipped AxTableExtension declares only the members it modifies, so an empty
        // <Fields> would be noise — and would churn every golden that pins the skeleton.
        Assert.Null(root.Element("Fields"));
    }

    [Fact]
    public void Extension_emits_table_fields_with_the_concrete_type_discriminator()
    {
        var doc = XppScaffolder.Extension(
            "table", "VendInvoiceInfoLine", "DSV",
            edtBaseTypeResolver: edt => edt switch { "Qty" => "Real", "Description" => "String", _ => null },
            tableFields: new[]
            {
                new TableFieldSpec("QuantityD", "Qty", null, Mandatory: true),
                new TableFieldSpec("CommentD", "Description", "@SYS1234", Mandatory: false),
            });

        var fields = doc.Root!.Element("Fields")!.Elements("AxTableField").ToList();
        Assert.Equal(2, fields.Count);

        // AxTableField is abstract: without the discriminator the metadata reader throws.
        Assert.Equal("AxTableFieldReal", fields[0].Attribute(Xsi + "type")!.Value);
        Assert.Equal("QuantityD", fields[0].Element("Name")!.Value);
        Assert.Equal("Qty", fields[0].Element("ExtendedDataType")!.Value);
        Assert.Equal("Yes", fields[0].Element("Mandatory")!.Value);

        Assert.Equal("AxTableFieldString", fields[1].Attribute(Xsi + "type")!.Value);
        Assert.Equal("@SYS1234", fields[1].Element("Label")!.Value);
        Assert.Null(fields[1].Element("Mandatory"));
    }

    [Fact]
    public void Extension_refuses_fields_on_kinds_that_have_no_Fields_member()
    {
        var fields = new[] { new TableFieldSpec("QuantityD", "Qty", null, Mandatory: false) };

        foreach (var kind in new[] { "form", "edt", "enum" })
        {
            var ex = Assert.Throws<ArgumentException>(
                () => XppScaffolder.Extension(kind, "SomeTarget", "DSV", null, fields));
            Assert.Contains("no Fields member", ex.Message);
        }
    }

    /// <summary>
    /// The offline path must produce the same field element the live path does — otherwise
    /// `generate extension --field` and `modify add-field` disagree about the same edit.
    /// </summary>
    [Fact]
    public void Extension_fields_match_the_shape_a_table_carries_them_in()
    {
        Func<string, string?> resolver = edt => edt == "Qty" ? "Real" : null;
        var spec = new TableFieldSpec("QuantityD", "Qty", null, Mandatory: true);

        var onTable = XppScaffolder.Table("SomeTable", fields: new[] { spec }, edtBaseTypeResolver: resolver)
            .Root!.Element("Fields")!.Elements("AxTableField").Single();
        var onExtension = XppScaffolder.Extension("table", "SomeTable", "DSV", resolver, new[] { spec })
            .Root!.Element("Fields")!.Elements("AxTableField").Single();

        Assert.Equal(onTable.ToString(), onExtension.ToString());
    }
}
