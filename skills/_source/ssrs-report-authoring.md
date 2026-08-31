---
id: ssrs-report-authoring
description: Author or extend an SSRS report in D365 Finance & Operations — the TmpTable → Contract → DP → Controller → AxReport stack, RDL designs, and Print management integration. Invoke when the user asks to create a report, add a dataset, change a report design, or wire a report into Print management.
covers: TmpTable to Contract to DP to Controller to AxReport, Print management
applyTo:
  - "**/AxReport/**"
  - "**/AxClass/**DP.xml"
  - "**/AxClass/**Controller.xml"
  - "**/AxClass/**Contract.xml"
  - "**/AxTable/**Tmp*.xml"
appliesWhen: User intent mentions SSRS, report, RDL, report design, data provider or DP class, SrsReportRunController, report contract, temp table for a report, or Print management.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# SSRS report authoring

> A D365FO report is five cooperating objects, not one file. Getting the stack
> right matters more than the RDL: a report whose temp table is `InMemory`
> instead of `TempDB` will design fine and return nothing at runtime.

## The five objects

| # | Object | AOT | Rule |
|---|---|---|---|
| 1 | Temp table | `AxTable` | `TableType = TempDB` — **not** `InMemory`. SSRS opens its own connection and cannot see an in-memory buffer. `TableGroup` stays a business role (`Main`). |
| 2 | Contract | `AxClass` | `[DataContractAttribute]`, one `[DataMemberAttribute]` `parm` per parameter |
| 3 | DP class | `AxClass` | `extends SrsReportDataProviderBase`, `[SrsReportParameterAttribute(classStr(MyContract))]` |
| 4 | Controller | `AxClass` | `extends SrsReportRunController` (or `SrsPrintMgmtController`, see below) |
| 5 | Report | `AxReport` | dataset bound to the DP, one or more designs |

**CLI workflow:**

```sh
# 1. Pre-flight: does a report already cover this?
d365fo search report FmVehicleService --output json

# 2. Inspect an existing report's datasets and designs before copying its shape
d365fo get report SalesInvoice --output json

# 3. Scaffold the report + its data provider (and a contract, when parameters are declared)
d365fo generate report FmVehicleServiceReport \
  --dp FmVehicleServiceDP \
  --tmp FmTmpVehicleService \
  --dataset FmVehicleServiceDS \
  --field VehicleId --field ServiceDate \
  --parameter VehicleGroupId:String \
  --install-to FleetManagement

# 4. The temp table and the controller are separate objects — scaffold them too
d365fo generate table FmTmpVehicleService --pattern main --table-type TempDB --install-to FleetManagement
d365fo generate class FmVehicleServiceController --extends SrsReportRunController --install-to FleetManagement
```

`generate report` emits the `AxReport` plus its DP class, and a `DataContract`
class when `--parameter` is passed. The temp table and the controller are not
part of that command today — scaffold them with `generate table` /
`generate class` as shown, and check `d365fo generate report --help` rather than
assuming a flag exists.

## DP class rules

- `processReport()` is the only entry point the framework calls. Fill the temp
  table there; do not compute in the getter.
- The dataset getter carries `[SRSReportDataSetAttribute(tableStr(MyTmp))]` and
  returns the temp-table buffer.
- The DP runs **on the server, without a user session** — never touch
  `element`, a form, or `curUserId()`-dependent state.
- Read parameters off the contract, never off a global.

```xpp
[SrsReportParameterAttribute(classStr(FmVehicleServiceContract))]
public class FmVehicleServiceDP extends SrsReportDataProviderBase
{
    FmTmpVehicleService tmpVehicleService;

    /// <summary>
    /// Returns the rows this report renders, populated by processReport().
    /// </summary>
    [SRSReportDataSetAttribute(tableStr(FmTmpVehicleService))]
    public FmTmpVehicleService getFmTmpVehicleService()
    {
        select tmpVehicleService;
        return tmpVehicleService;
    }

    /// <summary>
    /// Builds the report data set from the contract's parameters.
    /// </summary>
    public void processReport()
    {
        FmVehicleServiceContract contract = this.parmDataContract() as FmVehicleServiceContract;
        FmVehicle                vehicle;

        while select vehicle
            where vehicle.VehicleGroupId == contract.parmVehicleGroupId()
        {
            tmpVehicleService.clear();
            tmpVehicleService.VehicleId = vehicle.VehicleId;
            tmpVehicleService.insert();
        }
    }
}
```

