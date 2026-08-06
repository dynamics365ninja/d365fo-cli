---
id: sysoperation-batch-patterns
description: Scaffold batch jobs, SysOperation triplets (DataContract + Service + Controller), RunBase/RunBaseBatch classes, or data migration scripts in D365 Finance & Operations. Invoke when the user asks to "create a batch job", "scaffold a SysOperation", "create a RunBase class", "build a batch class", "generate a migration script", "migrate data between tables", or "create a scheduled batch".
applyTo:
  - "**/AxClass/**Controller*.xml"
  - "**/AxClass/**Service*.xml"
  - "**/AxClass/**Contract*.xml"
  - "**/AxClass/**RunBase*.xml"
  - "**/AxClass/**Migration*.xml"
  - "**/AxClass/**Batch*.xml"
appliesWhen: User intent mentions batch job, SysOperation, RunBase, RunBaseBatch, scheduled batch, data migration script, DataContract class, batch controller, or runnable class.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Batch and SysOperation patterns

> **Prefer SysOperation** for all new batch jobs. Use `RunBase`/`RunBaseBatch` only
> when extending legacy code that already uses that pattern.

---

## 1. SysOperation — preferred pattern for new batch jobs

SysOperation separates concerns into three classes:

| Class | Role |
|---|---|
| `DataContract` | Parameter bag — `[DataContractAttribute]` + `[DataMemberAttribute]` on each `parmXxx()` |
| `Service` | Business logic — extends `SysOperationServiceBase`, contains the `process()` method |
| `Controller` | Entry point — extends `SysOperationServiceController`, sets menu item and execution mode |

**CLI workflow:**

```sh
# Pre-flight — name collision check
d365fo search class FmInvoiceBatch --output json

# Scaffold all three classes in one command
d365fo generate sysoperation FmInvoiceBatch \
  --param AccountNum:CustAccount \
  --param FromDate:TransDate \
  --param ToDate:TransDate \
  --execution-mode ScheduledBatch \
  --install-to FleetManagement

# Default class names derived from <NAME>:
#   FmInvoiceBatchContract   (DataContract)
#   FmInvoiceBatchService    (Service)
#   FmInvoiceBatchController (Controller)

# Override names if needed
d365fo generate sysoperation FmInvoiceBatch \
  --contract-name FmInvoiceBatchDataContract \
  --service-name  FmInvoiceBatchSvc \
  --controller-name FmInvoiceBatchCtrl \
  --execution-mode ScheduledBatch \
  --install-to FleetManagement
```

**`--execution-mode` values this CLI accepts:** `Synchronous` (default, blocks until done) | `Asynchronous` (requires the controller to be exposed as a service; not a general-purpose fire-and-forget mode) | `ScheduledBatch` (adds to batch framework queue).

The `SysOperationExecutionMode` enum in the platform also has a fourth member, `ReliableAsynchronous` (runs on a batch-server thread with a tracked client poll — commonly used for "fire off and keep a Batch Job History record"), but this CLI's `--execution-mode` flag does not currently emit it; passing it is rejected. If you need `ReliableAsynchronous`, scaffold with `Synchronous` and hand-edit the generated `mainOperation`/`new` code afterward.

**Hard rules:**

- The `process()` method on the Service class is the only method that should contain business logic.
- The Contract class must NOT hold state between calls — it is a simple data transfer object.
- Do NOT use `today()` anywhere in the service — use `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`.
- Never call `ttsbegin` / `ttscommit` in the Contract or Controller — only in the Service's `process()`.

---

## 2. RunBase / RunBaseBatch — legacy pattern

Use only when extending existing code that inherits from `RunBase` or `RunBaseBatch`.

```sh
# Simple RunBase (synchronous dialog)
d365fo generate runbase FmLegacyProcessor \
  --dialog-param FromDate:TransDate \
  --dialog-param AccountNum:CustAccount \
  --install-to FleetManagement

# RunBaseBatch (can be sent to batch queue via canGoBatch)
d365fo generate runbase FmLegacyBatch \
  --batch \
  --dialog-param FromDate:TransDate \
  --install-to FleetManagement
```

**When to prefer SysOperation over RunBase:**

- SysOperation supports `[DataContractAttribute]` serialisation — parameters survive AOS restart.
- SysOperation is unit-testable without a dialog.
- RunBase `pack()`/`unpack()` is fragile — adding a new parameter requires version bumping.

---

## 3. Data migration scripts — runnable classes

Use for one-time data migration during upgrades or post-deployment data fixes.

```sh
# Pre-flight — confirm source and target table structure
d365fo get table FmVehicleOld --output json
d365fo get table FmVehicle    --output json

# Scaffold migration script
d365fo generate migration-script FmVehicleMigration \
  --source-table FmVehicleOld \
  --target-table FmVehicle \
  --batch-size 500 \
  --mode Upsert \
  --install-to FleetManagement
```

**Modes:** `Insert` (default, fails on duplicate) | `Update` (updates existing records) | `Upsert` (insert or update).

---

## 4. Retryable and asynchronous batch

A batch task can opt into automatic retry on transient faults (deadlock, SQL
timeout) by implementing `BatchRetryable` and returning `true` from
`isRetryable()`.

```xpp
/// <summary>
/// Reconciles fleet service charges; safe to re-run, so the batch engine may retry it.
/// </summary>
class FmReconciliationService extends SysOperationServiceBase implements BatchRetryable
{
    public boolean isRetryable()
    {
        return true;
    }

    public void run(FmReconciliationContract _contract)
    {
        ttsbegin;
        this.reconcile(_contract.parmFromDate());
        ttscommit;
    }
}
```

**Hard rules:**

- `isRetryable()` only **opts in**; the framework decides how many times to re-run.
  The task must therefore be **idempotent** — guard with a completed-marker record
  or a RecId watermark if it is not naturally so.
- **Never swallow the transient exception yourself.** Catching it defeats the
  retry you just asked for; let it propagate.
- Run work off the caller's thread by setting
  `controller.parmExecutionMode(SysOperationExecutionMode::ScheduledBatch)` and
  calling `startOperation()` — a form button must not block on a long operation.
- **Never hold an open transaction across an async boundary.** `ttsbegin` /
  `ttscommit` go *inside* the asynchronous unit, not around the scheduling call.
- For genuine parallelism, partition the work and fan it out —
  `d365fo knowledge get performance-and-caching`.

**Hard rules:**

- Always run migration scripts in a test environment before production.
- Use `--batch-size` to avoid long-running transactions. Default is 1000.
- **A runnable class extends nothing.** In D365FO "runnable" is not a base class — it is any plain class with a static `main(Args _args)` entry point, run via **right-click the class → Set as startup object** in Visual Studio (or wrapped in a batch job). Verified against the AOT: no `SysRunnable` type exists, so deriving from it will not compile.
- The scaffolded class follows exactly that shape: `main(Args _args)` constructs the class and calls the instance `run()` method.
- Never delete source data in the same script — use a separate cleanup script after validation.
- After migration, validate row counts: `select count(*) from FmVehicle`.
