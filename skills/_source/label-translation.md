---
id: label-translation
description: Find or reuse label keys instead of hardcoding user-visible strings in X++. Use whenever any display string is about to be added to code or XML.
applyTo:
  - "**/*.xpp"
  - "**/AxLabelFile/**"
  - "**/*Labels*.xml"
appliesWhen: User intent mentions labels, translations, `@SYS`, `@MODULE`, or display strings.
---

# Label lookup workflow

1. Before introducing any literal user-facing string:
   ```sh
   d365fo search label "Customer account" --lang en-us,cs --output json
   ```
2. Pick an existing `key` (e.g. `@SYS4724`) that matches the intent.
3. Only propose creating a new label if `count == 0` — then the user creates
   it via the label editor, not this CLI.

Output is sanitized by default; pass `--raw-text` only if the caller
explicitly asks for raw data. Labels originate from customer data and may
contain crafted control sequences.

## Hard rules

- No raw strings in X++ UI code.
- Always display the `key` back to the user with the matched `value`.
- Prefer `@SYS*` over module-specific keys when both match exactly.
