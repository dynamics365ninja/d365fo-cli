---
id: transactions-and-concurrency
description: Scope transactions and survive concurrency in D365FO — ttsbegin/ttscommit rules, optimistic concurrency and UpdateConflict retry, UnitOfWork, and the error-handling patterns that go with them. Invoke when the user asks about transactions, tts levels, locking, update conflicts, retry, or where to put try/catch.
covers: tts scoping, OCC retry, `UnitOfWork`, error handling
applyTo:
  - "**/*.xpp"
  - "**/AxClass/**"
  - "**/AxTable/**"
appliesWhen: User intent mentions ttsbegin, ttscommit, ttsabort, transaction, commit, rollback, locking, forUpdate, pessimisticlock, optimisticlock, OCC, RecVersion, UpdateConflict, deadlock, retry, UnitOfWork, or try/catch placement.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Transactions, concurrency and errors

## 1. Transaction scoping

- `ttsbegin` and `ttscommit` are **reference-counted**: nesting is legal, and only
  the outermost `ttscommit` writes.
- **Always pair them.** An unbalanced pair is a runtime crash, not a warning.
- **Never put `try`/`catch` *inside* a `ttsbegin`…`ttscommit` block.** By the time
  the `catch` runs the transaction has already been rolled back, so the handler
  cannot repair anything and the following `ttscommit` fails on a tts-level
  mismatch. The handler belongs **outside** the block.
- `ttsabort` is for unrecoverable situations, never for normal control flow.
- `forUpdate` on the `select` before any `.update()` / `.delete()`.
- Set-based operators run in an implicit transaction when not explicitly scoped.
- Keep the block short: its length is the width of the conflict window.

```xpp
// ✅ handler outside the transaction
try
{
    ttsbegin;
    select forUpdate optimisticlock custTable
        where custTable.AccountNum == _accountNum;
    custTable.CreditMax += 1000;
    custTable.update();
    ttscommit;
}
catch (Exception::UpdateConflict)
{
    // recoverable — see the retry pattern below
}
```

```xpp
// ❌ handler inside the transaction: the tts block is already broken
ttsbegin;
try
{
    custTable.update();
}
catch (Exception::Error)
{
    // nothing to salvage here
}
ttscommit;   // crashes on a tts-level mismatch
```

## 2. Optimistic concurrency (OCC)

- OCC is on by default (`OccEnabled = Yes` on the table). A `select` takes no
  update lock; the lock is taken at update time.
- The kernel bumps a hidden `RecVersion` column on every update. If it changed
  between your read and your write, you get `Exception::UpdateConflict`.
- The modern pattern is `select forUpdate optimisticlock` plus a retry: catch the
  conflict, `reread()`, re-apply, retry. When the retries run out the kernel
  throws `Exception::UpdateConflictNotRecovered`.
- `pessimisticlock` takes the lock at select time and blocks other writers. Use it
  only for genuine hotspots where retry churn costs more than blocking.
- **Never disable `OccEnabled` to "avoid" conflicts.** It serialises writers and
  costs throughput; fix the retry instead.

```xpp
#OCCRetryCount

try
{
    ttsbegin;
    select forUpdate optimisticlock custTable
        where custTable.AccountNum == _accountNum;

    custTable.CreditMax += 1000;
    custTable.update();
    ttscommit;
}
catch (Exception::UpdateConflict)
{
    if (appl.ttsLevel() == 0)
    {
        if (xSession::currentRetryCount() >= #RetryNum)
        {
            throw Exception::UpdateConflictNotRecovered;
        }
        retry;   // the kernel re-reads and re-runs the tts block
    }
    throw Exception::UpdateConflict;
}
```

## 3. UnitOfWork — coordinated multi-table writes

`UnitOfWork` batches related inserts, updates and deletes into one transaction and
**orders them by their relations**, so the parent is inserted before the child.
Prefer it over hand-ordered inserts across related tables.

```xpp
UnitOfWork uow = new UnitOfWork();
FmVehicle     vehicle = new FmVehicle();
FmVehicleLine line    = new FmVehicleLine();

// Registered in any order; UnitOfWork commits them in dependency order.
uow.insertOnSaveChanges(vehicle);
uow.insertOnSaveChanges(line);
uow.saveChanges();
```

## 4. Errors

| Call | Behaviour |
|---|---|
| `info` / `warning` / `error` | post to the infolog |
| `checkFailed` | posts an error **and returns `false`** — the `validateWrite` / `validateField` idiom |
| `throw Exception::Error` | aborts the current transaction |

- Every message is a **label token**. A literal fails `BPErrorLabelIsText`.
- Accumulate validation results so the user sees every problem at once:
  `ret = ret && this.checkX();`
- Catch **specific** exceptions. A bare `catch` hides `UpdateConflict` and
  `CLRError`, both of which need different handling.
- `Exception::CLRError` is the only way to catch a .NET failure; pull the detail
  from `CLRInterop::getLastException()`. `Exception::Error` will not catch it.
- **Never swallow an exception silently.** If you opted into batch retry
  (`BatchRetryable`), let the transient exception propagate — catching it defeats
  the retry.

```xpp
public boolean validateWrite()
{
    boolean ret = super();

    if (!this.VehicleId)
    {
        ret = checkFailed("@Fleet:VehicleIdRequired") && ret;
    }

    if (this.Mileage < 0)
    {
        ret = checkFailed("@Fleet:MileageNegative") && ret;
    }

    return ret;
}
```

## 5. Deprecated APIs still seen in this area

| Legacy | Replacement |
|---|---|
| `today()` | `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())` — `BPUpgradeCodeToday` |
| `infolog.add()` | `info()` / `warning()` / `error()` |
| `RunBase` | SysOperation (`d365fo knowledge get sysoperation-batch-patterns`) |
| AIF services | data entities + OData |
| form `display`/`edit` methods | computed columns or entity virtual fields |

A method carrying `[SysObsolete]` names its replacement in the attribute message —
read it, and **never call the obsolete method**. `d365fo get class <Name> --output json`
shows the attribute before you commit to a call.

## Hard rules

- `try`/`catch` outside the tts block. Always.
- `forUpdate` before any write; `optimisticlock` plus retry as the default.
- Never call a function inside a `where` clause — assign to a local first (`SEL005`).
- Never nest `while select` (`SEL004`); use `join` / `exists join`.
- One transaction per logical unit of work — not one per row, and not one per
  batch of 100 000 rows.
