---
description: Extend a D365FO form at the right lifecycle point, and wire objects into the navigation menus. Invoke when the user asks where to put form logic (init, executeQuery, active, validateWrite, clicked, modified), how to refresh a grid, how to read caller context, or how to add a menu item or a nested submenu.
applyTo: '**/AxForm/**,**/AxMenu/**,**/AxMenuItemDisplay/**,**/AxMenuItemAction/**,**/AxMenuItemOutput/**'
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

## 3. Menus and menu items

An `AxMenu` is a flat `<Elements>` collection of `AxMenuElement` entries. Which
*kind* of element you write decides whether it works:

| Element type | Purpose | Key field |
|---|---|---|
| `AxMenuElementMenuItem` | reference a display/action/output menu item | `<MenuItemName>` |
| `AxMenuElementSubMenu` | nest **another menu** as a folder ("Inquiries and reports") | `<SubMenu>` |
| `AxMenuElementSeparator` | visual separator | — |
| `AxMenuElementTile` | a tile | — |
| `AxMenuElementMenuReference` | a different, legacy concept — **not** plain nesting | — |

- The submenu field is `<SubMenu>`. It is **not** `<MenuName>` and **not**
  `<MenuItemName>`; `AxMenuElementMenu` is not a type at all. These names are
  verified against `Microsoft.Dynamics.AX.Metadata.dll`.
- A wrong element-type name is **not caught by xppc**. It surfaces only when the
  metadata generation step tries to deserialize the file
  ("cannot be deserialized as AxMenu … no knowledge of any type that maps to this
  name"). `d365fo validate metadata <file>` asks the provider the same question
  offline from the build.
- A submenu is an ordinary `AxMenu` object: build its own items first, then add
  the `AxMenuElementSubMenu` reference from the parent menu.
- The same applies to `AxMenuExtension` nesting into a standard menu.

```sh
d365fo generate menu-item FmVehicleListPageMenuItem --kind Display \
  --object FmVehicleListPage --object-type Form \
  --label "@Fleet:Vehicles" --install-to FleetManagement

# Prove the result deserializes the way the AOS will read it
d365fo validate metadata <path-to-menu-xml> --output json
```

## Hard rules

- Put logic at the lifecycle point that owns the concern. Validation that lives in
  `write()` runs after the record is already saved.
- `research(true)` to refresh, `executeQuery()` to re-shape.
- Never guess a control or data source name — `d365fo get form <Name>` first.
- Never hand-author menu XML without running `d365fo validate metadata` over it;
  the compiler will not tell you the element type is fictional.
- Menu-item labels are label tokens, never literals.
