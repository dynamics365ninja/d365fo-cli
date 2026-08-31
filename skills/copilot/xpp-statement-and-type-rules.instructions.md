---
description: X++ statement-level and type-system rules — switch / break, ternary, no-DB-null sentinels, casting (`as` / `is`), `using` blocks, embedded function declarations. Invoke when writing any non-trivial X++ control flow or type conversion.
applyTo: '**/*.xpp,**/AxClass/**,**/AxTable/**'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# X++ statement & type rules

> **Sources of truth:** [learn:xpp-conditional](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-conditional) and [learn:xpp-variables-data-types](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-variables-data-types).

## Operators & precedence

- **TRAP: `&&` and `||` have EQUAL precedence** and evaluate left-to-right —
  `a || b && c` means `(a || b) && c`, NOT `a || (b && c)` as in C#. Always
  parenthesize mixed `&&`/`||` chains.
- Precedence (high→low): unary (`-` `~` `!`) → `*` `/` `div` `mod` `<<` `>>`
  `&` `^` → `+` `-` `|` → relational (`<` `<=` `==` `!=` `>` `>=` `like` `as`
  `is`) → `&&` and `||` (equal) → `?:`.
- `div` = integer division, `mod` = remainder — **keywords**, not `/` and `%`:
  `7 div 2 == 3`, `7 mod 2 == 1`. Plain `/` always divides as real, even
  between ints.
- `++` and `--` are **statements** with no prefix/postfix value distinction —
  `int y = i++;` is a syntax error; increment on its own line.
- Assignment operators: `=` `+=` `-=` `*=` `/=` (xppc-verified — `*=` and `/=`
  do compile, contrary to the language reference). There is no `%=`, no `<<=`
  and no `??=`.
- `like` matches SQL-style wildcards (`*` any run, `?` one character) — in
  `where` clauses (translated to SQL) and on `str` values in code.
- String concatenation is `+`; there is **no** string interpolation — use
  `strFmt("%1 / %2", a, b)`.
- Implicit conversions are narrower than they look: int→real, int→int64,
  int↔enum, int↔boolean compile, but **real→int is a compile error** ("loses
  range and precision") and so are int→str, str→int, date→int, enum→str.
  Convert explicitly (`real2int`, `int2Str`, `any2Str`, …).

## `switch` / `break`

- **`break` is required** at the end of every `case`. Implicit fall-through compiles but is misleading.
- **`default:` must be the LAST case item** — a `case` after `default` is a compile error.
- `case` accepts non-constant expressions (variables, calls) — evaluated top to bottom.
- To match multiple values to a single branch use the **comma-list** form — never empty fall-through:

```xpp
// ✅ CORRECT
switch (mod(year, 4))
{
    case 13, 17, 21:
        result = "leap-adjacent";
        break;
    default:
        result = "ordinary";
        break;
}
```

## Ternary

`cond ? a : b` — both branches must have the same type. No implicit widening of `int` ↔ `real`.

## ❗ X++ has NO database null

Each primitive has a "null-equivalent" sentinel:

| Type | Null-equivalent value |
|---|---|
| `int` / `int64` | `0` |
| `real` | `0.0` |
| `str` | `""` |
| `date` | `1900-01-01` (`dateNull()`) |
| `utcDateTime` | date-part `1900-01-01` (`utcDateTimeNull()`) |
| `enum` | element with value `0` |
| `boolean` | `false` |
| `RecId` | `0` |

In SQL `where` clauses these compare as **false** (rows with sentinel values are NOT returned by `where field`). In plain expressions they are ordinary values.

```xpp
// ❌ WRONG — there is no null
if (myDate == null) { … }

// ✅ CORRECT
if (!myDate)              { … }   // boolean test on sentinel
if (myDate == dateNull()) { … }   // explicit
```

Same for `utcDateTime` — compare against `utcDateTimeNull()` or use `if (!myUtc)`.

## Casting

- Prefer **`as`** (returns `null` on type mismatch) and **`is`** (boolean test) over hard down-casts.
- Hard down-casts (`(SubClass)objectExpr`) on object-typed expressions throw `InvalidCastException` on mismatch.
- Late binding exists for `Object` and `FormRun` only — accept the runtime cost if you use it.

```xpp
common = ledgerJournalTrans;
LedgerJournalTrans trans = common as LedgerJournalTrans;
if (trans) { trans.update(); }
```

## `using` blocks for IDisposable

Equivalent to `try` + `finally { x.Dispose(); }` but shorter and exception-safe.

```xpp
using (var reader = new StreamReader(path))
{
    line = reader.ReadLine();
}
```

## Embedded function declarations

Local functions inside a method **can read** variables declared earlier in the enclosing method but **cannot leak** their own variables out. Prefer them over a private helper method only when the helper truly does not belong to the class API.

## Hard "never" list

- **Never** test `myDate == null` — there is no null in X++.
- **Never** rely on switch fall-through — always `break` (or use the comma-list form).
- **Never** down-cast an `Object` without an `is` guard (or an `as` + null check).


## Rule canon — statements and types

<!-- canon:statements -->
- `switch break` required. Multi-value: `case 13, 17, 21:`.
- Ternary branches must share the same type.
- **X++ has NO database null.** Sentinels: `int 0`, `real 0.0`, `str ""`,
  `date 1900-01-01` (`dateNull()`), `utcDateTime` date-part `1900-01-01`,
  `enum 0`. Test `if (!myDate)` or `if (myDate == dateNull())`. NEVER
  `if (myDate == null)`.
- Casting: prefer `as`/`is` over hard down-cast. Late binding only on
  `Object` / `FormRun`.
- `using` blocks for `IDisposable`.
- Embedded function declarations: read-only access to enclosing locals.
<!-- /canon -->
