---
name: table-scaffolding
description: Create AxTable XML in D365 Finance & Operations using business-role pattern presets (Main / Transaction / Parameter / Group / Reference / WorksheetHeader / WorksheetLine), or add fields / indexes / relations to existing tables. Use whenever the user asks to "create a table", "scaffold a master/transaction/parameter table", "add a field", or "set TableGroup / TableType".
applies_when: User intent mentions creating a table, choosing TableGroup / TableType, adding fields / indexes / relations, or temporary (TempDB / InMemory) tables.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Creating & modifying AxTable definitions

> The CLI's `d365fo generate table` mirrors `d365fo-mcp-server`'s
> `generate_object` (`objectType=table`). Pattern presets pre-populate the table with the
> canonical `TableGroup`, a sensible default field skeleton, and an
> alternate-key index — so the scaffold passes BP `BPCheckAlternateKeyAbsent`
> out of the box.

## Pre-flight (always — ONE call)

```sh
d365fo prepare create <Name> --type table --field <F1> --field <F2> --goal "<why>" --output json
```

This single call returns the name-collision check, naming validation, similar
existing tables to copy patterns from, EDT suggestions per planned field,
reusable labels, mined property defaults (what % of STANDARD tables set
Label/TableGroup/ClusteredIndex and the most common TableGroup values), and a
**grounding token** — pass it to `d365fo generate table … --grounding-token <token>`.

Fallback (prepare unavailable):

```sh
d365fo search table <namePart> --output json     # name collision check
d365fo get edt <Edt>           --output json     # confirm any EDT you intend to use
d365fo labels search "<text>"   --output json     # reuse label first; only create on miss
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
| `TableType` | **Storage** kind | `--table-type` accepts `RegularTable`, `TempDB`, `InMemory` (the AOT `<TableType>` element itself is emitted as `Regular`, `TempDB`, or `InMemory` — `RegularTable` is the CLI's flag name, not the literal XML value, and is omitted from the XML entirely when it's the default) |

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

## Related AOT objects

### AOT Query for joining related tables

After scaffolding a table, you often need an AOT Query to drive forms, reports, or data entities:

```sh
# Inner join — SalesTable driving, SalesLine joined
d365fo generate query SalesTableWithLines \
  --ds SalesTable --join "SalesLine:InnerJoin:SalesTable" \
  --out c:/AOT/MyModel/AxQuery/SalesTableWithLines.xml

# Check for existing queries on the same table first
d365fo search query <NamePart> --output json
```

`--join target:joinKind:parentDs` (repeatable). JoinKind: `InnerJoin`, `OuterJoin`, `ExistsJoin`, `NoExistsJoin` (no "t" — this is the real `AxQuerySimpleEmbeddedDataSource.JoinMode` spelling, confirmed against shipped platform `AxQuery` XML; `NotExistsJoin` is rejected). The join is emitted as `<UseRelations>Yes</UseRelations>`, which tells the platform to auto-derive the join key from the target table's existing AOT relation — this only works when such a relation actually exists between the two tables.

### Number sequence integration

If the table needs an auto-generated document number:

```sh
# Generate the module extension class, the EDT, and the form handler
d365fo generate number-sequence <ModuleName> \
  --edt <NumEdtName> --scope Company --table <YourTable> \
  --out         c:/AOT/MyModel/AxClass/NumberSeqModuleExtension_<ModuleName>.xml \
  --out-edt     c:/AOT/MyModel/AxEdt/<NumEdtName>.xml \
  --out-handler c:/AOT/MyModel/AxClass/<YourTable>_NumberSeqFormHandler.xml
```

This emits:
- A CoC extension of the per-module `NumberSeqApplicationModule_<ModuleName>` class (e.g. `NumberSeqApplicationModule_FleetManagement`) that registers the new sequence.
- An EDT with `NumberSequence=Yes` and `NumberSequenceModule` set.
- A `NumberSeqFormHandler` extension class for the target form's `init()`.

Manual consumption in X++ — the `numRef<EdtName>()` accessor lives as a `static
NumberSequenceReference` method on the module's **own parameter table** (e.g.
`FmParameters`, mirroring how `CustParameters::numRefCustAccount()` returns
`NumberSeqReference::findReference(extendedTypeNum(CustAccount))` in the real
platform) — **not** on `CompanyInfo` (that class does exist and is the
standard way to read the current legal entity via `CompanyInfo::find()`,
but it isn't where a module's own number-sequence accessor belongs):
```xpp
NumberSeq numSeq = NumberSeq::newGetNum(FmParameters::numRefMySequence());
str nextNum = numSeq.num();
numSeq.used();   // or numSeq.abort() to roll back
```

## Hard rules

- Never guess EDTs — `d365fo get edt <Name>` first.
- Never duplicate a field name — `d365fo get table` first.
- Never pass `tableGroup="TempDB"` (or `--pattern TempDB`).
- Never override the EDT label on a field unless deliberately captioned.
- Never ship a table without an alternate-key index (BP).
- Never inline UI strings — labels only (BP `BPErrorLabelIsText`).
- After scaffolding, run `d365fo build && d365fo sync` **only on user request**.
