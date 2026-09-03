# Examples

One worked example per command. Setup lives in [SETUP.md](SETUP.md). Every example assumes `d365fo` is on `PATH` and the index is populated.

Output: `--output json` returns `{ "ok": true, "data": {…} }` or `{ "ok": false, "error": { "code": "…", "message": "…", "hint": "…" } }` on every command.

---

## Discover

### `search` — fuzzy-find AOT objects

```sh
d365fo search class Cust
```

Same pattern for `search table|edt|enum|form|query|view|entity|report|service|workflow|label`.

### `get` — full metadata for one object

```sh
d365fo get table CustTable
d365fo get table CustTable --resolve-labels    # rewrite @File:Id tokens to text
```

Supports `get class|edt|enum|form|menu-item|security|label|role|duty|privilege|query|view|entity|report|service|service-group`. Unknown names return a `*_NOT_FOUND` envelope with a `hint: "Did you mean: …"`.

### `find` — trace cross-references

```sh
d365fo find coc CustTable::validateWrite
```

Also available: `find relations|usages|extensions|handlers`. `find refs --xref` queries `DYNAMICSXREFDB` for path/line/column/kind.

`find refs <needle>` searches inside X++ method bodies. By default it scans the source on disk on demand; if you extracted with `--index-source` it answers from the `MethodSourceFts` full-text index instead (much faster, and the result carries `via: "fts"`):

```sh
d365fo index extract --index-source     # one-time: full-text index method bodies
d365fo find refs CustPostInvoice        # now served from FTS
```

#### `find form-patterns`

```sh
d365fo find form-patterns --output json               # full histogram
d365fo find form-patterns --pattern SimpleList         # all SimpleList forms
d365fo find form-patterns --table CustTable            # forms using CustTable
d365fo find form-patterns --similar-to CustGroup       # same pattern + datasource
```

Use before `generate form` to confirm the right pattern.

#### `get form-pattern` — pattern spec catalog

```sh
d365fo form-pattern spec --output json                  # list all patterns + sub-patterns
d365fo form-pattern spec DetailsMaster --output json    # full structural spec
d365fo form-pattern spec FieldsFieldGroups --output json  # container sub-pattern spec
```

Returns the machine-readable spec the form-pattern validator enforces: required
structure tree (control types, occurrence, order), known `PatternVersion`s,
when-to-use criteria, Microsoft reference forms, and lifecycle guidance.
Pure catalog data — needs no index. Free-text aliases (`master`, `toc`,
`transaction`, …) resolve to the canonical pattern.

### `get batch` — several objects in one call

```sh
d365fo get batch table:CustTable class:CustTableType edt:CustAccount --output json
```

Up to 10 `<kind>:<name>` specs per call. Each item carries its own `ok`/`error`
— one failed lookup never fails the batch. Replaces the MCP server's
`batch_get_info` and collapses N `get object` round-trips into one process.

### `resolve label` — look up a label token

```sh
d365fo labels resolve @SYS12345 --lang en-US,cs
```

### `read` — pull X++ source from AOT XML

```sh
d365fo read class CustTable_Extension --method validateWriteExt
```

`read table` and `read form` work the same way; add `--lines 10-40` or `--declaration` to scope the snippet.

### `models` — inspect indexed models

```sh
d365fo models list
d365fo models deps ApplicationSuite
d365fo models coupling --top 10
d365fo models coupling --only-cycles
```

`models list` enumerates every model with publisher, layer, and custom-flag. `models coupling` runs Tarjan SCC over `ModelDependencies` to surface dependency cycles and ranks every model by fan-in / fan-out / instability (`I = Ce / (Ca + Ce)`, where 0 = most stable, 1 = most volatile).

### Business events, policies, keys, tiles

```sh
# Business events
d365fo search business-event CustomerPayment --output json
d365fo get business-event CustPaymentBusinessEvent --output json

# Security policies (XDS)
d365fo search security-policy CustCustom --output json
d365fo get security-policy CustCustomSecurityPolicy --output json

# Configuration keys
d365fo search configuration-key TradeAgreements --output json

# Tiles and workspaces
d365fo search tile CustCustom --output json
d365fo search workspace VendPayment --output json
```

### `search any` — scope-agnostic quick jump

```sh
d365fo search any CustTable
```

UNIONs every indexed kind in one query and returns `byKind` counts for triage.

### `stats` — per-model + top-N aggregates

```sh
d365fo stats --top 10
```

Returns per-model object counts plus top tables (by field count), top classes (by method count), and top CoC extension targets. Handy for sizing a customisation and for agent prompts.

---

## Maintain the index

### `index refresh` — incremental re-extract

```sh
d365fo index refresh
d365fo index refresh --force          # re-scan every model
d365fo index extract --since 2026-04-01T00:00:00Z  # explicit threshold
d365fo index history --limit 20       # last 20 per-model extract runs
d365fo index history --model Contoso  # filter to one model
```

`index refresh` compares each model's content fingerprint against the DB row and re-extracts only changed models. `--force` ignores all thresholds.

### `lint` — in-process Best-Practice heuristics

```sh
d365fo lint
d365fo lint --category table-no-index,string-without-edt --all-models
d365fo lint --category today-usage,do-insert-update,doc-comment-missing
d365fo lint --format sarif > lint.sarif
```

