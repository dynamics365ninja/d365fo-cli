---
description: Trace D365FO security from a Role all the way down to Entry Points, discover which roles reach a given object, or scaffold new security objects (privilege, duty, role, XDS policy). Use when the user asks about permissions, security coverage, roles, duties, privileges, "create a security privilege", "scaffold a security role", "generate a duty", or "create a security policy".
applyTo: '**/AxSecurityRole/**,**/AxSecurityDuty/**,**/AxSecurityPrivilege/**'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Tracing D365FO security hierarchies

## Workflow

1. **Top-down** — which entry points does a role reach? `security coverage`
   is a reverse/bottom-up lookup, not a forward one — `Role` is not a valid
   `--type` for it. Walk the hierarchy explicitly instead:
   ```sh
   d365fo security role <RoleName> --output json       # -> duties[] + privileges[]
   d365fo security duty <DutyName> --output json        # -> privileges[]
   d365fo security privilege <PrivilegeName> --output json  # -> entryPoints[] (objectName + objectType)
   ```

2. **Bottom-up** — which roles reach this object?
   ```sh
   d365fo security coverage <ObjectName> --type MenuItemDisplay --output json
   # --type is required (no default) and matched literally against the indexed
   # AOT ObjectType — confirmed real values, straight from shipped
   # AxSecurityPrivilege XML: MenuItemDisplay | MenuItemAction | MenuItemOutput |
   # ServiceOperation | Table | Form | Report | Class. Entry points are always indexed as one of the
   # specific MenuItem* kinds, never bare "Menuitem" — omitting --type or passing
   # "Menuitem" now fails fast with BAD_INPUT instead of silently returning an
   # empty routes[].
   ```

3. The response contains `routes[*]` of shape
   `{ role, duty, privilege, entryPoint }`. Duplicate `role`s indicate multiple
   paths — all must be removed to revoke access.

## Hard rules

- Do not recommend granting `-System Administrator` for troubleshooting.
- Do not infer; always run `get security` before making claims.
- Report paths verbatim; do not collapse `duty` or `privilege` steps.

## Scaffolding security objects

Trace first — understand the existing hierarchy — then scaffold new objects to fit it.

```sh
# 1. Create a privilege granting a specific access level to a menu item entry point
d365fo generate privilege FmManageVehiclesPrivilege \
  --entry-point FmVehiclesMenuItem --entry-kind MenuItemDisplay \
  --access Update \
  --label "@Fleet:ManageVehicles" \
  --install-to FleetManagement

# 2. Bundle privileges into a duty
d365fo generate duty FmMaintainVehiclesDuty \
  --privilege FmManageVehiclesPrivilege \
  --privilege FmViewCustomersPrivilege \
  --label "@Fleet:MaintainVehicles" \
  --install-to FleetManagement

# 3. Create a role and assign duties
d365fo generate role FmFleetClerkRole \
  --duty FmMaintainVehiclesDuty \
  --label "@Fleet:FleetClerk" \
  --description "Fleet management operator" \
  --install-to FleetManagement

# 4. Merge a duty into an existing role XML after the fact
d365fo generate duty FmNewDuty --privilege FmSomePrivilege \
  --into-role c:/AOT/FleetManagement/AxSecurityRole/FmFleetClerkRole.xml \
  --install-to FleetManagement
```

**Scaffold order:** menu item → privilege (references the menu item) → duty (bundles privileges) → role (bundles duties).
Always verify the new objects appear in the hierarchy afterwards — `d365fo
security coverage <Name> --type Role --output json` always returns an empty
`routes[]` (see Workflow above); instead run `d365fo security role
<RoleName> --output json` to confirm the duties/privileges are attached, and
`d365fo security coverage <EntryPointName> --type MenuItemDisplay --output
json` to confirm the new role reaches the target entry point.
