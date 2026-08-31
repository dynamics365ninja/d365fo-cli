using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The three compiler-checked techniques for extending a shipped report. Every shape
/// was compiled upstream against real standard objects; these tests pin the details the
/// compiler settled.
/// </summary>
public class ReportExtensionScaffolderTests
{
    [Fact]
    public void Dataset_without_accessor_emits_the_per_row_DataEventHandler_shape()
    {
        var xml = ReportExtensionScaffolder.DatasetExtension(
            "AssetBarCodeDP", "AssetBarCodeTmp", "AssetBarCodeDPCtso_EventHandler", null).ToString();

        Assert.Contains("[DataEventHandler(tableStr(AssetBarCodeTmp), DataEventType::Inserting)]", xml);
        Assert.Contains("Common _sender, DataEventArgs _e", xml);
        // The per-row shape needs no accessor and must not pretend to have one.
        Assert.DoesNotContain("PostHandlerFor", xml);
        Assert.DoesNotContain("linkPhysicalTableInstance", xml);
    }

    [Fact]
    public void Dataset_with_accessor_emits_the_bulk_PostHandlerFor_shape_with_linked_buffer()
    {
        var xml = ReportExtensionScaffolder.DatasetExtension(
            "AssetBarCodeDP", "AssetBarCodeTmp", "AssetBarCodeDPCtso_EventHandler", "geAssetBarCodeTmp").ToString();

        Assert.Contains("[PostHandlerFor(classStr(AssetBarCodeDP), methodStr(AssetBarCodeDP, processReport))]", xml);
        Assert.Contains("XppPrePostArgs _args", xml);
        // The accessor is used verbatim — it cannot be derived (the platform ships this typo).
        Assert.Contains("dataProvider.geAssetBarCodeTmp()", xml);
        // linkPhysicalTableInstance is load-bearing: a buffer merely declared in the
        // handler is a different, empty table.
        Assert.Contains("tmpUpdate.linkPhysicalTableInstance(providerRows)", xml);
        Assert.Contains("ttsbegin", xml);
        Assert.Contains("while select forupdate tmpUpdate", xml);
    }

    [Fact]
    public void CustomDesign_controller_uses_parmArgs_never_initArgs()
    {
        var xml = ReportExtensionScaffolder.CustomDesignController(
            "CtsoSalesInvoice", "Report", "SalesInvoiceController").ToString();

        // There is no initArgs on SrsReportRunController or anywhere in its hierarchy.
        Assert.DoesNotContain("initArgs", xml);
        Assert.Contains("controller.parmArgs(_args)", xml);
        Assert.Contains("controller.parmReportName(ssrsReportStr(CtsoSalesInvoice, Report))", xml);
        Assert.Contains("controller.startOperation()", xml);
        Assert.Contains("extends SalesInvoiceController", xml);
    }

    [Fact]
    public void CustomDesign_printmgmt_handler_answers_only_its_document_type()
    {
        var xml = ReportExtensionScaffolder.CustomDesignPrintMgmtHandler(
            "CtsoSalesInvoice", "Report", "SalesOrderInvoice").ToString();

        Assert.Contains("[SubscribesTo(classStr(PrintMgmtDocType), delegateStr(PrintMgmtDocType, getDefaultReportFormatDelegate))]", xml);
        Assert.Contains("case PrintMgmtDocumentType::SalesOrderInvoice:", xml);
        Assert.Contains("_result.result(ssrsReportStr(CtsoSalesInvoice, Report))", xml);
    }

    [Fact]
    public void MenuRedirect_posts_on_static_construct_and_repoints_the_return_value()
    {
        var xml = ReportExtensionScaffolder.MenuRedirect(
            "SalesInvoiceController", "CtsoSalesInvoice", "Report", "SalesInvoiceControllerCtso_EventHandler").ToString();

        Assert.Contains("[PostHandlerFor(classStr(SalesInvoiceController), staticMethodStr(SalesInvoiceController, construct))]", xml);
        Assert.Contains("_args.getReturnValue() as SrsReportRunController", xml);
        Assert.Contains("controller.parmReportName(ssrsReportStr(CtsoSalesInvoice, Report))", xml);
    }

    // No eval case builds a report extension, so the golden CDATA gate cannot reach these
    // four shapes. They are still AxClass files written into a model, and the X++ they
    // carry is doc-comment-bearing handler code — assert the node type on each of them.
    public static TheoryData<string, XDocument> AllShapes() => new()
    {
        { "DatasetExtension", ReportExtensionScaffolder.DatasetExtension(
            "AssetBarCodeDP", "AssetBarCodeTmp", "AssetBarCodeDPCtso_EventHandler", null) },
        { "CustomDesignController", ReportExtensionScaffolder.CustomDesignController(
            "CtsoSalesInvoice", "Report", "SalesInvoiceController") },
        { "CustomDesignPrintMgmtHandler", ReportExtensionScaffolder.CustomDesignPrintMgmtHandler(
            "CtsoSalesInvoice", "Report", "SalesOrderInvoice") },
        { "MenuRedirect", ReportExtensionScaffolder.MenuRedirect(
            "SalesInvoiceController", "CtsoSalesInvoice", "Report", "SalesInvoiceControllerCtso_EventHandler") },
    };

    [Theory]
    [MemberData(nameof(AllShapes))]
    public void Every_shape_wraps_its_X_plus_plus_payload_in_CDATA(string shape, XDocument doc)
    {
        var payloads = doc.Descendants()
            .Where(e => e.Name.LocalName is "Declaration" or "Source")
            .Where(e => !string.IsNullOrWhiteSpace(e.Value))
            .ToList();

        Assert.NotEmpty(payloads);
        foreach (var el in payloads)
            Assert.IsType<XCData>(Assert.Single(el.Nodes()));
        Assert.NotNull(shape);
    }
}
