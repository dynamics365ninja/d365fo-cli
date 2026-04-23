---
description: Author a Chain-of-Command extension in D365FO without duplicating existing wrappers. Use when the user asks to "wrap a method", "add a CoC", or "modify behavior of standard method".
applyTo: '**/AxClass/**_Extension*.xml,**/*_Extension.xpp'
---
# Writing a Chain-of-Command extension safely

## Pre-flight (mandatory)

```sh
# 1) Confirm the target class and method exist
d365fo get class <TargetClass> --output json

# 2) Discover existing CoC wrappers — MUST be empty or coordinated
d365fo find coc <TargetClass>::<method> --output json
```

If `count > 0`, enumerate `items[*].extensionClass` to the user and stop before
writing another wrapper. Explain: "There are already N wrappers; stacking a new
one risks ordering bugs."

## Authoring checklist

- `[ExtensionOf(classStr(<TargetClass>))]` decorator.
- `final class <TargetClass>_Extension`.
- `next <method>(...)` invocation on every path; never drop the call unless
  deliberately blocking the base behavior.
- Preserve the exact return type from `d365fo get class` response.

## Post-flight

```sh
d365fo build --output json
d365fo bp check --output json
```

## Hard rules

- Never duplicate an existing wrapper.
- Never remove `next` without explicit user instruction.
- Never hardcode labels — `d365fo search label` first.