16 rule categories (see [ARCHITECTURE.md](ARCHITECTURE.md) for the full list):

| Category | What it finds |
|---|---|
| `table-no-index` | Tables without cluster/alternate index |
| `string-without-edt` | String fields without an EDT |
| `insert-in-loop` | `.insert()` inside a loop — suggest `RecordInsertList` |
| `nested-select` | Nested `while select` loops |
| `force-literals` | `forceLiterals` in a select — SQL injection risk |
| `tts-try-catch` | `try` inside `ttsbegin`/`ttscommit` |
| `runbase-no-can-go-batch` | `RunBaseBatch` subclass without `canGoBatch()` |
| `doc-comment-missing` | Public/protected methods without `/// <summary>` |
| … | (and 8 more — see ARCHITECTURE.md) |

Defaults to custom models only; `--all-models` includes ISV/MS content. `--format sarif` emits SARIF 2.1.0 for CI (GitHub code-scanning, Azure DevOps).

### `validate name` — naming-rule linter

```sh
d365fo validate name Table FmVehicle --prefix Fm
d365fo validate name Coc CustTable_Extension
```

Static naming-rule check (publisher prefix, PascalCase, suffix conventions). Returns structured `violations[]` with `code`, `severity`, `message`. Useful as a pre-commit hook.

### `init` — quickstart

```sh
d365fo init                            # in a terminal: interactive wizard
d365fo init --run-extract
d365fo init --dry-run                  # show what would be done
d365fo init --persist-profile          # append env vars to $PROFILE / ~/.profile
```

Auto-detects the Windows `PackagesLocalDirectory`, prepares the SQLite schema, and (with `--run-extract`) drives the full extract pipeline. `--persist-profile` writes `D365FO_PACKAGES_PATH` / `D365FO_INDEX_DB` (and, if set, `D365FO_LABEL_LANGUAGES`) to the user's shell profile.

