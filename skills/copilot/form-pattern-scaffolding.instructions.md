---
description: Scaffold an AxForm in D365 Finance & Operations using one of the nine canonical patterns (SimpleList, SimpleListDetails, DetailsMaster, DetailsTransaction, Dialog, TableOfContents, Lookup, ListPage, Workspace). Invoke whenever the user asks to "create a form", "scaffold a list page", "make a dialog", or "build a workspace".
applyTo: '**/AxForm/**,**/*Form.xml'
---
# Authoring AxForm XML — pattern-correct

> The CLI's `d365fo generate form` mirrors `d365fo-mcp-server`'s
> `generate_smart_form`. The nine D365FO patterns are validated against real
> AOT forms (`CustGroup`, `PaymTerm`, `CustTable`, `SalesTable`, …). Hand-rolled
> XML loses ActionPane, QuickFilter, FastTabs, the right `PatternVersion`,
> and the design-time hooks Visual Studio expects — **never hand-roll**.

## ⛔ Anti-pattern: escalating workarounds

```
WRONG SPIRAL (each step is more wrong):
 1. "I'll write the AxForm XML by hand"
 2. "It's only 3 elements, I'll skip ActionPane / QuickFilter"
 3. "PatternVersion 1.0 is fine instead of 1.1"
 4. "I'll add SimpleList without grid columns"

CORRECT — always:
 d365fo generate form <Name> --pattern <P> --table <T> --field <F1> --field <F2> --install-to <Model>
```

## Pre-flight

```sh
d365fo search form <Name> --output json          # collision check
d365fo get table <PrimaryTable> --output json    # field list for the grid

# Pattern reconnaissance — what do peers use for THIS table / similar entities?
d365fo find form-patterns --table <PrimaryTable> --output json
d365fo find form-patterns --similar-to <ReferenceForm> --output json
d365fo find form-patterns --pattern SimpleList --output json   # pattern catalogue
```

The analyzer (`d365fo find form-patterns`) reads `<Design><Pattern>` from
every indexed AxForm. Use it instead of guessing — pass the most-common peer
pattern straight into `--pattern` on the next step. With no flags it returns
a histogram so you can see what shapes exist before drilling in.

## Pattern catalog

| Pattern | When to use | Required |
|---|---|---|
| `SimpleList`         | Setup / config list (read-mostly grid) | `--table` |
| `SimpleListDetails`  | List + detail panel on the right | `--table`, `--section Name:Caption` |
| `DetailsMaster`      | Full master record (CustTable shape) | `--table`, FastTabs via `--section` |
| `DetailsTransaction` | Header + lines (SalesTable / SalesLine) | `--table`, `--lines-table` |
| `Dialog`             | Popup parameter dialog | (datasource optional) |
| `TableOfContents`    | Tabbed settings page (parameters form) | `--section` per tab |
| `Lookup`             | Dropdown lookup form | `--table` |
| `ListPage`           | Top-level navigation list page | `--table` |
| `Workspace`          | Operational workspace with KPI tiles + panorama sections | `--section` per panorama section |

Aliases recognised: `master`, `transaction`, `toc`, `panorama`,
`drop-dialog`, `dropdialog`, `simplelist-details`, etc.

## Scaffolding examples

```sh
# Master form for a vehicle table
d365fo generate form FmVehicle \
    --pattern master \
    --table FmVehicle \
    --field VIN --field Make --field Year \
    --section General:"@SYS:General" \
    --section Notes:"@SYS:Notes" \
    --install-to FleetManagement

# Order header + lines (DetailsTransaction)
d365fo generate form FmOrder \
    --pattern transaction \
    --table FmOrderHeader \
    --lines-table FmOrderLine \
    --field OrderId --field CustAccount --field DeliveryDate \
    --install-to FleetManagement

# Dialog (no primary datasource needed)
d365fo generate form FmRunImport --pattern dialog --install-to FleetManagement

# Workspace with two panorama sections
d365fo generate form FmFleetWorkspace \
    --pattern workspace \
    --section Recent:"@Fleet:Recent" \
    --section Pending:"@Fleet:Pending" \
    --install-to FleetManagement
```

`--field <F>` is repeatable — these become grid / detail columns. The
section template is `--section Name:Caption` (split on the first `:`).

## Hard rules

- Never hand-roll AxForm XML — always use `--pattern`.
- Never skip the primary datasource for SimpleList / Lookup / ListPage / Master / Transaction patterns.
- Never use `Dialog` or `TableOfContents` patterns for transactional grids.
- Pre-flight `search form <Name>` before scaffolding to avoid collisions.
- Caption strings must be labels (BP `BPErrorLabelIsText`) — never raw text.
- After scaffolding, run `d365fo build` only on user request.
