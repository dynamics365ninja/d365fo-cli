---
id: forms-and-navigation
description: Extend a D365FO form at the right lifecycle point, and wire objects into the navigation menus. Invoke when the user asks where to put form logic (init, executeQuery, active, validateWrite, clicked, modified), how to refresh a grid, how to read caller context, or how to add a menu item or a nested submenu.
covers: Form lifecycle extension points, menus and submenu nesting
applyTo:
  - "**/AxForm/**"
  - "**/AxMenu/**"
  - "**/AxMenuItemDisplay/**"
  - "**/AxMenuItemAction/**"
  - "**/AxMenuItemOutput/**"
appliesWhen: User intent mentions form lifecycle, form init, executeQuery, datasource active, validateWrite, clicked or modified handlers, refreshing a grid, element.args, form control names, menus, menu items, or nesting a submenu.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Form lifecycle and navigation

## 1. The initialization sequence

```
form.init()
  └── FormDataSource.init()        (per data source)
        └── form.run()
              └── FormDataSource.executeQuery()
                    └── FormDataSource.active()   (per cursor move)
```

| Method | Fires | Put here |
|---|---|---|
| `form.init()` | once; structure loaded, data sources **not yet active** | ranges and query changes before the first run |
| `FormDataSource.init()` | once per data source; link types resolved | default ranges for that data source |
| `FormDataSource.executeQuery()` | on **every** refresh | query changes that depend on current state |
| `FormDataSource.active()` | on every cursor move | update dependent data sources, enable/disable controls |
| `FormDataSource.validateWrite()` | before save | validation — return `false` to block the save |
| `FormDataSource.write()` | after save | post-save work; the record is already committed |
| `FormControl.clicked()` / `.modified()` | user interaction | button and field handlers |

- Refresh the grid with `FormDataSource.research(true)` — it keeps the cursor
  position. `executeQuery()` resets it.
- `element.args()` carries the caller context: the record, the menu item, and any
  enum parameter.
- `element.design().controlName(formControlStr(MyForm, MyControl))` reaches a
  control at runtime.
- `FormDataSource.queryBuildDataSource()` exposes the underlying QBDS for runtime
  range work — use `SysQuery::findOrCreateRange` there, since `executeQuery()`
  runs repeatedly.

**Never guess a control name.** Control names differ from field names and are
usually prefixed:

```sh
d365fo get form CustTable --output json | jq '.data.controls[].name'
d365fo find extensions CustTable --output json     # who already extends this form?
```

## 2. Extending a form

- Form-level CoC: `[ExtensionOf(formStr(CustTable))] final class CustTable_Fm_Extension`.
- Nested wrapping uses `formDataSourceStr`, `formDataFieldStr`, `formControlStr`
  — and **cannot add new methods**, only wrap existing ones.
- Forms cannot be wrapped statically.
- Event handlers are the alternative when CoC is blocked; see
  `d365fo knowledge get event-handler-authoring`.

```sh
d365fo generate extension Form CustTable --suffix Fm --install-to FleetManagement
d365fo generate event-handler --source-kind Form --source CustTable \
  --event Initialized --install-to FleetManagement
```

**Typical overrides per pattern:**

| Pattern | Usual extension points |
|---|---|
| SimpleList / setup | data source `initValue` + `validateWrite` |
| DetailsMaster | `form.init` + data source `active` / `validateWrite` |
| DetailsTransaction | line data source `initValue` (defaults from the header) + header `active` |
| Dialog | `form.init` (read `element.args()`) + `closeOk` |
| Lookup | `form.init` + data source `executeQuery` (filter by caller context) |

`d365fo form-pattern spec <Pattern> --output json` lists the structure the pattern
requires before you start.

### Overrides on the form itself

When the method belongs on the form rather than in an extension class — a new
form, or one this model owns — write it into the AxForm XML through the two
method scaffolders instead of by hand. Both take the form's XML file as their
first argument and merge into it:

```sh
# Data source override: init, active, validateWrite, initValue, executeQuery, write, delete …
d365fo generate datasource-method c:/AOT/MyModel/AxForm/FmVehicleList.xml   --datasource FmVehicle --method validateWrite

# Control event: clicked, modified, lookup, enter, leave …
d365fo generate control-method c:/AOT/MyModel/AxForm/FmVehicleList.xml   --control Grid_VIN --method clicked
```

