---
description: Scaffold batch jobs, SysOperation triplets (DataContract + Service + Controller), RunBase/RunBaseBatch classes, or data migration scripts in D365 Finance & Operations. Invoke when the user asks to "create a batch job", "scaffold a SysOperation", "create a RunBase class", "build a batch class", "generate a migration script", "migrate data between tables", or "create a scheduled batch".
applyTo: '**/AxClass/**Controller*.xml,**/AxClass/**Service*.xml,**/AxClass/**Contract*.xml,**/AxClass/**RunBase*.xml,**/AxClass/**Migration*.xml,**/AxClass/**Batch*.xml'
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

**Execution modes:** `Synchronous` (default, blocks until done) | `Asynchronous` (fire-and-forget, returns immediately) | `ScheduledBatch` (adds to batch framework queue).

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

## 3. Data migration scripts — `SysRunnable`

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

**Hard rules:**

- Always run migration scripts in a test environment before production.
- Use `--batch-size` to avoid long-running transactions. Default is 1000.
- The scaffolded class extends `SysRunnable` and exposes a static `main(Args _args)` entry point that constructs the class and calls the instance `run()` method — invoke via **Right-click → Open** in Visual Studio (or wrap the call in a batch/RunBase wrapper). There is no static `SysRunnable::run()` call.
- Never delete source data in the same script — use a separate cleanup script after validation.
- After migration, validate row counts: `select count(*) from FmVehicle`.
