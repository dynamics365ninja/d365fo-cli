using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The report-scaffold depth options ported from upstream generateSmartReport (1.15.0):
/// --pre-process, --controller-type print-mgmt, --ui-builder. Each pins a fact the compiler
/// settled upstream.
/// </summary>
public class ReportScaffoldDepthTests
{
    private static ReportSpec Spec(bool preProcess = false, bool printMgmt = false, bool uiBuilder = false)
        => new("ConDemoReport", Parameters: [new ReportParameterSpec("FromDate", "DateTime")])
        {
            PreProcess = preProcess,
            PrintMgmtController = printMgmt,
            UiBuilder = uiBuilder,
        };

    [Fact]
    public void PreProcess_pairs_the_TempDB_table_with_the_TempDB_base()
    {
        // PreProcessTempDB, not PreProcess: the scaffolded tmp table is TempDB and that is
        // the base shipped code pairs it with (332 of 370 pre-processed DPs upstream).
        var xml = XppScaffolder.ReportDp(Spec(preProcess: true)).ToString();
        Assert.Contains("extends SrsReportDataProviderPreProcessTempDB", xml);

        var plain = XppScaffolder.ReportDp(Spec()).ToString();
        Assert.Contains("extends SrsReportDataProviderBase", plain);
    }

    [Fact]
    public void PrintMgmt_controller_implements_the_abstract_half_and_invents_no_parm()
    {
        var xml = XppScaffolder.ReportController(Spec(printMgmt: true)).ToString();

        Assert.Contains("extends SrsPrintMgmtController", xml);
        // runPrintMgmt() is abstract — a subclass without it does not compile.
        Assert.Contains("protected void runPrintMgmt()", xml);
        Assert.Contains("protected void initPrintMgmtReportRun()", xml);
        Assert.Contains("PrintMgmtReportRun::construct(", xml);
        // xppc: "does not contain a definition for method 'parmPrintMgmtDocType'" —
        // the earlier upstream scaffold invented it; ours must not.
        Assert.DoesNotContain("parmPrintMgmtDocType", xml);
        Assert.DoesNotContain("initArgs", xml);
    }

    [Fact]
    public void Simple_controller_shape_is_unchanged()
    {
        // The default emission is pinned by VM-verified goldens (L1-report-basic) —
        // the new options must not disturb it.
        var xml = XppScaffolder.ReportController(Spec()).ToString();
        Assert.Contains("extends SrsReportRunController", xml);
        Assert.DoesNotContain("runPrintMgmt", xml);
        Assert.Contains("controller.parmReportName(ssrsReportStr(ConDemoReport, AutoDesign));", xml);
    }

    [Fact]
    public void UiBuilder_is_bound_on_the_contract_via_SysOperationContractProcessing()
    {
        var spec = Spec(uiBuilder: true);
        var builder = XppScaffolder.ReportUiBuilder(spec)!.ToString();
        Assert.Contains("public class ConDemoReportUIBuilder extends SrsReportDataContractUIBuilder", builder);
        Assert.Contains("public void build()", builder);
        Assert.Contains("methodStr(ConDemoReportDPContract, parmFromDate)", builder);

        // The framework instantiates the builder from the contract attribute; nothing
        // else wires it — a builder class without the binding compiles and never runs.
        var contract = XppScaffolder.ReportContract(spec)!.ToString();
        Assert.Contains("SysOperationContractProcessing(classStr(ConDemoReportUIBuilder))", contract);

        // Without the flag: no builder, no binding.
        Assert.Null(XppScaffolder.ReportUiBuilder(Spec()));
        Assert.DoesNotContain("SysOperationContractProcessing", XppScaffolder.ReportContract(Spec())!.ToString());
    }
}