The scaffold carries the correct signature and the `super()` call the override
needs — the two things that are easy to get wrong from memory and that decide
whether the form still saves. A method the form already declares is left alone
rather than overwritten.

### Starting from a form that already works

`generate form-clone` copies an existing AxForm under a new name, keeping its
pattern, controls and data source wiring, and optionally re-pointing the data
sources at other tables:

```sh
d365fo generate form-clone FmServiceOrderInquiry   --from FmVehicleServiceOrder   --rebind FmVehicleLine=FmServiceOrder   --install-to FleetManagement
```

`--from` takes a form name resolved through the index, or a path to an AxForm
XML file. `--rebind <OldTable>=<NewTable>` is repeatable and moves the data
sources; everything the clone keeps is what the original had, so start from a
form whose pattern is the one you want rather than from the nearest one.

## 3. Menus and menu items

An `AxMenu` is an `<Elements>` collection of `AxMenuElement` entries, and a
sub-menu is **not** a separate object: `AxMenuElementSubMenu` carries its own
nested `<Elements>` inline, with a `<Label>`. Which *kind* of element you write
decides whether it works:

| Element type | Purpose | Key field |
|---|---|---|
| `AxMenuElementMenuItem` | a display/action/output menu item | `<MenuItemName>` (+ `<MenuItemType>` for Action/Output; Display is the default and is not written) |
| `AxMenuElementSubMenu` | a folder ("Inquiries and reports") holding its own `<Elements>` | `<Name>`, `<Label>`, nested `<Elements>` |
| `AxMenuElementTile` | a workspace tile | `<Tile>` |
| `AxMenuElementMenuReference` | pull in **another `AxMenu`** by name | `<MenuName>` |
| `AxMenuElementSeparator` | visual separator | — |

Counted on a stock installation (81 menus): 60 nest sub-menus inline this way,
1 references another menu through `AxMenuElementMenuReference`/`<MenuName>`,
and **no file carries a `<SubMenu>` member** — the type declares none. The
element types are what `Microsoft.Dynamics.AX.Metadata.dll` declares;
`AxMenuElementMenu` is not one of them.

