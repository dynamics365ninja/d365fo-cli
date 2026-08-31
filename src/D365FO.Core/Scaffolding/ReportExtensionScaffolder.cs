// <copyright file="ReportExtensionScaffolder.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Scaffolds the three compiler-checked techniques for extending an SSRS report that already
/// ships (port of the upstream MCP server's <c>report-dataset-extension</c>,
/// <c>report-custom-design</c> and <c>report-menu-redirect</c> patterns — every emitted shape
/// was compiled upstream against real standard objects, with a negative control in the same
/// build).
///
/// Three details the compiler settled and these scaffolds carry:
/// <list type="bullet">
/// <item>the dataset accessor is a <b>parameter</b>, never derived from the temp-table name:
/// the platform's own AssetBarCodeDP spells its getter <c>geAssetBarCodeTmp</c>, a shipped
/// typo. Give it and you get the bulk <c>[PostHandlerFor]</c> shape; omit it and you get the
/// per-row <c>[DataEventHandler]</c>, which needs no accessor at all;</item>
/// <item><c>linkPhysicalTableInstance</c> is load-bearing in the bulk shape — a temp-table
/// buffer merely declared in the handler is a <i>different, empty</i> table, so the handler
/// would appear to work while updating nothing;</item>
/// <item>a controller's <c>main()</c> uses <c>parmArgs</c> + <c>parmReportName</c> +
/// <c>startOperation</c> — there is no <c>initArgs</c> anywhere in the
/// SrsReportRunController hierarchy.</item>
/// </list>
/// </summary>
public static class ReportExtensionScaffolder
{
    /// <summary>
    /// Add columns to a STANDARD report's dataset, without touching the RDP class, its temp
    /// table or the report. Two shapes: a BULK pass over the finished temp table when the
    /// caller can name the provider's dataset accessor, and a per-ROW handler when it cannot.
    /// </summary>
    public static XDocument DatasetExtension(string dpClass, string tmpTable, string className, string? datasetAccessor)
    {
        if (string.IsNullOrWhiteSpace(datasetAccessor))
        {
            var rowSrc =
                "/// <summary>\n" +
                $"/// Runs for each row the provider inserts into {tmpTable}, before it reaches the database.\n" +
                "/// </summary>\n" +
                "/// <remarks>\n" +
                $"/// This shape needs no accessor on {dpClass}, which makes it the safe default.\n" +
                "/// For a lookup that could be done ONCE for the whole set, pass --dataset-accessor\n" +
                "/// and get the bulk post-handler instead.\n" +
                "/// </remarks>\n" +
                $"[DataEventHandler(tableStr({tmpTable}), DataEventType::Inserting)]\n" +
                $"public static void {tmpTable}_onInserting(Common _sender, DataEventArgs _e)\n" +
                "{\n" +
                $"    {tmpTable} row = _sender as {tmpTable};\n" +
                "\n" +
                "    if (!row)\n" +
                "    {\n" +
                "        return;\n" +
                "    }\n" +
                "\n" +
                $"    // TODO: set the field(s) your table extension added to {tmpTable}.\n" +
                "    // Everything the standard provider computed is already on the buffer.\n" +
                "}\n";

            return ClassDoc(className,
                $"/// <summary>\n/// Fills the column(s) this model added to <c>{tmpTable}</c>, one row at a time.\n/// </summary>\n" +
                $"public final class {className}\n{{\n}}\n",
                [($"{tmpTable}_onInserting", rowSrc)]);
        }

        var bulkSrc =
            "/// <summary>\n" +
            $"/// Runs after {dpClass} has populated its dataset.\n" +
            "/// </summary>\n" +
            "/// <remarks>\n" +
            "/// The parameter type is fixed: anything but XppPrePostArgs is a COMPILE error\n" +
            "/// (\"cannot be used as an event handler ... because the parameter profile does\n" +
            "/// not match\"). getThis() is typed Object, so the provider is downcast before\n" +
            "/// use, and the temp table instance is SHARED through linkPhysicalTableInstance —\n" +
            "/// a buffer merely declared here would be a different, empty table, and this\n" +
            "/// handler would appear to work while updating nothing.\n" +
            "/// </remarks>\n" +
            $"[PostHandlerFor(classStr({dpClass}), methodStr({dpClass}, processReport))]\n" +
            $"public static void {dpClass}_Post_processReport(XppPrePostArgs _args)\n" +
            "{\n" +
            $"    {dpClass} dataProvider = _args.getThis() as {dpClass};\n" +
            $"    {tmpTable} providerRows;\n" +
            $"    {tmpTable} tmpUpdate;\n" +
            "\n" +
            "    if (!dataProvider)\n" +
            "    {\n" +
            "        return;\n" +
            "    }\n" +
            "\n" +
            $"    providerRows = dataProvider.{datasetAccessor}();\n" +
            "    tmpUpdate.linkPhysicalTableInstance(providerRows);\n" +
            "\n" +
            "    ttsbegin;\n" +
            "\n" +
            "    while select forupdate tmpUpdate\n" +
            "    {\n" +
            $"        // TODO: set the field(s) your table extension added to {tmpTable}.\n" +
            "        tmpUpdate.update();\n" +
            "    }\n" +
            "\n" +
            "    ttscommit;\n" +
            "}\n";

        return ClassDoc(className,
            $"/// <summary>\n/// Fills the column(s) this model added to <c>{tmpTable}</c>, after <c>{dpClass}</c>\n/// has finished building its rows.\n/// </summary>\n" +
            $"public final class {className}\n{{\n}}\n",
            [($"{dpClass}_Post_processReport", bulkSrc)]);
    }