In a real terminal, `init` is a wizard unless `--packages`, `--output`, `--dry-run`, or `--no-wizard` is given (or output is piped/non-TTY, as it always is for an agent's shell tool calls) — it asks for the packages path, an optional UDE extra root, label languages, and whether to persist / build the index now. `--persist-profile` and `--run-extract` still work as pre-answers to those last two questions when the wizard does run, so `d365fo init --persist-profile` above launches the wizard interactively but skips asking whether to persist.

---

## Scaffold

`generate` writes atomically (`.tmp` + move) and keeps a `.bak` when `--overwrite` is used. Pass `--install-to <Model>` to drop the artefact straight into a model folder via the bridge (requires `D365FO_BRIDGE_ENABLED=1`, `D365FO_PACKAGES_PATH`, `D365FO_BIN_PATH`).

Add `--verify` to read the artefact back through the D365FO Metadata API after writing it, the way Visual Studio would. It applies to `--install-to` (the provider resolves objects by name inside the packages paths, so an arbitrary `--out` path is not something it can look up) and is skipped with a note when the runtime is unavailable — generation keeps working offline either way. A write that the provider then refuses to load fails with `VERIFY_FAILED`; the file is left on disk so you can open it in Visual Studio.

Every `generate` subcommand honours the flag, and the multi-file emitters (`business-event`, `custom-service`, `report`, `workflow`, `number-sequence`, …) verify each artefact they produce, not just the headline one. The payload always carries a `verify` object so a caller can tell the three outcomes apart rather than inferring them from silence:

```jsonc
"verify": { "status": "not-requested" }                       // --verify was not passed
"verify": { "status": "skipped", "detail": "…" }              // runtime unavailable, or --out rather than --install-to
"verify": { "status": "verified", "artefacts": [ … ] }        // every artefact read back, one entry each
```

Only one answer fails the command: `IMetadataProvider` was reachable and would not hand the object back — the document the reader refuses, which is what the flag is for. Everything else is a skip with its reason, because it is a limit of the tooling rather than a verdict on the artefact: a bridge that did not answer, a kind the bridge has no read channel for, or a MetaModel type its serializer cannot reflect (`AxMenuItemAction` and `AxSecurityPrivilege` load fine and still cannot be rendered back as XML — those verify, with the caveat in `detail`).

### Table

```sh
# Pattern-driven (P1) — emits canonical TableGroup, default fields, alt-key index
d365fo generate table FmCustomer \
  --pattern master \
  --label "@Fleet:Customer" \
  --install-to FleetManagement

# Header / lines pair (worksheet pattern)
d365fo generate table FmOrderHeader --pattern worksheet-header --install-to FleetManagement
d365fo generate table FmOrderLine   --pattern worksheet-line   --install-to FleetManagement

# Temp table — TempDB is a TableType, not a TableGroup
d365fo generate table FmTmpStaging \
  --pattern main --table-type TempDB \
  --install-to FleetManagement

# Hand-picked fields override pattern defaults
d365fo generate table FmVehicle \
  --label "@Fleet:Vehicle" \
  --field VIN:VinEdt:mandatory \
  --field Make:Name \
  --primary-key VIN \
  --out src/MyModel/AxTable/FmVehicle.xml

# Gate the table behind a configuration key and give it a drill-down form
d365fo generate table FmVehicle \
  --pattern master \
  --configuration-key FmModule \
  --form-ref FmVehicleForm \
  --install-to FleetManagement
```

Patterns: `main`/`master`, `transaction`, `parameter`/`setup`/`config`, `group`, `worksheetheader`/`header`, `worksheetline`/`line`, `reference`/`lookup`, `framework`, `miscellaneous`. Temp tables: `--table-type TempDB --pattern main`.

`--configuration-key` and `--form-ref` are omitted from the XML when not passed, so the AOT defaults (ungated, no drill-down form) still apply.

### Class

```sh
d365fo generate class FmVehicleService --extends RunBase \
  --out src/MyModel/AxClass/FmVehicleService.xml
```

### Chain-of-Command extension

```sh
d365fo generate coc CustTable --method update --method insert \
  --out src/MyModel/AxClass/CustTable_Extension.xml
```

### Simple-list form

```sh
d365fo generate simple-list FmVehicleListPage --table FmVehicle \
  --out src/MyModel/AxForm/FmVehicleListPage.xml
```

> **Deprecated alias.** Use `generate form --pattern SimpleList` instead.

### Form (any of nine D365FO patterns)

```sh
d365fo generate form FmVehicleDetails \
  --pattern DetailsMaster \
  --table FmVehicle \
  --field VIN --field Make --field Model \
  --caption "@Fleet:Vehicles" \
  --out src/MyModel/AxForm/FmVehicleDetails.xml
```

| Pattern | Use case |
|---|---|
| `SimpleList` | Setup / config tables |
| `SimpleListDetails` | List + detail panel |
| `DetailsMaster` | Full master record (CustTable shape) |
| `DetailsTransaction` | Header + lines — add `--lines-table <TABLE>` |
| `Dialog` | Modal popup |
| `TableOfContents` | Tabbed settings page |
| `Lookup` | Dropdown lookup |
| `ListPage` | Navigation list |
| `Workspace` | KPI tiles + panorama |

`--field` (repeatable) — grid/detail columns. `--section Name:Caption` — tabs / panorama sections. `--lines-table <TABLE>` — secondary datasource for `DetailsTransaction`.

**Pattern gate:** before writing, `generate form` validates the generated XML
against the form-pattern catalog (rules FP001–FP010). Structural violations
block the write while `D365FO_FORM_PATTERN_ENFORCE=true` (default); the JSON
summary carries a `patternCheck` block and any recommendations as `warnings`.

#### `validate form-pattern` — re-check any form XML

```sh
d365fo form-pattern validate src/MyModel/AxForm/FmVehicleDetails.xml --output json
cat FmVehicles.xml | d365fo form-pattern validate        # stdin works too
```

Exit codes: `0` clean (or warnings only), `2` structural errors. Run after any
hand edit of form XML; `d365fo form-pattern spec <P>` shows the required tree
when a violation needs fixing.

### Data entity (`AxDataEntityView`)

```sh
d365fo generate entity FmVehicleEntity --table FmVehicle \
  --public-entity-name FmVehicle --public-collection-name FmVehicles \
  --all-fields \
  --out src/MyModel/AxDataEntityView/FmVehicleEntity.xml
```

Emits a single-datasource `AxDataEntityView`. `--all-fields` auto-populates fields from the source table (mandatory flag carries over). Without it `<Fields />` is empty.

### Extension (table / form / EDT / enum)

```sh
d365fo generate extension Table CustTable Contoso \
  --out src/MyModel/AxTableExtension/CustTable.Contoso.xml
```

Name is always `<Target>.<Suffix>` to match the AOT convention. Kinds: `Table`, `Form`, `Edt`, `Enum`.

### Event handler

```sh
d365fo generate event-handler Contoso_CustTable_Handler \
  --source-kind Table --source-object CustTable --event inserted \
  --out src/MyModel/AxClass/Contoso_CustTable_Handler.xml
```

Picks the right attribute (`[DataEventHandler]`, `[FormEventHandler]`, `[FormDataSourceEventHandler]`, `[SubscribesTo]`) from `--source-kind`.

### Security privilege / duty

```sh
d365fo generate privilege FmVehicleReadPriv \
  --entry-point FmVehicleListPage --entry-kind MenuItemDisplay --entry-object FmVehicleListPage \
  --access Read --label "@Fleet:ReadVehicles" \
  --out src/MyModel/AxSecurityPrivilege/FmVehicleReadPriv.xml

d365fo generate duty FmVehicleMaintainDuty \
  --privilege FmVehicleReadPriv --privilege FmVehicleUpdatePriv \
  --out src/MyModel/AxSecurityDuty/FmVehicleMaintainDuty.xml

# Scaffold-and-wire in one pass: emit the duty/privilege XML AND merge its
# name into an existing role's <Duties> / <Privileges>.
d365fo generate duty FmReportingDuty --privilege FmReportsViewPriv \
  --out src/MyModel/AxSecurityDuty/FmReportingDuty.xml \
  --into-role src/MyModel/AxSecurityRole/FmVehicleAdminRole.xml
```

Both `generate privilege` and `generate duty` accept `--into-role <PATH>`; after the scaffold file is written, the role XML is updated idempotently with a `.bak` sibling (same merge path as `generate role --add-to`).

### SysOperation — batch operation with parameters

```sh
# Two-parameter batch job — emits contract, service, and controller XML
d365fo generate sysoperation ProcessVendorPayments \
  --param "fromDate:TransDate" --param "toDate:TransDate" \
  --execution-mode Asynchronous \
  --out-contract c:/AOT/MyModel/AxClass/ProcessVendorPaymentsContract.xml \
  --out-service  c:/AOT/MyModel/AxClass/ProcessVendorPaymentsService.xml \
  --out          c:/AOT/MyModel/AxClass/ProcessVendorPaymentsController.xml
```

The controller XML extends `SysOperationServiceController`. `--param name:EdtType` (repeatable) generates `parmXxx()` accessors decorated with `[DataMemberAttribute]` on the contract class. Use `--execution-mode Synchronous|Asynchronous|ScheduledBatch`.

### Number sequence — three-file output

```sh
# Module extension class + EDT + form handler
d365fo generate number-sequence CustCustomModule \
  --edt CustCustomNum --scope Company --table CustCustomTable \
  --out         c:/AOT/MyModel/AxClass/NumberSeqModuleExtension_CustCustomModule.xml \
  --out-edt     c:/AOT/MyModel/AxEdt/CustCustomNum.xml \
  --out-handler c:/AOT/MyModel/AxClass/CustCustomTable_NumberSeqFormHandler.xml
```

Emits a CoC extension of `NumberSeqApplicationModule` wired to the new EDT and an optional `NumberSeqFormHandler` extension class for the target form.

### Workflow — PurchTable approval workflow

```sh
# Approval workflow with document class, approval element and submit stub
d365fo generate workflow PurchTableApproval \
  --table PurchTable --approval-name PurchTableApprovalElement \
  --out          c:/AOT/MyModel/AxWorkflowTemplate/PurchTableApproval.xml \
  --out-document c:/AOT/MyModel/AxClass/PurchTableWorkflowDocument.xml \
  --out-approval c:/AOT/MyModel/AxWorkflowApproval/PurchTableApprovalElement.xml \
  --out-submit   c:/AOT/MyModel/AxClass/PurchTable_WorkflowSubmitExtension.xml
```

The workflow type itself is an `AxWorkflowTemplate` — there is no `AxWorkflow` AOT folder.
`--out-document` emits a `WorkflowDocument` subclass. `--approval-name` / `--task-name` emit
`AxWorkflowApproval` / `AxWorkflowTask` elements and reference them from `SupportedElements`.
`--out-submit` emits a CoC extension of the target table that adds `canSubmitToWorkflow()`.
The command reports the menu items, query and `--category` workflow category it references but
does not create — the model does not build until those exist.

### Menu item — display menu item for a form

```sh
d365fo generate menu-item CustCustomForm \
  --kind Display --object CustCustomForm --object-type Form \
  --label "@SYS12345" \
  --out c:/AOT/MyModel/AxMenuItemDisplay/CustCustomForm.xml
```

`--kind` accepts `Display`, `Action`, or `Output`. `--object-type` accepts `Form`, `Class`, or `Report`.

### EDT — custom string EDT

```sh
d365fo generate edt CustCustomId --base-type String --size 20 --label "@SYS100" \
  --out c:/AOT/MyModel/AxEdt/CustCustomId.xml
```

`--base-type` accepts `String`, `Integer`, `Real`, `Int64`, `Date`, `UtcDateTime`, `Enum`, `Guid`. `--size` applies to String EDTs. Use `--extends <BaseEdt>` to derive from an existing EDT.

### Enum — extensible status enum

```sh
d365fo generate enum CustCustomStatus \
  --value "None:0:@SYS000" --value "Active:1:@SYS001" --value "Closed:2:@SYS002" \
  --out c:/AOT/MyModel/AxEnum/CustCustomStatus.xml
```

`--value name:intValue:labelKey` (repeatable). Adds `<IsExtensible>Yes</IsExtensible>` by default — pass `--no-extensible` to opt out.

### Query — SalesTable + SalesLine inner join

```sh
d365fo generate query SalesTableWithLines \
  --ds SalesTable --join "SalesLine:InnerJoin:SalesTable" \
  --out c:/AOT/MyModel/AxQuery/SalesTableWithLines.xml
```

`--ds` sets the root datasource. `--join target:joinKind:parentDs` (repeatable) adds embedded datasources. `joinKind` accepts `InnerJoin`, `OuterJoin`, `ExistsJoin`, `NotExistsJoin`.

### View — a read-only projection over a query

```sh
d365fo generate view FmOpenRentals \
  --query FmOpenRentalsQuery \
  --field VehicleId:FmVehicle \
  --field RentalId:FmRental:RecId \
  --computed DaysOut:getDaysOutSQL:Int \
  --install-to FleetManagement
```

`--field <name>:<dataSource>[:<dataField>]` projects a query column (`dataField`
defaults to `name`). `--computed <name>:<viewMethod>:<type>` adds a field backed by
an X++ method returning SQL; the type (`String|Int|Int64|Real|Date|UtcDateTime|Enum`)
is required because the AOT encodes it in the field's discriminator and cannot infer
it from the method. `--query` is mandatory — a view without one has nothing to
project. No `<Ranges>` and no `<Title*>` are emitted: ranges belong on the backing
query.

### Map — a shared field template across tables

```sh
d365fo generate map FmAddressMap \
  --field Street:Name --field City:Name \
  --map-to "FmVehicle:Street=AddrStreet,City" \
  --map-to FmRental \
  --install-to FleetManagement
```

`--field <name>:<edt>[:<label>]` declares a map field; the EDT drives its concrete
type, resolved through the same index-backed lookup `generate table` uses, so a map
field and a table field on one EDT can never disagree. `--map-to <table>[:pairs]`
wires the map to a table; omit the pairs to connect every field by identical name.
Connecting a field the map does not declare is rejected rather than written.

Both write straight to disk — the metadata bridge's `createObject` only accepts
class/table/EDT/enum/form, so `--install-to` resolves the model folder and the
scaffold is written there (same as `generate query`).

### Navigation: menu, tile, form part

```sh
# A module menu: sub-menus in the order declared, items under them by path
d365fo generate menu ConFleet --label "@FLM540"   --submenu "Vehicles:@FLM95"   --item Vehicles/FMVehicle --item Setup/FMSetup:Action   --tile Workspaces/FMClerkWorkspace   --in-content-area --install-to FleetManagement

# A wide tile that opens a menu item; --type Count with --query makes it a cue
d365fo generate tile ConFmVehiclesTile --menu-item FMVehicle   --label "@FLM95" --size Wide --install-to FleetManagement

# The other two types open something else instead of a menu item
d365fo generate tile ConFmUtilizationKpi --type KPI --kpi ConFmUtilization   --size Wide --install-to FleetManagement
d365fo generate tile ConFmDocsLink --type Link --url "https://learn.microsoft.com/dynamics365/"   --display TextOnly --install-to FleetManagement

# Register a form as a hostable part (preview pane, fact box)
d365fo generate form-part ConFmVehiclePreviewPart --form FMVehicle   --caption "@FLM95" --install-to FleetManagement
```

`--item [<submenu>/]<menuItem>[:Display|Action|Output]`: Display is the contract
default and is not written. A menu item the index does not know is reported in
`unknownMenuItems` rather than refused — the index is a mirror, and the item may
live in a model that has not been extracted. A tile's target follows its `--type`:
762 of the 770 shipped tiles carry `<MenuItemName>` and all of them are Standard
or Count; the eight that do not are the KPI and Link tiles, which bind `<KPI>` and
`<URL>` instead.

### Configuration key, workflow category, label file, resource

```sh
d365fo generate configuration-key ConFmFleetTrial --label "@FLM1"   --parent-key Bank --install-to FleetManagement

# --module is a ModuleAxapta value and is checked against the enum in the index
d365fo generate workflow-category ConFmVehicleWfCategory --module Basic   --label "@FLM2" --help-text "@FLM3" --install-to FleetManagement

# One AxLabelFile per language; the .label.txt lands under LabelResources/<lang>/
d365fo generate label-file ConFleet --language en-US   --entry "Vehicles=Fleet vehicles" --install-to FleetManagement

# The manifest plus the file, copied to AxResource/ResourceContent/Images/
d365fo generate resource ConFmLogo --source ./assets/ConFmLogo.png   --install-to FleetManagement
```

A resource whose content file is missing is a **Metadata Error** at compile time
("Resource content file 'ConFmLogo.png' not found"), not a deployment surprise, so
the command warns when it writes a manifest without `--source`.

### Composite and aggregate data entities

```sh
# Header/lines bundle for Data management: the relation is the one on the LINE
# entity that binds it to its parent
d365fo generate composite-entity ConFmTestCompositeEntity   --root DMFTestHeaderEntity   --embedded DMFTestLineEntity:DMFTestHeader   --label "Test header and lines" --install-to FleetManagement

# Read-only projection of an aggregate measurement
d365fo generate aggregate-entity ConFmRentalsByColor   --measurement FMAggregateMeasurements   --measure NoRentals:FMRentalCharges:NoRentals:BIRCount   --dimension VehicleColor:FMRentalCharges:FMVehicle:VehicleColor:FMColorName   --install-to FleetManagement
```

The composite's X++ declaration carries `[CompositeDataEntityView]` and does not
extend `common`; the aggregate's does. An aggregate field pins
`i:type="d3p1:AxAggregateDataEntityMappedField"` and writes its mapping members in
the `Microsoft.Dynamics.AX.Metadata.V2` namespace, exactly as the platform does —
unprefixed, the reader keeps the field and drops every mapping on it. The
measurement, its groups and attributes are not in the index, so the command says
they were not verified; `d365fo build` is what proves them.

### Map extension

```sh
d365fo generate extension Map AssetTransMap --suffix ConFm --install-to FleetManagement
```

The contract declares nothing beyond `Name`, and the installation ships no map
extension at all (census: 0 files) — the object is the shell a later edit hangs on.

### Security role (new or merge)

```sh
# Scaffold a new role that references duties / privileges
d365fo generate role FmVehicleAdminRole \
  --duty FmVehicleMaintainDuty --privilege FmVehicleReadPriv \
  --label "@Fleet:VehicleAdminRole" --description "Full access to Fleet vehicles" \
  --out src/MyModel/AxSecurityRole/FmVehicleAdminRole.xml

# Merge new references into an existing role (idempotent; writes .bak)
d365fo generate role --add-to src/MyModel/AxSecurityRole/FmVehicleAdminRole.xml \
  --duty FmReportingDuty --privilege FmExportPriv
```

`--add-to` validates the root element (`AxSecurityRole`), dedupes by `Name` (case-insensitive), and returns `NoChange` when every reference already exists.

### Report (`AxReport` + DP class + data contract)

```sh
# Minimal — one dataset, no columns specified (tablix shell only)
d365fo generate report FmVehicleReport \
  --dp FmVehicleReportDP \
  --tmp FmVehicleReportTmp \
  --dataset FmVehicleDS \
  --caption "@Fleet:VehicleReport" \
  --out src/MyModel/AxReport/FmVehicleReport.xml

# With tablix column definitions — generates header row + data row in the tablix
d365fo generate report FmVehicleReport \
  --dp FmVehicleReportDP --tmp FmVehicleReportTmp \
  --field VIN --field Make --field Model --field Year \
  --caption "@Fleet:VehicleReport" \
  --out src/MyModel/AxReport/FmVehicleReport.xml

# With report parameters — auto-generates a DataContract class (FmVehicleReportDPContract.xml)
d365fo generate report FmVehicleReport \
  --dp FmVehicleReportDP \
  --field VIN --field Make --field Year \
  --parameter FromDate:DateTime --parameter ToDate:DateTime --parameter Customer \
  --out src/MyModel/AxReport/FmVehicleReport.xml

# Multi-dataset — primary dataset + additional via --extra-dataset
d365fo generate report FmFleetSummaryReport \
  --dp FmFleetSummaryReportDP \
  --field VehicleCount --field TotalMileage \
  --extra-dataset FmFleetCostsDS:FmFleetCostsDP \
  --extra-dataset FmFleetIncidentsDS:FmFleetIncidentsDP \
  --parameter PeriodYear:Integer \
  --out src/MyModel/AxReport/FmFleetSummaryReport.xml
```

The command atomically writes **two or three XML files**: `AxReport` (datasets + auto-design + tablix), DP class extending `SrsReportDataProviderBase`, and (when `--parameter` is used) a DataContract class extending `SrsReportDataContractBase`. Output overrides: `--out-dp`, `--out-contract`.

### Business event — custom event with payload

```sh
# Scaffold event class + contract class
d365fo generate business-event CustCustomBusinessEvent \
  --contract-name CustCustomBusinessEventContract \
  --payload "custAccount:CustAccount" --payload "amount:AmountCur" \
  --category "CustomerEvents" --primary-table CustTable \
  --out          c:/AOT/MyModel/AxClass/CustCustomBusinessEvent.xml \
  --out-contract c:/AOT/MyModel/AxClass/CustCustomBusinessEventContract.xml
```

The event class extends `BusinessEventsBase` and is decorated with `[BusinessEvents(classStr(CustCustomBusinessEvent), classStr(CustCustomBusinessEventContract), "CustomerEvents", "...")]`. The contract class implements `BusinessEventsContract` with `parmXxx()` accessors for each `--payload` entry.

After scaffolding, activate the event in **System Administration > Business events catalog**.

### Custom service — JSON/SOAP service with operations

```sh
# Scaffold service XML + service class + service group
d365fo generate custom-service CustCustomService \
  --class-name CustCustomServiceClass --group-name CustCustomServiceGroup \
  --operation "processCustomer:CustAccount" \
  --out       c:/AOT/MyModel/AxService/CustCustomService.xml \
  --out-class c:/AOT/MyModel/AxClass/CustCustomServiceClass.xml \
  --out-group c:/AOT/MyModel/AxServiceGroup/CustCustomServiceGroup.xml
```

Use `d365fo search service <name>` to check for existing services before naming.

### Migration script — data-fix runnable with batching

```sh
d365fo generate migration-script MigrateCustCustomData \
  --source-table CustCustomTableOld --target-table CustCustomTable \
  --batch-size 500 --mode Upsert \
  --out c:/AOT/MyModel/AxClass/MigrateCustCustomData.xml
```

`--mode` accepts `Insert`, `Update`, `Upsert`. Emits a `Runnable` class with `doInsert`/`doUpdate` (the one documented exception where `do*` methods are appropriate for migration logic) and configurable `ttsbegin`/`ttscommit` batching with progress logging.

### RunBase — legacy batch class

```sh
d365fo generate runbase CustCustomReport \
  --batch \
  --dialog-param "fromDate:TransDate" --dialog-param "toDate:TransDate" \
  --out c:/AOT/MyModel/AxClass/CustCustomReport.xml
```

`--batch` adds `canGoBatch() { return true; }` and `pack()`/`unpack()` with a `container` member list. `--dialog-param name:EdtType` (repeatable) generates dialog field declarations and `getFromDialog()` logic. Use `generate sysoperation` for new code — `generate runbase` is provided for ISV teams maintaining older codebases.

### Security policy — XDS row-level security policy

```sh
d365fo generate security-policy CustCustomSecurityPolicy \
  --constrained-table CustCustomTable --policy-query CustCustomPolicyQuery \
  --operation Select --context-type RoleName --context-value CustCustomRole \
  --out c:/AOT/MyModel/AxSecurityPolicy/CustCustomSecurityPolicy.xml
```

`--operation` accepts `All`, `Select`, `Insert`, `Update`, `Delete`. `--context-type` accepts `RoleName` or `ContextString`. Use `d365fo search security-policy <name>` to audit existing XDS policies before adding new ones.

---

## Suggest / Analyze

### `suggest extension` — extensibility strategy advisor

```sh
# Auto-detect object kind and rank extension strategies
d365fo suggest extension CustTable --output json

# Hint the kind explicitly when needed
d365fo suggest extension SalesFormLetterService --kind Class --output json
d365fo suggest extension SalesTable --kind Table  --output json
d365fo suggest extension CustTable  --kind Form   --output json
```

Ranks extension strategies (`CoC`, `EventHandler`, `Extension`) by confidence based on what already exists in the codebase. Use before scaffolding.

### `analyze patterns` — how a scenario is usually done here

```sh
# Which APIs does the code around this scenario reach for?
d365fo analyze patterns "number sequence" --output json

# Restrict to your own model to see how THIS codebase does it
d365fo analyze patterns "posting" --model ConFleet --output json
```

Ranked by how many **distinct** objects use each API, so one verbose class cannot make its own
habits look like the codebase's. Reads whole method bodies, not just the matching line — the line
that mentions "number sequence" is usually a comment, and the call that answers the question is
below it.

Check `coverage.searched`: `false` means the corpus could not be read at all, which is a different
answer from "nothing matches".

### `analyze implementations` — who else implements this method

```sh
# Before writing an override or a CoC wrapper
d365fo analyze implementations validateWrite --output json
d365fo analyze implementations initValue --model ApplicationSuite --output json
```

### `analyze api-usage` — how an API is really reached

```sh
d365fo analyze api-usage NumberSeq --output json
d365fo analyze api-usage DocumentManagement --limit 10 --output json
```

Separates construction from static calls from declarations. When an API is never constructed, it
says so — a `new` is then the wrong shape.

### `analyze completeness` — cross-check project against index

```sh
# Analyse a full model folder (walks all *.xml recursively)
d365fo analyze completeness src/MyModel --output json

# Analyse a single AOT XML file
d365fo analyze completeness src/MyModel/AxSecurityRole/FmAdminRole.xml

# Skip slow label checks in CI (focus on structural refs only)
d365fo analyze completeness src/MyModel --skip-labels --output json

# Pipe into jq to count issues by code
d365fo analyze completeness src/MyModel --output json \
  | jq '.data.issues | group_by(.code) | map({code: .[0].code, count: length})'
```

Reports `MISSING_DUTY`, `MISSING_PRIVILEGE`, `MISSING_EDT`, `MISSING_LABEL`, `PARSE_ERROR`. Flags: `--skip-labels`, `--skip-edts`, `--skip-security`.

---

## Labels (read & write)

```sh
# Read
d365fo labels search "Customer invoice"
d365fo labels search "customer invoice" --fts        # rank-sorted FTS5
d365fo get label @SYS12345 --language en-us

# Write \u2014 atomic, preserves comments, BOM UTF-8
d365fo label create NewKey "New value" --file path/Foo.en-us.label.txt
d365fo label create NewKey "Updated"   --file path/Foo.en-us.label.txt --overwrite
d365fo label rename NewKey RenamedKey  --file path/Foo.en-us.label.txt
d365fo label delete RenamedKey         --file path/Foo.en-us.label.txt
```

`search label --fts` requires FTS5 (falls back to `LIKE` if unavailable). Write commands keep a `.bak` of the previous file.

---

### `labels update` — correct an existing label

```sh
# One language
d365fo labels update ConVehicle "Vehicle" --install-to ConFleet --lang en-us

# Several: one --text per language
d365fo labels update ConVehicle --install-to ConFleet --lang en-us,cs \
  --text en-us=Vehicle --text cs=Vozidlo
```

Refuses a key that does not exist (that is `labels create`), and refuses one text for several
languages — which would replace every other translation with this one's string.

## Journal / undo

```sh
# Inspect the stack (most-recent-first)
d365fo journal list --output json
d365fo journal list --limit 5 --output json

# Preview before committing to an undo
d365fo undo --dry-run --output json

# Revert the last write, or the last N
d365fo undo --output json
d365fo undo --steps 3 --output json

# Delete an AOT object (journaled — undo-able like any other write)
d365fo delete --kind table --name OldTable --path C:\pkg\MyModel\MyModel\AxTable\OldTable.xml
d365fo delete --kind class --name OldClass --install-to MyModel   # via the metadata bridge
```

Every `generate *`, `labels create\|rename\|delete`, and `delete` write appends an entry to a
size-capped journal at `<index-dir>/journal/`. `undo` replays entries in reverse through the
same write path that produced them (disk or bridge), restoring the exact pre-image — a create
is removed, an update/delete is restored byte-for-byte. Stops at the first failure so older
entries are never skipped.

---

## Review

```sh
d365fo review diff --base HEAD
```

Compare two revs with `--base main --head feature/my-branch`. Rules shipped today:

- `FIELD_WITHOUT_EDT` — table field without `<ExtendedDataType>`.
- `FIELD_WITHOUT_LABEL` — user-facing field without `<Label>`.
- `HARDCODED_STRING` — verbatim string literal in X++ source.
- `DYNAMIC_QUERY` — dynamic `Query` construction (flag for security review).

---

## Verify the project

### `verify` — objects on disk, and in the `.rnrproj`

```sh
# The whole model
d365fo verify ConFleet --output json

# Just the objects you believe you created
d365fo verify ConFleet --expect ConFleetVehicle --expect AxClass/ConFleetPosting --output json

# A folder that is not under a configured packages path
d365fo verify --path /repo/PackagesLocalDirectory/ConFleet/ConFleet --output json
```

`UNREGISTERED` means the file is there and the project does not list it, so it is never compiled —
the failure mode nothing else reports. Exit code 2 when the model and the project disagree.

## Windows-only ops (D365FO VM)

These commands wrap the Microsoft tooling Visual Studio uses, so you can drive the IDE's workflow from a terminal, script, or CI pipeline.

```powershell
d365fo build --project C:\AosService\PackagesLocalDirectory\MyModel\MyModel.rnrproj
d365fo sync --full
d365fo test run --suite MyModel.Tests
d365fo bp check --model MyModel
```

Each parses the tool output and returns a structured JSON envelope (errors, warnings, elapsed time, tail of stdout). On non-Windows they return `UNSUPPORTED_PLATFORM`.

---

## Agent integration

### Emit the system prompt

```sh
d365fo agent-prompt --out .prompts/d365fo.md
```

`d365fo schema --full` emits a machine-readable catalog of every command.

### GitHub Copilot (VS Code / Visual Studio)

```powershell
.\scripts\Install-D365FoCopilotSkills.ps1 -XppRepo "K:\D365FO\MyProject"
```

Copilot auto-discovers `.github/skills/d365fo-cli/` and loads the skill on demand — no extra configuration needed. It drives `d365fo` through its terminal tool.

### Claude Code / Claude Desktop

Drop `skills/anthropic/` into the project or `~/.claude/skills/`. Each `SKILL.md` triggers via its `applies_when` front-matter.

### Codex CLI / Gemini CLI

Paste the output of `d365fo agent-prompt` into the session system prompt, or reference it from `AGENTS.md`.

Use the compact manifest when the agent needs to discover the CLI surface:

```sh
d365fo schema
```

Use the complete manifest only when needed:

```sh
d365fo schema --full
```

CLI-first shortcuts replace common MCP multi-call workflows:

```sh
d365fo search batch CustTable SalesTable CustAccount --output json
d365fo get object table CustTable --output json
d365fo find related coc CustTable --method validateWrite --output json
```

### MCP server (`d365fo-mcp`)

Standalone JSON-RPC 2.0 server (protocol `2024-11-05`) that shares the CLI's index. Config sample for Claude Desktop:

```jsonc
{
  "mcpServers": {
    "d365fo": {
      "command": "dotnet",
      "args": ["run", "--project", "/abs/path/to/src/D365FO.Mcp", "--no-build"],
      "env": { "D365FO_INDEX_DB": "/abs/path/d365fo-index.sqlite" }
    }
  }
}
```

After `dotnet publish src/D365FO.Mcp -c Release -r osx-arm64` you get a standalone `d365fo-mcp` binary you can drop on `$PATH`. The adapter exposes **20 consolidated, discriminator-based tools** (`search`, `get_object_info`, `get_method`, `extension_info`, `object_patterns`, `generate_object`, `security_info`, `labels`, `analyze`, `models`, …) covering CLI parity, security & labels, heuristics, and aggregation — same index, same guardrails. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md) for the full tool list.

