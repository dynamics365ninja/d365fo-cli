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

    [Fact]
    public void Dp_binds_its_contract_the_way_shipped_DPs_do()
    {
        // Compiled against xppc 7.0.7996.33: [SRSReportDataContract("Name")] fails with
        // "The name 'SRSReportDataContract' denotes a class that is not derived from the
        // 'SysAttribute' class". Every shipped DP (AssetCardDP, AgreementFollowUpDP, …)
        // spells it SRSReportParameterAttribute(classStr(<Contract>)).
        var xml = XppScaffolder.ReportDp(Spec(preProcess: true)).ToString();

        Assert.Contains("SRSReportParameterAttribute(classStr(ConDemoReportDPContract))", xml);
        Assert.DoesNotContain("SRSReportDataContract(", xml);
    }

    [Fact]
    public void Dp_without_parameters_names_no_contract_class()
    {
        // ReportContract() returns null when there are no parameters, so a contract
        // attribute here would point at a class the command never wrote.
        var xml = XppScaffolder.ReportDp(new ReportSpec("ConDemoReport")).ToString();

        Assert.DoesNotContain("SRSReportParameterAttribute", xml);
        Assert.DoesNotContain("Contract", xml);
    }

    [Fact]
    public void Dp_skeleton_comment_names_the_buffer_it_declared()
    {
        // `char | 0x20` promoted the char to int: the hint read "99onDemoReportDPTmp.insert();".
        var xml = XppScaffolder.ReportDp(Spec()).ToString();

        Assert.Contains("conDemoReportDPTmp.insert();", xml);
        Assert.DoesNotContain("99", xml);
    }

    [Fact]
    public void Contract_extends_nothing()
    {
        // "extends SrsReportDataContractBase" named a class that is in no model — xppc failed
        // the file with "The class or interface 'SrsReportDataContractBase' does not exist",
        // and every dependent cast in the DP failed after it. Shipped contracts carry the
        // DataContract attribute and no base class.
        var xml = XppScaffolder.ReportContract(Spec())!.ToString();

        Assert.Contains("public class ConDemoReportDPContract", xml);
        Assert.DoesNotContain("extends", xml);
        Assert.Contains("DataContractAttribute", xml);
    }
}