- A wrong element-type name is **not caught by xppc**. It surfaces only when the
  metadata generation step tries to deserialize the file
  ("cannot be deserialized as AxMenu … no knowledge of any type that maps to this
  name"). `d365fo validate metadata <file>` asks the provider the same question
  offline from the build.
- Every `AxMenuElement` is written with `xmlns=""` — the menu contracts into
  `Microsoft.Dynamics.AX.Metadata.V1`, its elements into no namespace.
- Element `<Name>`s are the keys of the collection: the same name twice in one
  container is a document the reader silently halves.
- **Adding to a menu you do not own is an `AxMenuExtension`**, never an edit of the
  Microsoft file. Each addition is an `AxMenuExtensionElement` wrapping the same
  element shapes, optionally with a `<Parent>` — an existing element of the base
  menu — and a position (`Begin`, `End`, or `AfterItem` + `<PreviousSibling>`).
  Counted: 248 shipped menu extensions, `Parent` on 154, `PositionType` on 98.
  `generate extension Menu <BaseMenu> --suffix <S> --item <Parent>/<MenuItem>`;
  a path naming a submenu the extension itself adds (`--submenu`) nests inside it
  instead.

```sh
d365fo generate menu-item FmVehicleListPageMenuItem --kind Display \
  --object FmVehicleListPage --object-type Form \
  --label "@Fleet:Vehicles" --install-to FleetManagement

# The menu itself: sub-menus in the order declared, items placed by path
d365fo generate menu ConFleet --label "@Fleet:Fleet" \
  --submenu "Vehicles:@Fleet:Vehicles" \
  --item Vehicles/FmVehicleListPageMenuItem --item Setup/FmSetup:Action \
  --tile Workspaces/FmClerkWorkspace --in-content-area \
  --install-to FleetManagement

# Add to a standard menu: a new sub-menu after Customers, and one item under Customers
d365fo generate extension Menu AccountsReceivable --suffix Fleet \
  --submenu "Fleet:@Fleet:Fleet" --item Fleet/FmVehicleListPageMenuItem \
  --item Customers/FmCustomer --after Customers \
  --install-to FleetManagement

# Prove the result deserializes the way the AOS will read it
d365fo validate metadata <path-to-menu-xml> --output json
```

### Tiles, form parts and image resources

- A **tile** (`AxTile`) opens a menu item and sits on a workspace; every one of
  the 775 the platform ships carries `<MenuItemName>`. `generate tile <Name>
  --menu-item <MenuItem> [--type Count --query <AxQuery>]` — see
  `d365fo knowledge get analytics-and-er` for cues and KPIs.
- A **form part** (`AxFormPart`) registers a form so another form can host it as
  an info part, fact box or preview pane. Three members, all mandatory in
  practice: `<Name>`, `<Caption>`, `<Form>`.
  `generate form-part <Name> --form <AxForm> --caption <label>`.
- An **image** for a tile or menu is an `AxResource` manifest plus the file under
  `AxResource/ResourceContent/Images/`. The compiler checks that the file the
  manifest names exists ("Resource content file 'X.png' not found" is a Metadata
  Error). `generate resource <Name> --source ./X.png` writes both; `--type`
  selects the `ResourceContent` folder for non-image resources (`XmlDoc`, `Data`,
  `PowerBIReport` …).

## 4. Args — the record, caller and parameters an object is opened with

Every menu item, form and report is entered through an Args instance. Reading
it wrongly is how a form silently opens on the wrong record.

- Reach it from a form with `element.args()`, from a class via `main(Args _args)`.
- **The record**: `_args.record()` returns the caller's cursor as a table
  buffer — assign it to a typed buffer, and check `_args.dataset()` (the table
  id) BEFORE trusting it, because any caller can pass any table.
- **The caller**: `_args.caller()` returns an Object. Test with `is` and
  downcast with `as` — Object and FormRun are late-bound, so a call on the
  wrong type fails at RUNTIME, not compile time. Never assume the caller is a
  form: a batch or a service reaches the same code with `caller()` null.
- **Extra values**: `_args.parm()` carries one string, `_args.parmEnum()` one
  enum value with `_args.parmEnumType()` naming its type (set via
  `enumNum(MyEnum)`), `_args.parmObject()` any object. Each is get/set.
- **Which entry point**: `_args.menuItemName()` / `_args.menuItemType()`;
  `_args.openMode()` distinguishes New/Edit/View; `_args.lookupField()` /
  `_args.lookupValue()` carry a lookup's field and value.
- Opening something WITH arguments: build the Args (`args.record(myBuffer);
  args.parm(myId);`), then run the menu function with it.

## 5. display and edit methods (computed and writable columns)

Neither is deprecated — they remain the supported way to show a computed value
(computed columns replace them on data entities and views only).

- `display <ReturnType> name()` — computed, READ-ONLY on the form/report. On
  the table when every form should see it; on the form when only that one.
- `edit <ReturnType> name(boolean _set, <ReturnType> _value)` — writable:
  `_set` is false while painting and true when the user types; the method
  returns the value to show. On a FORM the signature carries the datasource
  buffer as well.
- `display`/`edit` and `static` are **mutually exclusive** — xppc:
  *"Conflicting modifiers 'static display'"*. The access modifier is free.
- A display method runs **once per visible row, every refresh** — a query
  inside one multiplies by the row count and is the usual cause of a slow grid.
  Cache it when its inputs change only with the record:
  `[SysClientCacheDataMethodAttribute(true)]` (the platform ships ~2,800 of
  these); a method depending on anything beyond the record must NOT be cached.
- A display method cannot be used in a select/where — it is X++, not SQL. For
  filtering, add a real field or a view.

## Hard rules

- Put logic at the lifecycle point that owns the concern. Validation that lives in
  `write()` runs after the record is already saved.
- Never read `_args.record()` without checking `_args.dataset()` first.
- `research(true)` to refresh, `executeQuery()` to re-shape.
- Never guess a control or data source name — `d365fo get form <Name>` first.
- Never hand-author menu XML without running `d365fo validate metadata` over it;
  the compiler will not tell you the element type is fictional.
- Menu-item labels are label tokens, never literals.
