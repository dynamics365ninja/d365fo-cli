---
description: Authoritative rules for X++ select / while-select queries in D365 Finance & Operations. Invoke whenever the user asks to write a "select", "while select", "query", joins, aggregates, cross-company, set-based ops, or any data-access X++.
applyTo: '**/*.xpp,**/AxClass/**,**/AxTable/**,**/AxQuery/**'
---
# X++ database queries — `select` / `while select`

> **Source of truth:** [learn:xpp-select-statement](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-select-statement).
> **Pre-flight:** confirm the target table / field with `d365fo get table <Name> --output json` before writing the query.

## Statement order (grammar-enforced)

```
select [FindOption…] [FieldList from] tableBuffer [index…]
       [order by | group by] [where …] [join … [where …]]
```

`FindOption` keywords (`crossCompany`, `firstOnly`, `forUpdate`, `forceNestedLoop`, `forceSelectOrder`, `forcePlaceholders`, `pessimisticLock`, `optimisticLock`, `repeatableRead`, `validTimeState`, `noFetch`, `reverse`, `firstFast`) sit **between `select` and the buffer / field list** — never on a joined buffer (sole exception: `forUpdate` may target a specific buffer in a join).

`order by` / `group by` / `where` must appear **after the LAST `join` clause** — never between joins.

## `crossCompany` belongs on the OUTER buffer

```xpp
// ✅ CORRECT
select crossCompany custTable
    join custInvoiceJour
    where custInvoiceJour.OrderAccount == custTable.AccountNum;

// ❌ WRONG — cross-company on the joined buffer
select custTable
    join crossCompany custInvoiceJour
    where …;
```

Optional company filter: `select crossCompany : myContainer custTable …` — `myContainer` is a `container` literal `(['dat'] + ['dmo'])`. Empty container = scan all authorised companies.

## `in` operator — any primitive type, not just enums

Grammar: `where Expression in List`, where `List` is an X++ **`container`**, not a `Set`/`List`/`Map`/sub-query. Works with `str`, `int`, `int64`, `real`, `enum`, `boolean`, `date`, `utcDateTime`, `RecId`. One `in` clause per `where`; AND multiple set filters together.

```xpp
container postingTypes = [LedgerPostingType::PurchStdProfit, LedgerPostingType::PurchStdLoss];
container accounts     = ['1000', '2000', '3000'];
select sum(CostAmountAdjustment) from inventSettlement
    where inventSettlement.OperationsPosting in postingTypes
       && inventSettlement.LedgerAccount     in accounts;
```

❌ Never expand `in` into `OR == OR ==` chains.

## Other Learn-confirmed rules

- **Field list before table** when you don't need the full row — `select FieldA, FieldB from myTable where …`. Never `select * from`.
- **`firstOnly`** when at most one row is expected. Cannot be combined with `next`.
- **`forUpdate`** required before any `.update()` / `.delete()`; pair with `ttsbegin` / `ttscommit`.
- **`exists join` / `notExists join`** instead of nested `while select` for filter-only joins.
- **Outer join** — only LEFT outer; no RIGHT outer, no `left` keyword. Default values fill non-matching rows; distinguish "no match" vs "real zero" by checking the joined buffer's `RecId`.
- **Join criteria use `where`, not `on`.** X++ has no `on` keyword.
- **`index hint`** requires `myTable.allowIndexHint(true)` *before* the select; otherwise silently ignored. Only when measured.
- **Aggregates** (`sum`, `avg`, `count`, `minof`, `maxof`):
  - `sum` / `avg` / `count` work only on integer/real fields.
  - When `sum` would return null (no rows), X++ returns NO row — guard with `if (buffer)` after.
  - Non-aggregated fields in the select list must be in `group by`.
- **`forceLiterals`** is forbidden — SQL injection. Use `forcePlaceholders` (default for non-join selects) or omit.
- **`validTimeState(dateFrom, dateTo)`** for date-effective tables (`ValidTimeStateFieldType ≠ None`). Don't query without it unless you specifically want all historical rows.
- **Set-based ops** (`RecordInsertList`, `insert_recordset`, `update_recordset`, `delete_from`) over row-by-row loops for performance. They fall back to row-by-row only when an overridden table method, DB log, or alerts subscription forces it.
- **SQL injection mitigation** — `executeQueryWithParameters` for dynamic queries; never concatenate strings into `where`.
- **Timeouts** — interactive 30 min, batch/services/OData 3 h. Override via `queryTimeout`. Catch `Exception::Timeout`.

## Hard "never" list

- **Never** call functions in `where` (e.g. `where strFmt(...) == 'X'`) — assign to a local first; the optimizer can't index function expressions.
- **Never** use `today()` (BP `BPUpgradeCodeToday`) — use `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`.
- **Never** nest `while select` loops (BP `BPCheckNestedLoopinCode`) — joins, `exists join`, or pre-load to `Map` / temp table.
- **Never** call `doInsert` / `doUpdate` / `doDelete` for normal business logic — they bypass overridden methods, framework validation, and event handlers. Reserved for data-fix / migration scripts only.

## Pre-flight commands

```sh
d365fo get table <Table> --output json                 # field list, indexes, relations
d365fo find relations <Table> --output json            # FK relations to model joins
d365fo find usages <field|method> --output json        # caller risk before refactor
```
