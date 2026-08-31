---
id: barcode-scanning
description: Barcodes and scanner input in D365FO — printing vs scanning are unrelated code paths, GS1-128 application identifiers, item-barcode resolution, and where the platform parses a scan for you. Invoke when the user mentions barcodes, GS1, GTIN, SSCC, scanning, scanners, item barcodes, or barcode setup.
covers: barcode printing vs scanning, GS1-128 parsing, item-barcode resolution
applyTo:
  - "**/*.xpp"
  - "**/AxClass/**"
appliesWhen: User intent mentions barcode, bar code, GS1, GTIN, SSCC, EAN, UPC, scanning, scanner, keyboard wedge, application identifier, or item barcode.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Barcodes & scanner input

Barcodes are **two unrelated problems** in D365FO, and mixing them is the usual
defect. **Printing** goes through the Barcode class hierarchy, which encodes a
value into the font string an SSRS report renders — it decodes nothing.
**Scanning** delivers already-decoded text. That text is rarely a bare item
number: a GS1-128 label packs GTIN, batch, serial and expiry into one string
with application identifiers, so code that assigns the scan straight to an
`ItemId` works on the test label and fails on the first real one.

## 1. Printing

- The Barcode hierarchy (construct by barcode type, then encode) returns a
  **font-encoded** string with start/stop characters and the check digit added.
  Rendering it in a normal font produces a label no scanner reads — the
  matching barcode font must be installed on the report server.
- Never run scanned input back through the encoder to "normalize" it — the two
  directions share no code.

## 2. Resolving a scan

- Barcode setup says which symbology a code uses; the item-barcode table maps a
  code to **item + unit + quantity + inventory dimensions**, flagged separately
  for input and for printing. Resolve a scan through that table — a string
  compare against `ItemId` is wrong, because one item legitimately carries many
  codes (per unit, per pack size, an old vendor code).
- A barcode string is not a key: the same value can resolve under more than one
  setup, and a print-only code must not resolve on input. Filter on the
  use-for-input flag and treat "more than one match" as a real branch.
- A **GTIN is not an item number**: it identifies item + unit and often a pack
  quantity, so one scan of a case can mean 12 EA. Take the unit and quantity
  from the barcode record and convert through the unit-of-measure setup — never
  post the raw scanned quantity.
- Batch and serial numbers read off a GS1 label are applied as inventory
  dimensions through the dimension find-or-create API. Writing them onto a line
  directly leaves an orphan dimension and on-hand that does not add up.
- An **unresolved scan is a normal business case** (unknown code, wrong
  warehouse, blocked batch), not an exception path. Report it with a label and
  let the operator rescan — an unhandled throw inside a transaction on a device
  step kills the session and rolls back work the operator already did.

## 3. Inside the warehouse app: DO NOT write a GS1 parser

The platform parses GS1 **before the scan reaches the flow** and fills the
screen controls:

- Global options live on Warehouse management parameters: the prefix characters
  that mark a scan as GS1, the printable stand-in for the ASCII 29 group
  separator, and the unknown-application-identifier policy (**Error refuses the
  whole scan** for one unmapped element).
- The application-identifier list is setup data, and a bar-code data policy on
  the mobile device menu item is what makes **one scan fill several fields**.
- The scanner **hardware** is part of the configuration: it must add a
  recognised AIM prefix (`]C1` GS1-128, `]e0` GS1 DataBar, `]d2` GS1
  DataMatrix, `]Q3` GS1 QR) and convert the non-printable group separator to
  the character named in the parameters. A scan behaving as plain text usually
  means the scanner, not the code.
- Multiple-field scanning changes **when** a flow has its values — a custom
  step you assumed would run can be skipped because the scan already filled it.
  Test with the policy on AND off.

## 4. Outside the app: parse AI by AI

A rich-client form or an integration has no menu item to hang a policy on, so
that path parses in code. GS1-128 carries application identifiers — (00) SSCC,
(01) GTIN, (10) batch/lot, (17) expiry as YYMMDD, (21) serial, (30)/(37) count.
Parse **identifier by identifier**: a fixed-length AI runs straight into the
next one; a variable-length AI ends at the group separator or at end of scan.
**Slicing at fixed offsets is the classic defect.**

- Keyboard-wedge scanners TYPE the value and finish with Enter/Tab: the whole
  string arrives in one `modified()` call. Put the resolution there (or in the
  lookup) and make it idempotent — a double trigger must not book the quantity
  twice.
- Scanned strings carry invisible payload: significant leading zeros, a
  trailing CR/LF, the FNC1 separator, a check digit. Strip control characters
  explicitly and keep the value in a **string** type — storing a code in an int
  silently drops leading zeros and changes the code.

## Hard rules

- Printing and scanning share no code; never mix them.
- Resolve via the item-barcode setup, filtered to input codes — never a string
  compare against `ItemId`.
- Inside a warehouse-app flow, the platform parses GS1 — configure, don't code.
- Outside it, parse AI by AI with the group separator — never fixed offsets.
- Unit and quantity come from the barcode record; batch/serial go through the
  dimension API; unresolved scans are business cases, not throws.
- Barcode tables and setup differ by version — confirm names with
  `d365fo search any <name>` / `d365fo get table <name>` before writing
  against them.