    /// <summary>
    /// The controller half of the custom-design recipe: runs this model's own copy of the
    /// standard report (which keeps consuming the STANDARD contract and data provider — that
    /// is the point of duplicating the design rather than the whole solution).
    /// </summary>
    public static XDocument CustomDesignController(string customReport, string designName, string baseController)
    {
        var className = $"{customReport}Controller";
        var mainSrc =
            "/// <summary>\n" +
            "/// Entry point for the menu item.\n" +
            "/// </summary>\n" +
            "/// <param name = \"_args\">The arguments the menu item was started with.</param>\n" +
            "public static void main(Args _args)\n" +
            "{\n" +
            $"    {className} controller = new {className}();\n" +
            "\n" +
            "    controller.parmArgs(_args);\n" +
            $"    controller.parmReportName(ssrsReportStr({customReport}, {designName}));\n" +
            "    controller.startOperation();\n" +
            "}\n";

        return ClassDoc(className,
            "/// <summary>\n" +
            $"/// Runs this model's own design of the standard report.\n" +
            "/// </summary>\n" +
            "/// <remarks>\n" +
            $"/// Duplicate the standard report into this model as {customReport} FIRST. The second\n" +
            $"/// argument of ssrsReportStr is the DESIGN name inside that report — read it off the\n" +
            "/// AxReport rather than assuming one; it is compile-time checked, so a wrong name\n" +
            "/// fails the build.\n" +
            "/// </remarks>\n" +
            $"public class {className} extends {baseController}\n{{\n}}\n",
            [("main", mainSrc)]);
    }

    /// <summary>
    /// The print-management half of the custom-design recipe: maps the document type to this
    /// model's design. PrintMgmtDocType exposes seven delegates, all with the same
    /// (PrintMgmtDocumentType, EventHandlerResult) shape — answer ONLY the document types you
    /// are replacing and leave the rest to the platform.
    /// </summary>
    public static XDocument CustomDesignPrintMgmtHandler(string customReport, string designName, string documentType)
    {
        var className = $"{customReport}PrintMgmtHandler";
        var src =
            "/// <summary>\n" +
            "/// Supplies the report format for the document type this model overrides.\n" +
            "/// </summary>\n" +
            "/// <param name = \"_docType\">The document type being resolved.</param>\n" +
            "/// <param name = \"_result\">Carries the answer back to the framework.</param>\n" +
            "[SubscribesTo(classStr(PrintMgmtDocType), delegateStr(PrintMgmtDocType, getDefaultReportFormatDelegate))]\n" +
            "public static void getDefaultReportFormatDelegate(\n" +
            "    PrintMgmtDocumentType _docType,\n" +
            "    EventHandlerResult    _result)\n" +
            "{\n" +
            "    switch (_docType)\n" +
            "    {\n" +
            $"        case PrintMgmtDocumentType::{documentType}:\n" +
            $"            _result.result(ssrsReportStr({customReport}, {designName}));\n" +
            "            break;\n" +
            "    }\n" +
            "}\n";

        return ClassDoc(className,
            $"/// <summary>\n/// Points PrintMgmtDocumentType::{documentType} at this model's design.\n/// </summary>\n" +
            $"public final class {className}\n{{\n}}\n",
            [("getDefaultReportFormatDelegate", src)]);
    }

    /// <summary>
    /// Redirect an EXISTING report run at your own design without editing the menu item or
    /// hunting down callers: a post-handler on the controller's static <c>construct()</c>.
    /// Only works when the controller HAS a static construct() — many do not
    /// (AssetBarCodeController does not; SalesInvoiceController does); the intrinsic fails the
    /// build otherwise.
    /// </summary>
    public static XDocument MenuRedirect(string controllerClass, string customReport, string designName, string className)
    {
        var src =
            "/// <summary>\n" +
            "/// Repoints the freshly constructed controller at this model's design.\n" +
            "/// </summary>\n" +
            "/// <param name = \"_args\">The call this handler is wrapped around.</param>\n" +
            $"[PostHandlerFor(classStr({controllerClass}), staticMethodStr({controllerClass}, construct))]\n" +
            $"public static void {controllerClass}_Post_construct(XppPrePostArgs _args)\n" +
            "{\n" +
            "    SrsReportRunController controller = _args.getReturnValue() as SrsReportRunController;\n" +
            "\n" +
            "    if (controller)\n" +
            "    {\n" +
            $"        controller.parmReportName(ssrsReportStr({customReport}, {designName}));\n" +
            "    }\n" +
            "}\n";

        return ClassDoc(className,
            $"/// <summary>\n/// Sends <c>{controllerClass}</c> to this model's report design.\n/// </summary>\n" +
            $"public final class {className}\n{{\n}}\n",
            [($"{controllerClass}_Post_construct", src)]);
    }

    private static XDocument ClassDoc(string className, string declaration, IReadOnlyList<(string Name, string Source)> methods)
        => new(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        methods.Select(m => new XElement("Method",
                            new XElement("Name", m.Name),
                            new XElement("Source", m.Source)))))));
}
