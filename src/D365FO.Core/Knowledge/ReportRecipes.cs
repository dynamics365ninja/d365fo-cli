// <copyright file="ReportRecipes.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Knowledge;

/// <summary>One object an SSRS report shape needs, and what it is for.</summary>
/// <param name="Kind">AOT type — AxTable, AxClass, AxReport, AxMenuItemOutput.</param>
/// <param name="Role">Its job in the stack: TmpTable, Contract, DP, Controller, UIBuilder, Report, MenuItem.</param>
/// <param name="Extends">Base class, where the role has one. The spelling is the AOT's own.</param>
/// <param name="Naming">The convention shipped reports follow for this role.</param>
public sealed record ReportRosterEntry(string Kind, string Role, string? Extends, string Naming);

/// <summary>An implementation recipe for one report shape.</summary>
/// <param name="Id">Stable identifier used as the command argument.</param>
/// <param name="Title">One-line name.</param>
/// <param name="WhenToUse">The decision this recipe answers.</param>
/// <param name="Roster">Every object the shape needs.</param>
/// <param name="ScaffoldCall">The one <c>generate report</c> call that produces it.</param>
/// <param name="MethodGuidance">What still has to be written by hand afterwards.</param>
/// <param name="Checks">What to run before believing it works.</param>
/// <param name="ReferenceObjects">Shipped objects of this exact shape, to read rather than guess from.</param>
public sealed record ReportRecipe(
    string Id,
    string Title,
    string WhenToUse,
    IReadOnlyList<ReportRosterEntry> Roster,
    string ScaffoldCall,
    IReadOnlyList<string> MethodGuidance,
    IReadOnlyList<string> Checks,
    IReadOnlyList<string> ReferenceObjects);

/// <summary>
/// The seven SSRS report shapes, as implementation recipes.
/// </summary>
/// <remarks>
/// <para>
/// Unlike a form pattern there is no pattern XML to validate a report against, so a recipe is
/// not a spec — it is the object roster, the base classes, the single scaffold call that produces
/// the stack, and the checks worth running afterwards. <c>generate report</c> could already build
/// every one of these; what was missing was the layer that says WHICH to build, so an agent
/// either picked by guesswork or produced a default-shaped report for a requirement that needed
/// pre-processing.
/// </para>
/// <para>
/// Every base class and naming convention here was counted in this installation's own
/// ApplicationSuite/Foundation rather than recalled: <c>SrsReportRunController</c> 152,
/// <c>SRSReportDataProviderBase</c> 95, <c>SrsReportDataProviderPreProcessTempDB</c> 88,
/// <c>SrsReportDataContractUIBuilder</c> 59, <c>SrsPrintMgmtFormLetterController</c> 6. The
/// casing is the AOT's own and is reproduced exactly — note that the DP base is <c>SRS</c> in
/// capitals while every other class in the stack is <c>Srs</c>. X++ resolves either, so this is
/// a readability and consistency matter, not a compile one, but a scaffold that emits the shipped
/// spelling produces files that diff cleanly against their neighbours.
/// </para>
/// </remarks>
public static class ReportRecipes
{
    private const string TmpNaming = "<Report>Tmp, a table with TableType=TempDB. InMemory designs fine and returns nothing at run time.";
    private const string ContractNaming = "<Report>Contract, decorated [DataContractAttribute], one [DataMemberAttribute] parm method per parameter.";
    private const string ReportNaming = "<Report> — the AxReport name carries NO DP/Contract/Controller/Tmp suffix; those belong to its companions.";

    private static ReportRosterEntry Tmp() => new("AxTable", "TmpTable", null, TmpNaming);
    private static ReportRosterEntry Contract() => new("AxClass", "Contract", null, ContractNaming);
    private static ReportRosterEntry Report() => new("AxReport", "Report", null, ReportNaming);
    private static ReportRosterEntry MenuItem() => new("AxMenuItemOutput", "MenuItem", null,
        "<Report> as an Output menu item — a report is opened through an Output item, never a Display one.");

    private static ReportRosterEntry Dp(bool preProcess) => new(
        "AxClass", "DP",
        preProcess ? "SrsReportDataProviderPreProcessTempDB" : "SRSReportDataProviderBase",
        "<Report>DP, decorated [SRSReportParameterAttribute(classStr(<Report>Contract))].");

