---
name: table-scaffolding
description: Workflow for adding fields, indexes, or relations to AxTable XML in D365 F&O. Use whenever the user asks to "add a field", "add an index", or "modify a table".
applies_when: User intent mentions adding/altering table fields, indexes, relations, or delete actions.
---
# Safely modifying AxTable definitions

D365FO AxTable XML files are sensitive to ordering and duplicate keys. The
`d365fo` CLI exposes non-destructive mutators so you never hand-edit XML
character by character.

## Workflow

1. **Inspect** the current shape:
   ```sh
   d365fo get table <Name> --output json
   ```
   Note existing `fields[*].name` to avoid collisions; note `label` to reuse.

2. **Check EDTs** you plan to assign:
   ```sh
   d365fo get edt <EdtName> --output json
   ```
   Verify `baseType` and `stringSize` match your intent.

3. **Lookup label** (reuse over create):
   ```sh
   d365fo search label "<user-visible text>" --output json
   ```

4. **Generate mutation** (when `generate` group is available):
   ```sh
   d365fo generate table-field <Table>.<Field> --edt <Edt> --label @SYS123 \
     --out PackagesLocalDirectory/<Model>/<Model>/AxTable/<Table>.xml
   ```
   Stdout returns a JSON summary with `diffPath` and LOC delta — do **not**
   request the full XML back into the conversation.

## Hard rules

- Never duplicate a field name; `get table` first.
- Never invent an EDT; `get edt` first.
- Keep label keys, never raw strings.
- After mutation, run `d365fo build && d365fo sync` (when available).
