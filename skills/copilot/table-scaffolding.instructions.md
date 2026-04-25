---
description: Create AxTable XML in D365 Finance & Operations using business-role pattern presets (Main / Transaction / Parameter / Group / Reference / WorksheetHeader / WorksheetLine), or add fields / indexes / relations to existing tables. Use whenever the user asks to "create a table", "scaffold a master/transaction/parameter table", "add a field", or "set TableGroup / TableType".
applyTo: '**/AxTable/**,**/*Table.xml'
---
# Creating & modifying AxTable definitions

> The CLI's `d365fo generate table` mirrors `d365fo-mcp-server`'s
> `generate_smart_table`. Pattern presets pre-populate the table with the
> canonical `TableGroup`, a sensible default field skeleton, and an
> alternate-key index — so the scaffold passes BP `BPCheckAlternateKeyAbsent`
> out of the box.

## Pre-flight (always)

```sh
d365fo search table <namePart> --output json     # name collision check
d365fo get edt <Edt>           --output json     # confirm any EDT you intend to use
d365fo search label "<text>"   --output json     # reuse label first; only create on miss
```

## Pattern-driven scaffolding (P1)

```sh
# Master table (CustTable-style)
d365fo generate table FmCustomer \
    --pattern master \
    --label "@Fleet:Customer" \
    --install-to FleetManagement

# Transaction table (CustTrans-style)
d365fo generate table FmTrans \
    --pattern transaction \
    --label "@Fleet:Transaction" \
    --install-to FleetManagement

# Parameter table (CustParameters-style; one record per company)
d365fo generate table FmParameters \
    --pattern parameter \
    --label "@Fleet:Parameters" \
    --install-to FleetManagement

# Worksheet header + lines pair (SalesTable / SalesLine-style)
d365fo generate table FmOrderHeader --pattern worksheet-header --install-to FleetManagement
d365fo generate table FmOrderLine   --pattern worksheet-line   --install-to FleetManagement

# Temp table — TempDB is a TableType, NOT a TableGroup. Combine:
d365fo generate table FmTmpStaging \
    --pattern main \
    --table-type TempDB \
    --install-to FleetManagement
```

Aliases recognised: `master` → Main, `setup`/`config` → Parameter,
`transactional` → Transaction, `lookup` → Reference, `header`/`line` for
worksheets, `misc` → Miscellaneous. Full list:
`main|transaction|parameter|group|worksheetheader|worksheetline|reference|framework|miscellaneous`.

When `--field` is supplied, **caller fields win** — pattern defaults are
skipped entirely:

```sh
d365fo generate table FmVehicle \
    --pattern master \
    --field VIN:VinEdt:mandatory \
    --field Make:Name             \
    --field Year:Yr               \
    --primary-key VIN             \
    --label "@Fleet:Vehicle"      \
    --install-to FleetManagement
```

`--primary-key <Field>` is repeatable. Falls back to "all mandatory fields",
then "first field" so `BPCheckAlternateKeyAbsent` never trips.

The CLI returns a JSON summary `{path, bytes, backup, pattern, tableType,
usedPatternDefaults, fieldCount}` — never request the full XML back.

## ❗ `TableGroup` vs `TableType`

| Property | Meaning | Allowed values |
|---|---|---|
| `TableGroup` | **Business role** | `Main`, `Transaction`, `Parameter`, `Group`, `WorksheetHeader`, `WorksheetLine`, `Reference`, `Framework`, `Miscellaneous` |
| `TableType` | **Storage** kind | `RegularTable`, `TempDB`, `InMemory` |

❌ Passing `--pattern TempDB` is **rejected** — the CLI returns
`BAD_INPUT` with a hint to use `--table-type TempDB --pattern main` instead.

## Label-on-field exception

When a field's EDT already carries a `Label`, do **NOT** pass a label on the
field — it inherits from the EDT. Override only when the table genuinely
needs a different caption.

## Pattern defaults (when no `--field` is supplied)

| Pattern | Default fields |
|---|---|
| `main`            | `AccountNum`(mandatory), `Name`, `Description` |
| `transaction`     | `AccountNum`(mandatory), `TransDate`(mandatory), `Voucher`, `Amount` |
| `parameter`       | `Key`(mandatory), `Enabled` |
| `group`           | `GroupId`(mandatory), `Description` |
| `worksheetheader` | `HeaderId`(mandatory), `DocDate`(mandatory), `AccountNum` |
| `worksheetline`   | `HeaderId`(mandatory), `LineNum`(mandatory), `Quantity`, `Amount` |
| `reference`       | `Code`(mandatory), `Description` |

Treat the defaults as a *starting point* — always replace placeholder fields
(e.g. `Key`, `Code`) with names that fit the domain.

## Hard rules

- Never guess EDTs — `d365fo get edt <Name>` first.
- Never duplicate a field name — `d365fo get table` first.
- Never pass `tableGroup="TempDB"` (or `--pattern TempDB`).
- Never override the EDT label on a field unless deliberately captioned.
- Never ship a table without an alternate-key index (BP).
- Never inline UI strings — labels only (BP `BPErrorLabelIsText`).
- After scaffolding, run `d365fo build && d365fo sync` **only on user request**.
