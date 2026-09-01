---
name: report-print-destinations
description: Sending an SSRS report somewhere other than the screen from X++ — SRSPrintDestinationSettings, the SRSPrintMediumType values, and setting the destination on a controller before it runs. Invoke when the user asks to email, print, archive or save a report to file from code, or when a destination set in code is ignored at run time.
applies_when: User intent involves printing or emailing a report from X++, saving a report to PDF/file, batch report output, or a print destination that has no effect.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Report print destinations

## The two objects

`SRSPrintDestinationSettings` is an `AxClass` with 68 methods. It carries the
destination and its options. You do not construct it in isolation — you take
the one already on the report contract, so the settings you change are the ones
the run will actually use.

`SRSPrintMediumType` is an `AxEnum`. Its values, read from the installation:

`Screen`, `Printer`, `File`, `Email`, `Custom`, `Archive`.

There is no `PDF` value — PDF is a **file format**, chosen with `fileFormat()`
once the medium is `File` or `Email`.

## The accessors are plain names, not parm-prefixed

This is where most attempts fail. The methods are:

| method | what it sets |
|---|---|
| `printMediumType()` | the destination — an `SRSPrintMediumType` value |
| `fileName()` | target path, for `File` |
| `fileFormat()` | PDF, Excel, Word, … for `File` and `Email` |
| `emailTo()` | recipients, for `Email` |
| `emailSubject()` | subject line |
| `printerName()` | target printer, for `Printer` |
| `numberOfCopies()` | copies, for `Printer` |

`parmPrintMediumType()` does **not** exist, nor do `toFile()`, `toScreen()`,
`toPrinter()` or `lockDestinationProperties()`. Those read like the API and are
not it — confirm with `d365fo get class SRSPrintDestinationSettings` rather
than from the shape of the name.

## Setting it before the run

The settings live on the contract's print settings, reached from the
controller. Change them **before** `startOperation()`; afterwards the run has
already resolved its destination and nothing you set has any effect — which is
the usual reason a destination set in code is "ignored".

```xpp
SrsReportRunController      controller = new SrsReportRunController();
SRSPrintDestinationSettings settings;

controller.parmReportName(ssrsReportStr(MyReport, Report));
controller.parmShowDialog(false);

settings = controller.parmReportContract().parmPrintSettings();
settings.printMediumType(SRSPrintMediumType::File);
settings.fileFormat(SRSReportFileFormat::PDF);
settings.fileName(@'\\server\share\MyReport.pdf');

controller.startOperation();
```

`parmShowDialog(false)` matters: with the dialog shown, whatever the user picks
replaces what the code set.

Two of those calls are **inherited**, which matters when you go looking for
them: `d365fo get class SrsReportRunController` lists 84 methods and
`parmShowDialog` is not among them — it is declared on `SysOperationController`,
which `SrsReportRunController` extends. `parmReportContract()` returns an
`SrsReportDataContract`, and `parmPrintSettings()` is declared there, not on the
controller. A method missing from the class you searched is not a method that
does not exist; walk the base chain before concluding the API is different.

## Print management is a different road

A business document that must obey per-customer copies, printer selection and
footer text does **not** get its destination this way. That is Print
management, and the controller extends `SrsPrintMgmtFormLetterController`
instead — see `d365fo report-pattern spec print-mgmt-form-letter`. Setting a
destination in code on such a report bypasses the configuration the customer
maintains, which is a bug that only shows up in production.

## Checks

- `d365fo get class SRSPrintDestinationSettings --output json` before writing —
  68 methods, and the names are not parm-prefixed.
- `d365fo get enum SRSPrintMediumType` — six values, and `PDF` is not one.
- `d365fo validate xpp` on the calling class; the reference gate proves both
  type names against the index.
