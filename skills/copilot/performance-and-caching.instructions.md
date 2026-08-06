---
description: Make D365FO code fast and keep it fast — set-based operations, indexes, the table CacheLookup modes, SysGlobalObjectCache and RecordViewCache, temp-table choice, and parallel batch fan-out. Invoke when the user asks why something is slow, how to cache, how to process a large volume, or which temp table type to use.
applyTo: '**/*.xpp,**/AxTable/**,**/AxClass/**'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Performance and caching

> Order of attack, cheapest first: stop looping, cover the `where` clause with an
> index, cache what is stable, then parallelise. Caching a slow query is the most
> common way to make a problem harder to find.

## 1. Stop looping

- Set-based first: `insert_recordset`, `update_recordset`, `delete_from`. One
  round trip instead of N.
- `RecordInsertList` when the rows must be constructed in X++ before insert.
- `exists join` / `notExists join` when you need no columns from the joined table.
- **Never nest `while select`** — flatten to one statement with joins (`SEL004`).
- `firstOnly` / `firstFast` on single-record lookups.
- Never call a function inside a `where` clause; assign to a local first (`SEL005`).

## 2. Indexes

Every field in a `where` clause should be covered by an index, in the order the
query uses it. Check what exists before adding one:

```sh
d365fo get table FmVehicle --output json | jq '.data.indexes'
d365fo find relations FmVehicle --output json
```

A table also needs a unique alternate key (`BPCheckAlternateKeyAbsent`), which
doubles as the OData `$key` and the dual-write match key.

## 3. Record caching — `CacheLookup`

| Value | Behaviour | Use for |
|---|---|---|
| `None` | no caching | volatile transaction tables |
| `NotInTTS` | cache outside a transaction, re-read inside it | rows read then updated |
| `Found` | cache rows found by a **unique-index** lookup | master/reference tables (most common) |
| `FoundAndEmpty` | also caches "not found" | lookups that miss often |
| `EntireTable` | whole table in a per-AOS cache | **only** small, rarely-changing reference tables |

- Record caching only engages for a select on the **whole** primary/unique index,
  every key field compared with `==`. A partial key or a range bypasses it
  entirely — which is why "I set CacheLookup and nothing changed" is usually
  correct behaviour.
- A single insert/update/delete **flushes the `EntireTable` cache cluster-wide**.
  On a table with any write traffic that is a net loss.
- `NotInTTS` guarantees a fresh row before an update inside a transaction.

## 4. Explicit caches

- **`SysGlobalObjectCache`** — an explicit server-side key/value cache across
  sessions. `find(owner, key, value, scope)` / `insert(...)`, with the scope
  choosing DataArea vs Global. Use it for expensive computed or configuration
  values. **Never** for volatile transactional data, and never with `Global`
  scope for anything user- or company-specific — that leaks across tenants of the
  same AOS.
- **`RecordViewCache`** — pre-loads a record set into memory once so a tight loop
  re-reading the same working set stops hitting SQL. Construct it from a
  `select forUpdate` buffer before the loop.
- **Display/edit methods** — mark expensive ones
  `[SysClientCacheDataMethodAttribute(true)]` so the client caches the value
  instead of round-tripping on every repaint.

```xpp
SysGlobalObjectCache goc    = classFactory.globalObjectCache();
container            result = goc.find('FleetManagement', [_configKey]);

if (!result)
{
    result = [FmRateCalculator::run(_configKey)];
    goc.insert('FleetManagement', [_configKey], result);
}
```

```xpp
FmVehicle vehicle;
vehicle.VehicleGroupId = _group;

// Pre-load the working set once; the finds below read from memory.
RecordViewCache cache = new RecordViewCache(vehicle);
```

## 5. Temp tables — `TempDB` vs `InMemory`

| | `TempDB` | `InMemory` |
|---|---|---|
| Storage | SQL Server tempdb | ISAM file on the AOS tier |
| Joins / set-based ops | supported and efficient | **not supported / slow** |
| SSRS reports | required | does not work |

- Default to `TempDB`. `InMemory` is an AX 2009 legacy carried forward.
- `TableType` is storage (`RegularTable` / `TempDB` / `InMemory`); `TableGroup`
  is the business role (`Main`, `Transaction`, …). **Passing `tableGroup=TempDB`
  is the single most common mistake** in this area.
- TempDB tables are scoped to the buffer's lifetime and dropped automatically.
- To move temp data across tiers use a `container` or a `RecordSortedList`.

```sh
d365fo generate table FmTmpVehicleStaging --pattern main --table-type TempDB \
  --install-to FleetManagement
```

## 6. Parallel batch

A single `run()` is single-threaded. To use more than one thread, fan the work
out into **independent** batch tasks on one `BatchHeader`.

- Partition the work (by key range) and `batchHeader.addRuntimeTask(task, server)`
  once per partition.
- Each task owns its own `ttsbegin`/`ttscommit` — never one transaction across all
  partitions.
- **⛔ Never use `System.Threading` inside batch code.** The batch framework owns
  threading; manual threads break the session and company context.
- Concurrency is a configuration concern (batch group + the AOS maximum batch
  threads), not a code one. Size partitions at a few minutes of work each.
- Add `addDependency()` only where ordering is genuinely required — independent
  tasks maximise parallelism.
- **Make each task idempotent.** A parallel task may be retried after a transient
  fault; guard with a status flag or a RecId watermark.
- `BatchHeader::getCurrentBatchHeader()` when spawning from inside a running
  batch; a fresh `BatchHeader::construct()` when scheduling from a controller.

```xpp
/// <summary>
/// Fans one task per partition onto a single batch header so they run concurrently.
/// </summary>
public void scheduleParallel(List _partitions)
{
    BatchHeader    batchHeader = BatchHeader::construct();
    ListEnumerator le          = _partitions.getEnumerator();

    while (le.moveNext())
    {
        FmPartitionTask task = new FmPartitionTask();   // extends RunBaseBatch
        task.parmPartitionKey(le.current());
        batchHeader.addRuntimeTask(task, this.parmCurrentBatch().RecId);
    }

    batchHeader.parmCaption("@Fleet:ParallelImport");
    batchHeader.save();
}
```

## Hard rules

- Measure before caching. A cache over an unindexed query hides the defect and
  moves the pain to invalidation.
- `EntireTable` only on tables that are effectively read-only at runtime.
- Never cache per-user or security-sensitive data in a `Global` scope.
- Temp tables: `TableType=TempDB`, `TableGroup=Main`.
- Batch parallelism through `BatchHeader`, never through .NET threads.
