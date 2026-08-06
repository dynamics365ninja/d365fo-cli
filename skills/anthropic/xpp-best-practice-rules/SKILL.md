---
name: xpp-best-practice-rules
description: Best-practice (BP) rules every generated X++ file must satisfy — today/DateTimeUtil, label-typed messages, EDT migration, nested loops, alternate keys, doc comments, label existence. Invoke whenever you scaffold or edit X++ that will eventually be linted by xppbp.exe.
applies_when: User intent involves authoring X++ that must pass `d365fo bp check` / xppbp.exe — i.e. virtually all D365FO code.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

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
d365fo labels search "Vehicle is required" --lang en-us --output json
d365fo labels resolve @SYS12345                          # confirm an existing token
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

A unique index on the natural key, marked `AlternateKey = Yes`. The CLI's `d365fo generate table` template emits a `PrimaryIdx` index with `<AlternateKey>Yes</AlternateKey>` — don't strip it.

### `BPErrorUnknownLabel` — labels referenced must exist

`@File:Key` tokens must resolve to a real entry in an indexed label file. Confirm with:

```sh
d365fo labels resolve @File:Key --lang en-us,cs --output json
```

If the result is `ok:false` with `LABEL_NOT_FOUND`, **stop** and either pick an existing label (`d365fo labels search …`) or add the entry to the model's labels file before referencing it.

### `BPXmlDocNoDocumentationComments` — meaningful doc comments

Public/protected classes and methods need a non-trivial `/// <summary>`:

```xpp
/// <summary>Calculates the customer balance in the company currency.</summary>
/// <param name="_includeOpen">Whether open transactions count.</param>
/// <returns>Balance in MST.</returns>
public AmountMST balanceMST(boolean _includeOpen = true) { … }
```

Auto-generated stubs ("This method does foo.") do **not** count. Restate the contract.

### No dupes on the inheritance chain

Adding a method that already exists on a base class in the same model is rejected by the compiler as an invalid re-declaration (not a specific named BP rule — there is no `BPDuplicateMethod` rule in the shipped Best Practice rule assemblies). Run `d365fo get class <Base>` to confirm before adding.

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
d365fo lint --format sarif > lint.sarif

# Run specific method-flag categories (detected at index-extract time):
d365fo lint --category today-usage          # BPUpgradeCodeToday: today() calls
d365fo lint --category do-insert-update     # doInsert/doUpdate/doDelete usage
d365fo lint --category doc-comment-missing  # BPXmlDocNoDocumentationComments

# Full BP — only on the Windows VM, only on user request:
d365fo bp check --output json
```

Sixteen categories are now available: `table-no-index`, `ext-named-not-attributed`, `string-without-edt`, `today-usage`, `do-insert-update`, `doc-comment-missing`, `nested-select`, `insert-in-loop`, `tts-try-catch`, `empty-table-method`, `batch-no-cango`, `force-literals`, `public-instance-field`, `cache-lookup-mismatch`, `missing-delete-action`, `no-alternate-key`. The method-flag categories are populated at extract time by scanning `<Source>` text — no full body is stored. Re-run `d365fo index refresh` after editing source before linting.

**Never** auto-run `bp check`. It blocks the user (slow, Windows-only). Say *"Changes scaffolded. Run `d365fo bp check` when you're ready."*

## Hard "never" list

- **Never** call `today()`.
- **Never** hardcode a UI string in `info()` / `warning()` / `error()`.
- **Never** nest `while select` blocks.
- **Never** ship a table without an alternate key.
- **Never** reference a label without verifying it exists.
- **Never** auto-run `d365fo bp check`.

## CLI object-discovery best practices

When the user asks a **functional** question — "which classes print a free invoice", "which classes process sales orders" — translate it into **2–4 English keyword fragments** and run a single targeted batch search. Always add `--kind class` (or the relevant kind) to avoid scanning all 13 object types.

```sh
# ✅ CORRECT — kind-filtered, fast
d365fo search batch FreeTextInvoice PrintFreeTxt FreeInv --kind class --output json

# ❌ WRONG — full scan across Tables/Classes/EDTs/Enums/Forms/… slow and noisy
d365fo search batch FreeTextInvoice PrintFreeTxt FreeInv --output json
```

- Use **at most 4 fragments** per call. More fragments add tokens, not precision.
- Use the **English AOT name** (`FreeTextInvoice`, not `volná faktura`).
- If the result set has >50 hits, refine with a tighter fragment instead of adding more queries.
- After identifying candidate names, resolve details with `d365fo get class <Name> --output json`.


## Rule canon — non-negotiables

<!-- canon:core -->
1. NEVER guess method signatures — `d365fo get class <Name>` first.
2. NEVER use `today()` — use `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`.
3. NEVER call functions in `where` — assign to a local first.
4. NEVER hardcode strings in `info()`/`warning()`/`error()`. Search labels first.
5. NEVER nest `while select` — use `join` / `exists join` / `notExists join`.
6. EDT-label exception: when adding a field whose EDT carries a label, do NOT
   set `--label` on the field — it inherits.
7. ALWAYS write meaningful `/// <summary>` on public/protected members.
8. NEVER call `[SysObsolete]` methods.
9. NEVER make instance fields `public` — default `protected`; expose via `parmFoo`.
10. NEVER `doInsert`/`doUpdate`/`doDelete` for normal logic — migration only.
11. Standard data events: `[DataEventHandler]`, NOT `[SubscribesTo + delegateStr]`.
    `delegateStr` is for *custom* delegates only.
12. NEVER pass `tableGroup="TempDB"`. `TableGroup` is business role
    (`Main` / `Transaction` / `Parameter` / `WorksheetHeader` / `WorksheetLine`
    / `Reference` / `Framework` / `Group` / `Miscellaneous`). `TableType` is
    storage (`RegularTable` / `TempDB` / `InMemory`). Temp tables:
    `tableType=TempDB`, `tableGroup=Main`.
13. Class member variables go INSIDE the class `{ }`; methods at top level.
<!-- /canon -->

## Rule canon — must pass `d365fo bp check`

<!-- canon:bp -->
- `BPUpgradeCodeToday` — never `today()`.
- `BPErrorLabelIsText` — `info`/`warning`/`error` need labels.
- `BPErrorEDTNotMigrated` — modern `EDT.Relations` element only.
- `BPCheckNestedLoopinCode` — no nested `while select`.
- `BPCheckAlternateKeyAbsent` — every table needs a unique alternate key.
- `BPErrorUnknownLabel` — referenced labels must exist.
- `BPXmlDocNoDocumentationComments` — meaningful `/// <summary>`.
- `BPDuplicateMethod` — no duplicates on the inheritance chain.
<!-- /canon -->
