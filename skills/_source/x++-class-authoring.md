---
id: x++-class-authoring
description: Guidance for authoring or extending X++ classes in D365 Finance & Operations. Invoke whenever the user asks to "create a class", "extend a class", "add a method", or write any X++ that touches CoC.
applyTo:
  - "**/*.xpp"
  - "**/AxClass/**"
  - "**/*Class.xml"
appliesWhen: User intent mentions X++ classes, Chain-of-Command, SysOperation, controller/service patterns, or method overrides.
---

# Authoring X++ classes with the d365fo index

Before you write or modify any X++ class, **ground yourself in the index**. The
`d365fo` CLI replaces guessing with one-shot lookups that never pollute the
conversation with long metadata dumps.

## Workflow

1. **Resolve the base class**
   ```sh
   d365fo search class <namePart> --output json
   d365fo get class <FullName> --output json
   ```
   Read `methods[*].signature` to anchor overrides to the real signatures.

2. **Check for existing Chain-of-Command extensions** before writing a new one:
   ```sh
   d365fo find coc <TargetClass>::<method> --output json
   ```
   If the result has `count > 0`, prefer extending existing logic or coordinate
   with the owning team rather than stacking a duplicate wrapper.

3. **Label lookups** (never hardcode display strings):
   ```sh
   d365fo search label "<free text>" --lang en-us,cs --output json
   ```
   Use the returned `key` (e.g. `@SYS4724`) in your X++ code.

4. **Validate at the end**:
   ```sh
   d365fo review diff          # (when available) — best-practice delta
   d365fo bp check             # (when available) — xppbp.exe runner
   ```

## Hard rules

- **Never** emit X++ that references a field you have not verified with
  `d365fo get table <Name>`.
- **Never** create a CoC wrapper without first running `d365fo find coc`.
- **Prefer** EDTs over primitive types — resolve with `d365fo get edt <Name>`.
- **Expect** a `ToolResult` envelope on every command. On `ok:false`, surface
  `error.message` to the user and stop the task.
