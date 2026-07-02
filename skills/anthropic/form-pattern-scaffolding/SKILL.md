---
name: form-pattern-scaffolding
description: Scaffold an AxForm in D365 Finance & Operations using one of the nine canonical patterns (SimpleList, SimpleListDetails, DetailsMaster, DetailsTransaction, Dialog, TableOfContents, Lookup, ListPage, Workspace), or create a Display / Action / Output menu item. Invoke whenever the user asks to "create a form", "scaffold a list page", "make a dialog", "build a workspace", "create a menu item", "add a Display menu item", or "add an Action menu item".
applies_when: User intent mentions creating an AxForm, choosing a form pattern, list pages, master records, dialogs, lookups, workspaces, details/transaction (header+lines) forms, or creating a Display / Action / Output menu item.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Authoring AxForm XML — pattern-correct

> The CLI's `d365fo generate form` mirrors `d365fo-mcp-server`'s
> `generate_object` (`objectType=form`). The nine D365FO patterns are validated against real
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

# Pattern spec — required structure, versions, when-to-use, reference forms
d365fo form-pattern spec --output json                  # list all known patterns + sub-patterns
d365fo form-pattern spec DetailsMaster --output json    # full structural spec for one pattern

# Pattern reconnaissance — what do peers use for THIS table / similar entities?
d365fo find form-patterns --table <PrimaryTable> --output json
d365fo find form-patterns --similar-to <ReferenceForm> --output json
d365fo find form-patterns --pattern SimpleList --output json   # pattern catalogue
```

The analyzer (`d365fo find form-patterns`) reads `<Design><Pattern>` from
every indexed AxForm. Use it instead of guessing — pass the most-common peer
pattern straight into `--pattern` on the next step. With no flags it returns
a histogram so you can see what shapes exist before drilling in.

When the user provides an existing form as an example, treat it as a pattern
contract, not as optional inspiration:

```sh
d365fo get form <ExampleForm> --output json
d365fo find form-patterns --similar-to <ExampleForm> --output json
```

Use the same pattern family unless the user explicitly requests a different
one. After generation, verify that the new form still contains the pattern's
required scaffolding: datasource(s), design pattern/version metadata, required
ActionPane/Body/Tab/FastTab/grid/QuickFilter controls, and required line/header
datasources for transaction forms. Missing pattern elements are a failed
generation even if the XML is well-formed.

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
- `generate form` runs a structural pattern self-test (FP001–FP010) before
  writing; structural violations (unknown pattern, missing required controls,
  wrong order, disallowed children, misapplied sub-patterns) **block the write**
  while `D365FO_FORM_PATTERN_ENFORCE=true` (the default). Don't bypass the gate —
  fix the structure (`d365fo form-pattern spec <P>` shows the required tree).
- After editing form XML by hand or via an example, validate it:
  `d365fo form-pattern validate <file> --output json` (exit 2 = structural errors).
- Never skip the primary datasource for SimpleList / Lookup / ListPage / Master / Transaction patterns.
- Never drop required pattern controls or metadata from a generated form copied
  from an example. Validate against the example's pattern before finishing.
- Never rewrite an existing AxForm or AxFormExtension XML file wholesale.
  Preserve unrelated `<Controls>`, `<DataSourceModifications>`,
  `<DataSourceReferences>`, `<DataSources>`, methods, extension properties,
  and pattern metadata exactly.
- After changing form XML, validate XML well-formedness, run
  `d365fo validate xpp --file <file> --code-type xml-any --output json`, run
  `d365fo index refresh --model <Model>`, and re-read with
  `d365fo get form <Form> --output json`.
- Never use `Dialog` or `TableOfContents` patterns for transactional grids.
- Pre-flight `search form <Name>` before scaffolding to avoid collisions.
- Caption strings must be labels (BP `BPErrorLabelIsText`) — never raw text.
- After scaffolding, run `d365fo build` only on user request.

## FormRun lifecycle & extension points

Forms follow a strict initialization order. Extension code must respect it.

**Initialization sequence:**
1. `form.init()` — form structure loaded; data sources NOT yet active.
2. `FormDataSource.init()` — each data source initializes (link types resolved).
3. `form.run()` — form becomes visible.
4. `FormDataSource.executeQuery()` — initial data load.

**Common extension points (via CoC or event handlers):**

| Method | When to use |
|---|---|
| `FormDataSource.init()` | Add ranges, modify query before first execution |
| `FormDataSource.executeQuery()` | Modify query dynamically on each refresh |
| `FormDataSource.active()` | Cursor moves to a new record — update dependent UI |
| `FormDataSource.validateWrite()` | Custom validation before save |
| `FormDataSource.write()` | Post-save logic |
| `FormControl.clicked()` / `modified()` | Button/field interaction |

**Key form interaction APIs:**
- `FormDataSource.research(retainPosition: true)` — refresh grid, keep cursor position.
- `element.args()` — access caller context (menu item, record, enum parameter).
- `FormDataSource.queryBuildDataSource()` — underlying `QueryBuildDataSource` for runtime range manipulation.
- `FormDataSource.filter(fieldNum, value)` / `removeFilter(fieldNum)` — programmatic quick-filter.
- `element.design().controlName(formControlStr(MyForm, MyControl))` — access control by name at runtime.

**Rules:**
- Use `d365fo get form <Name> --output json` to find exact control names before wrapping.
- NEVER guess control names — they differ from field names and are often prefixed.
- Cannot add new methods via CoC on `formdatasourcestr`/`formdatafieldstr`/`formControlStr` — only wrap methods that already exist.

## Menu items

Menu items are the AOT entry points that open a form, call a class action, or trigger a report. Always scaffold the menu item alongside or after the target form.

```sh
# Display menu item — opens a form (most common)
d365fo generate menu-item FmCustomersMenuItem \
  --kind Display --object FmCustomers --object-type Form \
  --label "@Fleet:Customers" \
  --install-to FleetManagement

# Action menu item — calls a class runnable (batch/service)
d365fo generate menu-item FmPostOrdersAction \
  --kind Action --object FmPostOrdersService --object-type Class \
  --label "@Fleet:PostOrders" \
  --install-to FleetManagement

# Output menu item — triggers a report
d365fo generate menu-item FmOrdersReportMenuItem \
  --kind Output --object FmOrdersReport --object-type Report \
  --label "@Fleet:OrdersReport" \
  --install-to FleetManagement
```

**Hard rules:**
- One menu item per AOT type (`AxMenuItemDisplay`, `AxMenuItemAction`, `AxMenuItemOutput`) — naming convention `<ObjectName>MenuItem` or `<ObjectName>Action`.
- Do not create an `Action` menu item pointing to a form — use `Display`.
- After creating a menu item, it must be added to a menu or a security privilege to be reachable.
- Generated menu items always include `<Image><ImageType>Symbol</ImageType></Image>` — this avoids `BPErrorMissingOrUnsupportedImage` (an omitted or `File`-typed image fails best-practice checks; `Symbol` inherits the icon from the target object and is always valid).
