---
description: Author a Chain-of-Command extension in D365FO without duplicating existing wrappers. Use when the user asks to "wrap a method", "add a CoC", or "modify behavior of standard method".
applyTo: '**/AxClass/**_Extension*.xml,**/*_Extension.xpp'
---
# Writing a Chain-of-Command extension safely

> **Source of truth:** [learn:method-wrapping-coc](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/extensibility/method-wrapping-coc).

## Pre-flight (mandatory)

```sh
# 1) Confirm the target class + method + EXACT signature
d365fo get class <TargetClass> --output json
d365fo read class <TargetClass> --method <method> --declaration

# 2) Discover existing CoC wrappers — MUST be empty or coordinated
d365fo find coc <TargetClass>::<method> --output json
```

If `count > 0`, enumerate `items[*].extensionClass` to the user and stop before writing another wrapper. Stacking duplicates risks ordering bugs.

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
```

## `next` placement rules

- **Wrapper must call `next` unconditionally** — exception: `[Replaceable]` methods may conditionally break the chain.
- **`next` must sit at first-level statement scope** — NOT inside `if`, `while`, `for`, `do-while`, NOT after `return`, NOT inside a logical expression.
- Platform Update 21+: `next` is permitted inside `try` / `catch` / `finally` (the only nested contexts allowed).

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
- Never hardcode labels — `d365fo search label` first.
