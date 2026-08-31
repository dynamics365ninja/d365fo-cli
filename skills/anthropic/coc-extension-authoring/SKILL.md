---
name: coc-extension-authoring
description: Author a Chain-of-Command extension in D365FO without duplicating existing wrappers. Use when the user asks to "wrap a method", "add a CoC", or "modify behavior of standard method".
applies_when: User intent mentions Chain-of-Command, CoC, method wrapping, next() pattern, or non-intrusive extension.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Writing a Chain-of-Command extension safely

> **Source of truth:** [learn:method-wrapping-coc](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/extensibility/method-wrapping-coc).

## Pre-flight (mandatory — ONE call)

```sh
d365fo prepare change <TargetClass> --method <method> --goal "<why>" --output json
```

This single call returns the exact method signature, existing CoC wrappers,
CoC eligibility ([Hookable(false)]/[Wrappable(false)]/final), the recommended
strategy, and a **grounding token**. Do NOT issue separate `get class` /
`find coc` calls for facts it already returned.

If `existingCocExtensions` is non-empty, enumerate them to the user and stop before writing another wrapper. Stacking duplicates risks ordering bugs.

Pass the token to the generator: `d365fo generate coc <TargetClass> --method <method> --install-to <Model> --grounding-token <token>`. For any hand-written wrapper body, run `d365fo validate references <f>` + `d365fo validate xpp <f>` BEFORE writing (the file is a positional argument, not `--file`; omit it to read from stdin) — exit code 2 means hallucinated symbols / BP errors to fix first.

Fallback (prepare unavailable): `d365fo get class <TargetClass>` + `d365fo find coc <TargetClass>::<method>`.

## 🚨 NEVER copy default parameter values into the wrapper

The most common bug — Learn-confirmed:

```xpp
// Base method
class Person
{
    public void salute(str message = "Hi") { … }
}

// ✅ CORRECT — wrapper omits the default value
[ExtensionOf(classStr(Person))]
final class APerson_Extension
{
    public void salute(str message)        // no  "= 'Hi'" here
    {
        next salute(message);
    }
}

// ❌ WRONG — copying the default does not compile
public void salute(str message = "Hi")     // ← forbidden
{
    next salute(message);
}
```

## `next` placement rules

- **Wrapper must call `next` unconditionally** — exception: `[Replaceable]` methods may conditionally break the chain.
- **`next` must sit at first-level statement scope** — NOT inside `if`, `while`, `for`, `do-while`, NOT after `return`, NOT inside a logical expression.
- Platform Update 21+: `next` is permitted inside `try` / `catch` / `finally` (the only nested contexts allowed).
- Platform Update 22+: table and data-entity **system methods** (`insert`,
  `update`, `delete`, `validateWrite`, …) can be wrapped even when the target
  never declared them — the wrapper implicitly wraps the kernel method.
  `d365fo prepare test`/`prepare change` report their exact signatures.
- **On a table wrapper, never re-read the row you already hold.** `this` IS the
  record and `this.orig()` is its pre-image, already in memory — a
  `select … where x.RecId == this.RecId` (or `MyTable::findRecId(this.RecId)`)
  costs a database round trip on every write AND returns the CURRENT stored
  state, not the values this buffer was fetched with. On an insert
  `this.orig().RecId == 0` is the "new record" test (validator rule `COC006`).

```xpp
// ✅ CORRECT
public void doStuff()
{
    next doStuff();         // first-level
    this.afterStuff();
}

// ❌ WRONG — next inside `if`
public void doStuff()
{
    if (this.shouldRun())
        next doStuff();     // forbidden
}
```

## Signature & class shape

- Signature otherwise matches the base **exactly** — same return type, parameter types / order, same `static` modifier. Run `d365fo read class <Target> --method <m> --declaration` and copy.
- Static methods: repeat `static` on the wrapper. Forms cannot be wrapped statically.
- **Cannot wrap constructors.** A new no-arg method on an extension class becomes the *extension class's* own constructor (must be `public`).
- Class shape: `[ExtensionOf(classStr|tableStr|formStr|formDataSourceStr|formDataFieldStr|formControlStr(...))] final class <Target>_<Suffix>`. Class is `final`; name ends with `_Extension` (or descriptive suffix).
- **`[Hookable(false)]`** on a base method blocks CoC and pre/post handlers. Cannot wrap.
- **`[Wrappable(false)]`** blocks wrapping but still allows pre/post handlers. `final` methods need explicit `[Wrappable(true)]` to be wrappable.
- Form-nested wrapping: `formdatasourcestr`, `formdatafieldstr`, `formControlStr`. **Cannot add NEW methods** via CoC on these — only wrap methods that already exist.
- **Visibility:** wrappers can read/call **protected** members of the augmented class (Platform Update 9+). Cannot reach `private`.

## Authoring checklist

- [ ] Pre-flight passes — class + method exist, no duplicate wrapper, signature copied verbatim.
- [ ] `[ExtensionOf(...)]` decorator present.
- [ ] `final class <Target>_Extension`.
- [ ] Default parameter values **omitted** from the wrapper signature.
- [ ] `next <method>(...)` at first-level statement scope on every reachable path.
- [ ] Return type preserved exactly.
- [ ] `/// <summary>` doc comment (BP `BPXmlDocNoDocumentationComments`).

## Scaffold

```sh
d365fo generate coc <TargetClass> --method <method> --install-to <Model>
# or
d365fo generate coc <TargetClass> --method <m1> --method <m2> --out src/MyExt/MyExt_Extension.xml
```

## Post-flight

```sh
d365fo build --output json       # only on user request
d365fo bp check --output json    # only on user request
```

## Hard rules

- Never duplicate an existing wrapper.
- Never copy default parameter values into the wrapper signature.
- Never put `next` inside `if` / `while` / `for` / `do-while` / boolean expressions (PU21+: `try` / `catch` / `finally` only).
- Never remove `next` on a non-`[Replaceable]` method.
- Never wrap a constructor.
- Never hardcode labels — `d365fo labels search` first.


## Rule canon — Chain of Command

<!-- canon:coc -->
**NEVER copy default parameter values into the wrapper signature.** The base
method's defaults are already in effect when `next` runs.

- Wrapper must call `next` unconditionally (exception: `[Replaceable]`).
- `next` at first-level scope — NOT in `if`/`while`/`for`/`do-while`/boolean
  expressions/after `return`. PU21+: `try`/`catch`/`finally` allowed.
- Signature otherwise matches base EXACTLY.
- Static methods: repeat `static`. Forms cannot be wrapped statically.
- Cannot wrap constructors.
- Class shape: `[ExtensionOf(...)] final class <Target>_<Suffix>`.
- `[Hookable(false)]` blocks all CoC + handlers.
- `[Wrappable(false)]` blocks wrapping; allows handlers.
- Form-nested wrapping (`formdatasourcestr`, `formdatafieldstr`,
  `formControlStr`) cannot ADD new methods.
- Wrappers can read `protected` (PU9+); not `private`.
- Reuse existing target/model extension and handler classes before creating new
  ones. If no existing suffix can be derived and the user did not provide one,
  ask; do not create parallel feature-named artifacts unless the user explicitly
  requests that separation.
<!-- /canon -->
