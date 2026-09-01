---
id: rdl-design-expressions
description: Expressions inside an SSRS report's RDL design — the =Fields!/Parameters! syntax, aggregates and their scope, formatting, and why the RDL is edited in Visual Studio rather than through the AOT XML. Invoke when the user asks to show a computed value on a report, format a number or date in a design, total a column, or hide a row conditionally.
covers: RDL expression syntax, aggregate scope, formatting, where RDL lives
applyTo:
  - "**/*.rdl"
appliesWhen: User intent involves a report design expression, a total or subtotal, conditional visibility or formatting on a report, or editing RDL.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# RDL design expressions

## Where the RDL lives, and why you do not hand-edit it here

An `AxReport` carries its RDL **inside the AOT XML, in CDATA**. That means:

- `d365fo get report <Name>` returns the datasets and designs, which is what
  you need to reason about the report;
- the RDL itself is a payload inside a payload, and editing it as text is how a
  design gets corrupted with nothing to show for it;
- the RDL also carries `rd:DataSourceID`, `rd:DataSetID` and `rd:ReportID`
  GUIDs that are minted fresh on every generation, so two RDLs that render
  identically are not byte-identical.

The design is edited in **Visual Studio's report designer**. What this topic
gives you is the expression language, so the expression you put in a textbox is
right the first time.

## The syntax

Every expression starts with `=` and is Visual Basic, not X++:

| want | expression |
|---|---|
| a dataset field | `=Fields!AccountNum.Value` |
| a report parameter | `=Parameters!FromDate.Value` |
| concatenation | `=Fields!Name.Value & " (" & Fields!AccountNum.Value & ")"` |
| a null-safe field | `=IIf(IsNothing(Fields!Note.Value), "", Fields!Note.Value)` |
| the current page | `=Globals!PageNumber` |
| execution time | `=Globals!ExecutionTime` |

`&` concatenates; `+` on a string and a null yields null, which renders as an
empty cell and looks like missing data rather than a bug.

`IIf` evaluates **both** branches regardless of the condition, so
`=IIf(Fields!Qty.Value = 0, 0, Fields!Amount.Value / Fields!Qty.Value)` still
divides by zero. Guard the operand, not the result:
`=Fields!Amount.Value / IIf(Fields!Qty.Value = 0, 1, Fields!Qty.Value)`.

## Aggregates and scope

The second argument of an aggregate is the **scope** — a dataset name, or the
name of a group. Omitting it aggregates over the innermost containing scope,
which is why a total that looks right in a group footer is wrong in the page
footer.

```
=Sum(Fields!Amount.Value)                    ' innermost scope
=Sum(Fields!Amount.Value, "CustomerGroup")   ' one group
=Sum(Fields!Amount.Value, "MyReportDS")      ' the whole dataset
```

`RunningValue(Fields!Amount.Value, Sum, "CustomerGroup")` gives a running
total within a scope. `CountRows("MyReportDS")` counts rows rather than
non-null values, which `Count()` does not.

## Formatting

Format in the textbox's **Format property**, not by converting to a string in
the expression: a formatted string sorts and exports as text, so an Excel
export of `=FormatNumber(...)` is a column of text nobody can sum.

Standard .NET format strings apply — `#,##0.00`, `dd/MM/yyyy`, `P2`. For
currency, prefer the report's own culture handling to a hard-coded symbol.

## Conditional visibility and colour

Visibility is expressed as **Hidden**, so the logic is inverted from how it is
usually described:

```
=IIf(Fields!Amount.Value = 0, True, False)   ' Hidden when zero
```

Colour takes an expression the same way:
`=IIf(Fields!Amount.Value < 0, "Red", "Black")`.

## Checks

- `d365fo get report <Name> --output json` to confirm the dataset and field
  names an expression references — `Fields!Wrong.Value` renders as `#Error`
  rather than failing at build.
- Preview with data in more than one group before believing an aggregate: a
  missing scope argument is correct in every single-group test.
