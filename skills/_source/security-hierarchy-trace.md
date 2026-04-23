---
id: security-hierarchy-trace
description: Trace D365FO security from a Role all the way down to Entry Points, or discover which roles reach a given object. Use when the user asks about permissions, security coverage, roles, duties, or privileges.
applyTo:
  - "**/AxSecurityRole/**"
  - "**/AxSecurityDuty/**"
  - "**/AxSecurityPrivilege/**"
appliesWhen: User intent mentions security, roles, duties, privileges, entry points, or access control analysis.
---

# Tracing D365FO security hierarchies

## Workflow

1. **Top-down** — which entry points does a role reach?
   ```sh
   d365fo get security <RoleName> --type Role --output json
   ```

2. **Bottom-up** — which roles reach this object?
   ```sh
   d365fo get security <ObjectName> --type Menuitem --output json
   # type may be Table, Form, Report, Class, Menuitem
   ```

3. The response contains `routes[*]` of shape
   `{ role, duty, privilege, entryPoint }`. Duplicate `role`s indicate multiple
   paths — all must be removed to revoke access.

## Hard rules

- Do not recommend granting `-System Administrator` for troubleshooting.
- Do not infer; always run `get security` before making claims.
- Report paths verbatim; do not collapse `duty` or `privilege` steps.
