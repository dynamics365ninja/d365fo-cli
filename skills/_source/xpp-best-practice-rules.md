---
id: xpp-best-practice-rules
description: Best-practice (BP) rules every generated X++ file must satisfy — today/DateTimeUtil, label-typed messages, EDT migration, nested loops, alternate keys, doc comments, label existence. Invoke whenever you scaffold or edit X++ that will eventually be linted by xppbp.exe.
applyTo:
  - "**/*.xpp"
  - "**/AxClass/**"
  - "**/AxTable/**"
  - "**/AxForm/**"
appliesWhen: User intent involves authoring X++ that must pass `d365fo bp check` / xppbp.exe — i.e. virtually all D365FO code.
---

# Best-practice rules — generated X++ must be BP-clean

> **Source of truth:** [`d365fo bp check`](../../docs/EXAMPLES.md) — the Windows-VM runner that executes `xppbp.exe`. The list below covers the non-negotiable BP rules every scaffold and hand-edit must satisfy out of the box.

## Per-rule rules

### `BPUpgradeCodeToday` — `today()` is forbidden

`today()` ignores the user's preferred time-zone. Always use:

```xpp
TransDate today = DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone());
```

### `BPErrorLabelIsText` — no hardcoded UI strings

Every string passed to `info()` / `warning()` / `error()` / `Box::yesNo()` etc. must be a label token of the form `@File:Key`. Search before you create:

```sh
d365fo search label "Vehicle is required" --lang en-us --output json
d365fo resolve label @SYS12345                          # confirm an existing token
```

If no match → create the label via your model's labels file, then reference it. Never inline.

### `BPErrorEDTNotMigrated` — modern EDT relations

EDT relations must use the `EDT.Relations` element, **not** the legacy table-level relations on the EDT. The CLI's `d365fo generate edt` and `d365fo generate extension edt` already emit the modern shape — preserve it when hand-editing.

### `BPCheckNestedLoopinCode` — no nested data-access loops

Nested `while select` blocks are forbidden:

```xpp
// ❌ WRONG
while select custTable
{
    while select custInvoiceJour where custInvoiceJour.OrderAccount == custTable.AccountNum
    { … }
}

// ✅ CORRECT — single join
while select custTable
    join custInvoiceJour
    where custInvoiceJour.OrderAccount == custTable.AccountNum
{ … }
```

For *filter-only* joins use `exists join` / `notExists join`. For complex correlations pre-load to a `Map` or temp table.

### `BPCheckAlternateKeyAbsent` — every table needs an alternate key

A unique index on the natural key, marked `AlternateKey = Yes`. The CLI's `d365fo generate table` template emits a `<Pkey>` index with `AlternateKey = Yes` — don't strip it.

### `BPErrorUnknownLabel` — labels referenced must exist

`@File:Key` tokens must resolve to a real entry in an indexed label file. Confirm with:

```sh
d365fo resolve label @File:Key --lang en-us,cs --output json
```

If the result is `ok:false` with `LABEL_NOT_FOUND`, **stop** and either pick an existing label (`d365fo search label …`) or add the entry to the model's labels file before referencing it.

### `BPXmlDocNoDocumentationComments` — meaningful doc comments

Public/protected classes and methods need a non-trivial `/// <summary>`:

```xpp
/// <summary>Calculates the customer balance in the company currency.</summary>
/// <param name="_includeOpen">Whether open transactions count.</param>
/// <returns>Balance in MST.</returns>
public AmountMST balanceMST(boolean _includeOpen = true) { … }
```

Auto-generated stubs ("This method does foo.") do **not** count. Restate the contract.

### `BPDuplicateMethod` — no dupes on the inheritance chain

Adding a method that already exists on a base class in the same model fails BP. Run `d365fo get class <Base>` to confirm before adding.

## Label-on-field exception

When adding a field whose **EDT** already carries a label, do **NOT** set `--label` on the field — it inherits from the EDT. Override only if you deliberately want a different caption in this table:

```sh
d365fo generate table FmVehicle \
    --field VIN:VinEdt:mandatory      \   # ← VinEdt has Label = "VIN" — leave it alone
    --field Make:Name                  \   # ← inherits "Name" from EDT
    --label "@Fleet:Vehicle"
```

## Linting workflow

```sh
# In-process heuristics — fast, runs anywhere, useful for CI:
d365fo lint --output sarif > lint.sarif

# Full BP — only on the Windows VM, only on user request:
d365fo bp check --output json
```

**Never** auto-run `bp check`. It blocks the user (slow, Windows-only). Say *"Changes scaffolded. Run `d365fo bp check` when you're ready."*

## Hard "never" list

- **Never** call `today()`.
- **Never** hardcode a UI string in `info()` / `warning()` / `error()`.
- **Never** nest `while select` blocks.
- **Never** ship a table without an alternate key.
- **Never** reference a label without verifying it exists.
- **Never** auto-run `d365fo bp check`.
