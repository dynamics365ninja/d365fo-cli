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