    private static ReportRosterEntry Controller(string extends) => new(
        "AxClass", "Controller", extends,
        extends == "SrsPrintMgmtFormLetterController"
            ? "<Report>Controller — Print management resolves the design, so the controller does not name one."
            : "<Report>Controller, with a static main() that constructs it and calls startOperation().");

    private static readonly ReportRecipe[] All =
    [
        new(
            Id: "simple-list",
            Title: "Simple list",
            WhenToUse: "One flat table of rows with no grouping and no totals. The default shape, and the right "
                     + "one whenever the report is 'show me these records'.",
            Roster: [Tmp(), Contract(), Dp(preProcess: false), Controller("SrsReportRunController"), Report(), MenuItem()],
            ScaffoldCall: "d365fo generate report <Report> --tmp <Report>Tmp --field <F> [--field <F>…] --install-to <Model>",
            MethodGuidance:
            [
                "processReport() fills the temp table: select the source, set the Tmp buffer's fields, insert().",
                "The [SRSReportDataSetAttribute(tableStr(<Report>Tmp))] method returns the buffer the dataset binds to.",
            ],
            Checks:
            [
                "d365fo validate xpp on the DP — the select-inside-loop rules matter most here.",
                "d365fo build, then run the menu item: an empty report almost always means an InMemory temp table.",
            ],
            ReferenceObjects: ["BankChequeStatisticsDP", "BankCodaDetailsDP"]),

        new(
            Id: "grouped-with-totals",
            Title: "Grouped with totals",
            WhenToUse: "Rows broken into groups with a subtotal per group. Same object stack as a simple list — "
                     + "the grouping is a design concern, not a code one.",
            Roster: [Tmp(), Contract(), Dp(preProcess: false), Controller("SrsReportRunController"), Report(), MenuItem()],
            ScaffoldCall: "d365fo generate report <Report> --tmp <Report>Tmp --field <GroupKey> --field <F>… --install-to <Model>",
            MethodGuidance:
            [
                "The group key is a field on the temp table like any other; the RDL groups on it.",
                "Do NOT make the group key the temp table's unique index — the second row of a group then fails on "
                + "a duplicate key. The scaffold used to do exactly this.",
            ],
            Checks:
            [
                "Run it with data that has at least two rows in one group. A unique-index mistake passes every "
                + "single-row test there is.",
            ],
            ReferenceObjects: ["BankChequeStatisticsDP"]),

        new(
            Id: "header-detail",
            Title: "Header / detail",
            WhenToUse: "A document with a header and its lines — one dataset per level, joined by a shared key.",
            Roster:
            [
                Tmp(), new("AxTable", "TmpTable (lines)", null, "<Report>LineTmp, also TempDB, carrying the header key."),
                Contract(), Dp(preProcess: false), Controller("SrsReportRunController"), Report(), MenuItem(),
            ],
            ScaffoldCall: "d365fo generate report <Report> --tmp <Report>Tmp --field <F>… --extra-dataset <Report>LineTmp:<F>,<F> --install-to <Model>",
            MethodGuidance:
            [
                "One [SRSReportDataSetAttribute] method per temp table — the RDL binds a dataset to each.",
                "Fill both buffers in the same processReport(): the header key written into the line rows is what "
                + "the design joins on.",
            ],
            Checks: ["d365fo get report <Report> — confirm both datasets are present before designing the RDL."],
            ReferenceObjects: ["BankBillOfExchangeDP_FR"]),

        new(
            Id: "pre-process",
            Title: "Pre-processed",
            WhenToUse: "The data is expensive to compute, or the report must run in batch over a set the user "
                     + "chose earlier. The DP runs FIRST and persists its rows; the design then reads them.",
            Roster: [Tmp(), Contract(), Dp(preProcess: true), Controller("SrsReportRunController"), Report(), MenuItem()],
            ScaffoldCall: "d365fo generate report <Report> --tmp <Report>Tmp --field <F>… --pre-process --install-to <Model>",
            MethodGuidance:
            [
                "The DP extends SrsReportDataProviderPreProcessTempDB, NOT SRSReportDataProviderBase.",
                "Rows are keyed by AX_RdpPreProcessedId — the framework sets it; do not invent a key of your own.",
                "insertData() is where the rows go in, and it runs before the design is rendered rather than during.",
            ],
            Checks:
            [
                "Run it twice in the same session: rows keyed on the wrong id show up as a report that is correct "
                + "the first time and empty or duplicated the second.",
            ],
            ReferenceObjects: ["AgreementFollowUpDP", "AssetAcceleratedDepreciationDP_JP"]),

        new(
            Id: "query-based",
            Title: "Query-based",
            WhenToUse: "The rows are a plain select over existing tables with no computation. No DP class at all — "
                     + "the AOT query IS the data source, and the user gets filtering for free.",
            Roster:
            [
                new("AxQuery", "Query", null, "<Report>Query — the report's DataSources point at it."),
                Report(), MenuItem(),
                new("AxClass", "Controller (optional)", "SrsReportRunController",
                    "Only needed to pre-filter the query or pick a design at run time."),
            ],
            ScaffoldCall: "d365fo generate query <Report>Query --ds <Table> --install-to <Model>  # then bind the report to it",
            MethodGuidance:
            [
                "No temp table, no contract, no DP. Adding them because the other recipes have them is the most "
                + "common way a query-based report turns into three objects that do nothing.",
                "Ranges on the query become user-editable filters; `d365fo modify add-query-range` sets a fixed one.",
            ],
            Checks: ["d365fo get query <Report>Query --output json — confirm the datasources are what you expect."],
            ReferenceObjects: []),

        new(
            Id: "print-mgmt-form-letter",
            Title: "Print management form letter",
            WhenToUse: "A business document customers see — invoice, packing slip, confirmation — that must obey "
                     + "Print management: per-customer copies, printer destinations, footer text.",
            Roster:
            [
                Tmp(), Contract(), Dp(preProcess: false), Controller("SrsPrintMgmtFormLetterController"), Report(), MenuItem(),
                new("AxClass", "PrintMgmt wiring", null,
                    "A PrintMgmtDocType/PrintMgmtReportFormat extension linking the document type to this report's design."),
            ],
            ScaffoldCall: "d365fo generate report <Report> --tmp <Report>Tmp --field <F>… --controller-type print-mgmt --install-to <Model>",
            MethodGuidance:
            [
                "The controller extends SrsPrintMgmtFormLetterController — only 6 shipped classes do, so read one "
                + "before writing your own.",
                "Print management picks the design, so the controller must NOT hard-code one.",
                "Without the PrintMgmtReportFormat entry the report exists and is simply never selected — no error "
                + "anywhere, which is what makes this the step people miss.",
            ],
            Checks:
            [
                "Print the document from its own journal form, not from the menu item — the menu item bypasses "
                + "Print management and will look fine while the real path is unwired.",
            ],
            ReferenceObjects: ["CustDebitCreditNoteController", "CustVendAdvanceInvoiceController", "FiscalDocumentController_BR"]),

        new(
            Id: "ui-builder-dialog",
            Title: "UI-builder dialog",
            WhenToUse: "The parameter dialog needs behaviour the contract alone cannot express — a lookup, a field "
                     + "that enables another, a default computed at open time.",
            Roster:
            [
                Tmp(), Contract(), Dp(preProcess: false), Controller("SrsReportRunController"), Report(), MenuItem(),
                new("AxClass", "UIBuilder", "SrsReportDataContractUIBuilder",
                    "<Report>UIBuilder, named on the contract via [SysOperationContractProcessingAttribute]."),
            ],
            ScaffoldCall: "d365fo generate report <Report> --tmp <Report>Tmp --field <F>… --ui-builder --install-to <Model>",
            MethodGuidance:
            [
                "build() is where controls are overridden; postBuild() is where they are wired to lookups and "
                + "modified-handlers, because the controls do not exist until build() has run.",
                "The UIBuilder is reached through the CONTRACT's attribute, not the controller — a UIBuilder that "
                + "never runs is almost always one nothing points at.",
            ],
            Checks:
            [
                "Open the dialog from the menu item. A UIBuilder that is not wired shows the default dialog, which "
                + "looks like a working report rather than a missing attribute.",
            ],
            ReferenceObjects: ["AgreementFollowUpUIBuilder", "AssetBasisUIBuilder"]),
    ];

    /// <summary>Every recipe.</summary>
    public static IReadOnlyList<ReportRecipe> List() => All;

    /// <summary>One recipe by id, case-insensitively; null when there is none.</summary>
    public static ReportRecipe? Find(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The ids, for an error message that lists what is available.</summary>
    public static IReadOnlyList<string> Ids() => All.Select(r => r.Id).ToList();
}
