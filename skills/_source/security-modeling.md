---
id: security-modeling
description: Model D365FO security — the role → duty → privilege → entry-point chain, XDS policies, runtime access checks, and configuration keys as the compile-time counterpart. Invoke when the user asks to create or extend a privilege, duty or role, restrict access to a form or table, add an XDS policy, or gate functionality with a configuration key.
covers: Privilege/duty/role chain, XDS policies, configuration keys
applyTo:
  - "**/AxSecurityPrivilege/**"
  - "**/AxSecurityDuty/**"
  - "**/AxSecurityRole/**"
  - "**/AxSecurityPolicy/**"
  - "**/AxConfigurationKey/**"
appliesWhen: User intent mentions security role, duty, privilege, entry point, access level, XDS, extensible data security, table permissions, field permissions, configuration key, or license code.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Security modelling

> Security in D365FO is a four-level chain. Each level has its own AOT folder, and
> mixing them up produces XML that deploys but grants nothing.

```
Role      (job function, assigned to a user)
  └── Duty        (business function)
        └── Privilege   (one operation)
              └── Entry point   (menu item / service operation / form)
```

## 1. Privileges — always in pairs

Every object worth protecting gets **two** privileges: a read-only *View* variant
and a *Maintain* variant with create/update/delete.

- Entry point = the menu item name, plus its type
  (`MenuItemDisplay` / `MenuItemAction` / `MenuItemOutput`) and an access level:
  `Read | Create | Update | Delete | Correct | View | NoAccess`.
- Table permissions on the privilege give column-level access; field permissions
  handle column masking.

```sh
# View + Maintain pair over a form menu item
d365fo generate privilege FmVehicleView \
  --entry-point FmVehicleListPage --entry-point-type MenuItemDisplay \
  --access Read --label "@Fleet:ViewVehicle" --install-to FleetManagement

d365fo generate privilege FmVehicleMaintain \
  --entry-point FmVehicleListPage --entry-point-type MenuItemDisplay \
  --access Delete --label "@Fleet:MaintainVehicle" --install-to FleetManagement
```

## 2. Duties and roles

- A duty groups privileges for one business task ("Maintain vehicle master").
- A role groups duties for one job function ("Fleet clerk") and is what an
  administrator assigns.
- Extending Microsoft's roles/duties: generate a `SecurityRoleExtension` /
  `SecurityDutyExtension` rather than modifying the standard element.

```sh
d365fo generate duty FmVehicleMaintenanceDuty \
  --privilege FmVehicleMaintain --privilege FmVehicleView \
  --install-to FleetManagement

d365fo generate role FmFleetClerk --duty FmVehicleMaintenanceDuty --install-to FleetManagement
```

## 3. Tracing what a user can actually reach

The chain is only as good as its weakest link, and reading it from XML is
error-prone. Ask the index instead:

```sh
d365fo security role FmFleetClerk --output json          # role → duties → privileges
d365fo security coverage FmVehicleListPage --type Menuitem --output json   # who can reach this?
```

## 4. Runtime checks in X++

```xpp
if (SecurityRights::hasMenuItemAccess(menuItemStr(FmVehicleListPage), MenuItemType::Display))
{
    element.design().visible(true);
}

if (SecurityRights::hasTableAccess(tableNum(FmVehicle), AccessType::Read))
{
    // at least read access to the table
}
```

Use these to **hide** UI the user cannot use — never as the only line of defence.
The privilege is the enforcement point; the check is a courtesy.

## 5. XDS — row-level security

Extensible Data Security policies (`AxSecurityPolicy`) restrict *rows*, not
operations. A policy names a primary table, a query that constrains it, and the
constrained tables the restriction propagates to.

```sh
d365fo generate security-policy FmVehiclePolicy \
  --primary-table FmVehicle --query FmVehicleByDealerQuery \
  --install-to FleetManagement
```

`PrimaryTable` carries the table name; `ConstrainedTable` is a `NoYes` flag, not
a place to put a second table name. Getting that backwards produces a policy the
provider cannot read.

## 6. Configuration keys — the compile-time toggle

Configuration keys and Feature Management solve different problems:

| | Configuration key | Feature Management |
|---|---|---|
| When it takes effect | Compile/deploy time | Runtime |
| Scope | Tables, fields, menu items, security | Code paths |
| Cost of flipping | Recompilation + DB sync | None |

- Tables and fields under a disabled key are **excluded from the database
  schema**, from DMF, and from OData.
- Check at runtime with `isConfigurationKeyEnabled(configurationKeyNum(MyKey))`.
- Keys nest: disabling a parent disables every child.
- License codes gate which keys are available at all (ISV licensing).
- Always compile with your keys **disabled** at least once — code referencing a
  disabled field will not build.
- After changing keys, refresh the entity list in the Data management workspace.
- **For a new feature, prefer Feature Management** — see
  `d365fo knowledge get runtime-frameworks`.

```xpp
if (isConfigurationKeyEnabled(configurationKeyNum(WHSAdvanced)))
{
    this.processAdvancedWarehouse();
}
else
{
    this.processBasicWarehouse();
}
```

## Hard rules

- Each level is its own AOT folder: `AxSecurityPrivilege`, `AxSecurityDuty`,
  `AxSecurityRole`, `AxSecurityPolicy`. `d365fo generate {privilege|duty|role}`
  picks the right one — never point one kind at another's folder.
- Never grant an entry point directly on a role. Roles hold duties; duties hold
  privileges; privileges hold entry points.
- Label every privilege, duty and role — the label is what an administrator sees
  in the security configuration form.
- Never widen a Microsoft privilege in place. Add your own and put it in a duty.