## Controller rules

- Name the report with `ssrsReportStr(<Report>, <Design>)` — never a string
  literal, so a renamed design is a compile error rather than a runtime one.
- A controller's `main()` uses **`parmArgs(_args)` + `parmReportName(...)` +
  `startOperation()`** — there is **no `initArgs`** on `SrsReportRunController`
  or anywhere in its hierarchy (verified against xppc; shipped controllers all
  use the `parmArgs` shape).
- `prePromptModifyContract()` is where you seed parameters from the caller's
  record; `preRunModifyContract()` is the last chance before execution.
- The controller decides the execution mode (dialog, batch, direct print).

## Print management

Use `SrsPrintMgmtController` **instead of** `SrsReportRunController` when the
report should honour the Print management setup (destination per document type,
original vs copy).

- `SrsPrintMgmtController` declares **`runPrintMgmt()` abstract** — a subclass
  that does not implement it does not compile. There is no
  `parmPrintMgmtDocType` on the controller (verified against xppc).
- Register the document type by extending the `PrintMgmtDocType` enum.
- Override `getDocumentName()` and `getDocumentTitle()` on the controller.
- Override `getOriginalPrintMgmtPrintSettingDetail()` for the default settings.
- Register the type with `PrintMgmtDocumentType` (module, table, report) and add
  a `PrintMgmtReportFormat` entry linking the document type to the report design.
- Original vs copy is the base enum `PrintCopyOriginal`, carried on the contract
  as `parmPrintCopyOriginal()`. There is no `PrintCopyType` enum and no
  `parmPrintCopyType()`.
- Setup lives in the UI: **Accounts receivable → Setup → Print management**.

## Hard rules

- **Never read or hand-edit report XML.** `d365fo get report <Name> --output json`
  returns the datasets and designs; the RDL is embedded and easy to corrupt.
- The temp table is `TableType=TempDB`, `TableGroup=Main`. Passing
  `tableGroup=TempDB` is the single most common mistake here.
- One DP class per report. Sharing a DP across reports couples their parameter
  contracts and breaks caching.
- Labels everywhere: report captions, parameter prompts and column headers are
  label tokens, never literals (`BPErrorLabelIsText`).
- Extending a **standard** report — three techniques, every shape
  compiler-checked by the upstream sibling repo against real standard objects:
  1. **Dataset extension** (add data to the shipped design's temp table):
     subscribe after the DP with `[PostHandlerFor(classStr(<DP>),
     methodStr(<DP>, processReport))]` taking `XppPrePostArgs`, get the DP
     instance from the args, and — this is load-bearing —
     **`linkPhysicalTableInstance`** your temp-table buffer to the DP's before
     updating rows: a buffer merely declared in the handler is a *different,
     empty* table, so the handler appears to work while updating nothing. The
     DP's dataset accessor name is a fact to read off the DP, never derived
     from the temp-table name (the platform ships the typo `geAssetBarCodeTmp`).
     When no accessor exists, the per-row `[DataEventHandler(tableStr(<Tmp>),
     DataEventType::Inserting)]` shape needs none.
  2. **Custom design**: add a design to the report (or a copy), then a
     subclassed controller whose `main()` uses `parmArgs` + `parmReportName
     (ssrsReportStr(<Report>, <YourDesign>))` — for print-management reports a
     `PrintMgmtReportFormat` entry selects the design per document type instead.
  3. **Menu redirect**: repoint the shipped output menu item at your controller
     with `[PostHandlerFor(classStr(<Controller>), staticMethodStr(<Controller>,
     construct))]` when you cannot touch the menu item itself.
  Never CoC the DP's `processReport()` for extra rows — the `[PostHandlerFor]`
  subscription above is the verified route and needs no wrapper class.