---

## Daemon (warm cache)

For latency-sensitive integrations, run the CLI as a daemon so the SQLite handle and read caches stay hot:

```sh
d365fo daemon start                                  # named pipe / Unix socket + file watcher
d365fo daemon start --packages J:\AosService\PackagesLocalDirectory
d365fo daemon start --no-watch                      # disable auto index refresh
d365fo daemon start --watch-debounce 5000           # wait 5 s after last change before refresh
d365fo daemon status
d365fo daemon stop
```

Transport: Windows named pipe `\\\\.\\ pipe\\d365fo-cli`; Unix socket at `$XDG_RUNTIME_DIR/d365fo-cli.sock` (fallback `$TMPDIR`). The frame format matches `d365fo-mcp`: one newline-terminated JSON-RPC request per connection, one response, close.

The daemon also starts a `FileSystemWatcher` over `D365FO_PACKAGES_PATH` (or `--packages`). When `*.xml` files change, it debounces (default 3 s) and automatically triggers an incremental `index refresh` for the affected model, emitting a JSON notification to stderr:

```json
{ "event": "index_refreshed", "model": "Contoso" }
```

Pass `--no-watch` to disable the watcher (e.g. read-only environments or network shares).

---

## CI / automation

Every command is scriptable: exit codes are reliable, output is JSON by default in non-TTY, no interactive prompts.

```yaml
- name: D365 review
  run: |
    d365fo index build
    d365fo review diff --base origin/main --head HEAD --output json \
      | jq -e '.data.violationCount == 0'
```

---

## See also

- [SETUP.md](SETUP.md) — install, configure, connect an AI agent.
- [CAPABILITIES.md](CAPABILITIES.md) — full command reference and the built-in-editor-vs-`d365fo` decision table.
- [ARCHITECTURE.md](ARCHITECTURE.md) — index schema, lint rules, form pattern engine, the daemon.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common failure modes and fixes.
