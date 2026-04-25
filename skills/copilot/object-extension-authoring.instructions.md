---
description: Create a Table / Form / Edt / Enum extension (NOT a class CoC extension) in D365 Finance & Operations. Use when the user asks to "extend a table", "add a field to standard CustTable", "extend an EDT", "add an enum value", or "extend a form via FormExtension".
applyTo: '**/AxTableExtension/**,**/AxFormExtension/**,**/AxEdtExtension/**,**/AxEnumExtension/**,**/*.Extension.xml'
---
# Authoring object extensions (Table / Form / Edt / Enum)

> Object extensions are the **non-intrusive** way to add fields to standard
> tables, controls to standard forms, members to standard enums, and tighten
> standard EDTs. Unlike CoC class extensions they do not wrap method calls —
> they merge metadata at compile time.

## When to use which

| Standard object you want to change | Use |
|---|---|
| Add a field/index/relation to `CustTable` | `extension Table CustTable <Suffix>` |
| Add a control / data source / FastTab to a standard form | `extension Form CustTableListPage <Suffix>` |
| Tighten an EDT (e.g. lengthen a string, adjust label) | `extension Edt CustAccount <Suffix>` |
| Add new members to a base enum | `extension Enum NoYes <Suffix>` |
| Add behaviour to a class method | **NOT this** — use `coc-extension-authoring` instead |

## Pre-flight

```sh
# 1) Confirm the target object exists
d365fo get {table|form|edt|enum} <Target> --output json

# 2) Discover existing extensions targeting it (avoid duplicate <Suffix>)
d365fo find extensions <Target> --output json
```

If `count > 0`, list the existing extensions to the user. The naming
convention is `<Target>.<Suffix>` (dot-separated; `Suffix` typically is the
model short-name or the feature name).

## Scaffolding

```sh
# Add fields to standard CustTable in the FleetManagement model
d365fo generate extension Table CustTable Fleet --install-to FleetManagement

# Form extension targeting CustTableListPage
d365fo generate extension Form CustTableListPage Fleet --install-to FleetManagement

# Tighten the CustAccount EDT
d365fo generate extension Edt CustAccount Fleet --install-to FleetManagement

# Add an enum member to NoYes
d365fo generate extension Enum NoYes Fleet --install-to FleetManagement
```

The scaffold emits a minimal `<AxXxxExtension>` element with the
`<Name>Target.Suffix</Name>` shape Visual Studio expects. After scaffolding,
hand-edit the XML to add `<Fields>`, `<Controls>`, `<EnumValues>`, etc.
Re-run `d365fo index refresh --model <Model>` so subsequent
`d365fo get` calls reflect the changes.

## Hard rules

- Never have two extensions with the same `<Target>.<Suffix>` in the same
  model — `d365fo find extensions` first.
- Never use `extension` for class behaviour changes — that is CoC's job
  (`d365fo generate coc <Class>`).
- Never modify the standard object directly (over-layering) — extensions are
  the supported mechanism. Over-layering is reserved for ISVs with explicit
  contractual permission.
- Always pass labels (`@File:Key`) for added fields' captions — never
  hardcoded text (BP `BPErrorLabelIsText`).
- After scaffolding, run `d365fo build` only on user request.
