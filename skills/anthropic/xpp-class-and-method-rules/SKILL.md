---
name: xpp-class-and-method-rules
description: Rules for X++ class declarations, method modifiers, parameter passing, constructors, and extension methods (separate from CoC). Invoke when authoring new classes, overrides, factory methods, or extension methods.
applies_when: User intent involves declaring an X++ class, choosing access modifiers, designing a constructor, overriding a method, or creating extension methods.
---
# X++ class & method authoring rules

> **Source of truth:** [learn:xpp-classes-methods](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-classes-methods).
> **Pre-flight:** `d365fo get class <Name> --output json` for the base class signatures and `d365fo find usages` before any refactor.

## Class-level rules

- **Default class access = `public`.** Removing `public` does not make a class non-public. Use:
  - `internal` to scope to the same model.
  - `final` to prevent extension by inheritance (enables CoC instead).
  - `abstract` for base-only types (cannot mix with `final` / `static`).
- **Instance fields default = `protected`.** **NEVER make instance fields `public`.** Expose state via `parmFoo` accessors. Public fields tightly couple consumers to internal layout and break encapsulation.
- **Constructor pattern:** one `new()` per class (compiler generates an empty default if absent). Convention:
  - `protected void new()` — internal use only.
  - `public static MyClass construct()` — factory entry point.
  - `protected void init(...)` — post-construction setup.

## Method modifier order

```
[edit | display] [public | protected | private | internal] [static | abstract | final]
```

- `static final` is permitted; `abstract` cannot mix with `final` / `static`.
- **Override visibility rule:** an override must be at least as accessible as the base method. `public` → `public` only; `protected` → `public` or `protected`; `private` → not overridable.

## Parameters

- **Optional parameters** must come after all required parameters. Callers cannot skip — every preceding parameter must be supplied.
- Use `prmIsDefault(_x)` inside a `parmX(_x = x)` accessor to detect "was this caller-supplied?".
- **All parameters are pass-by-value.** Mutating a parameter inside the method does NOT affect the caller's variable. Return modified state explicitly or wrap in an object.

## `this` rules

- Required (or qualified) for instance method calls.
- **Cannot** qualify class-declaration member variables — write the bare name.
- **Cannot** be used in a `static` method.
- **Cannot** qualify static methods — use `ClassName::method()`.

## Extension methods (NOT CoC — these are *adders*)

Targets: Class / Table / View / Map.

- Extension class must be `static` (not `final`); name ends with `_Extension`.
- Every extension method is `public static`.
- **First parameter is the target type** — the runtime supplies the receiver; the caller does not pass it.

```xpp
public static class CustTable_Extension
{
    public static AmountMST balanceWithBuffer(CustTable _custTable, AmountMST _buffer)
    {
        return _custTable.balanceMST() + _buffer;
    }
}

// Caller — first param is omitted:
amount = custTable.balanceWithBuffer(1000);
```

## Constants & locals

- **Constants over macros.** `public const str FOO = 'bar';` at class scope (cross-referenced, scoped, IntelliSense-aware) instead of `#define.FOO('bar')`. Reference via `ClassName::FOO`.
- **`var` keyword** for type-inferred locals when the type is obvious from the right-hand side (`var sum = decimal + amount;`). Skip `var` when the RHS is non-obvious — readability beats brevity.
- **Declare-anywhere encouraged** — declare close to first use, smallest scope. The compiler rejects shadowing of outer-scope variables with the same name.

## Hard "never" list

- **Never** make instance fields `public`.
- **Never** call `[SysObsolete]` methods — read the attribute message for the replacement.
- **Never** skip `/// <summary>` doc comments on public/protected members (BP `BPXmlDocNoDocumentationComments`).
- **Never** override a method without `d365fo get class <Base>` to confirm the exact signature.

## Pre-flight commands

```sh
d365fo get class <Class> --output json                 # methods, attributes, signatures
d365fo read class <Class> --method <m> --declaration   # exact return type & params
d365fo find usages <method> --output json              # caller risk
```
