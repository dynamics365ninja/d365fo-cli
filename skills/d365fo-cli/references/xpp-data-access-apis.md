> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Data-access APIs beyond `select`

> Three alternatives to the `select` statement, in the order you should reach for
> them. `select` stays the default — it is the only one with compile-time field
> validation. Everything below trades that away for something specific.

| API | Reach for it when | Cost |
|---|---|---|
| `select` / `while select` | the shape is known at compile time | — |
| **AOT `Query` / `QueryRun`** | a form or report binds to it, a user edits the filter, or several consumers share it | no compile-time field checking |
| **SysDa** | the shape depends on runtime conditions and you are writing framework code | verbose; easy to get the object graph wrong |
| **Direct SQL** | a read X++ genuinely cannot express efficiently | no company/partition filter, needs a permission assert |

Full `select` grammar: `d365fo knowledge get xpp-database-queries`.

## 1. AOT Query object model

- `Query` holds the structure; `QueryBuildDataSource` (QBDS) is one table;
  `QueryBuildRange` is one filter; `QueryRun` executes and iterates.
- Add a range **idempotently** with `SysQuery::findOrCreateRange(qbds, fieldNum(…))`.
  Calling `addRange` in `executeQuery()` — which fires on every refresh — stacks
  duplicate ranges until the query returns nothing.
- Nested joins are child data sources: `qbds.addDataSource(...)`, with
  `qbds.joinMode(JoinMode::ExistsJoin)` choosing the kind at runtime.
- Cross-company at query level: `query.allowCrossCompany(true)` plus
  `query.addCompanyRange('dat')`.

```xpp
Query                query = new Query();
QueryBuildDataSource qbds  = query.addDataSource(tableNum(CustTable));
QueryRun             qr;
CustTable            ct;

qbds.addRange(fieldNum(CustTable, CustGroup)).value(queryValue('10'));
qbds.addSortField(fieldNum(CustTable, AccountNum));

qr = new QueryRun(query);
while (qr.next())
{
    ct = qr.get(tableNum(CustTable));
    // …
}
```

```xpp
// In a form's executeQuery() CoC — safe to run on every refresh.
QueryBuildDataSource qbds = this.queryBuildDataSource();
SysQuery::findOrCreateRange(qbds, fieldNum(CustTable, CustGroup)).value('DOM');
```

## 2. SysDa — the fluent API

- `SysDaQueryObject` is the builder. **It is not executable on its own**: wrap it
  in a `SysDaSearchObject`, then iterate with a `SysDaSearchStatement`. The
  iterator methods take the *search* object, never the query object — this is the
  single most common SysDa mistake.
- `SysDaSearchStatement.next()` compiles but is marked obsolete in favour of
  `findNext()`; prefer `findNext()` once you have verified the signature on your
  platform version with `d365fo get class SysDaSearchStatement`.
- An X++ enum passed to `SysDaValueExpression` must go through `enum2int()` — the
  parameter is `System.Object` and will not accept the enum.
- Statement families mirror the set-based operators: `SysDaFindObject` /
  `SysDaFindStatement` (firstOnly), `SysDaUpdateObject`, `SysDaInsertObject`,
  `SysDaDeleteObject`.
- Joins: `qe.joinClause(SysDaJoinKind::InnerJoin, joinQe)` — Inner, Outer, Exists,
  NotExists.

```xpp
CustTable custTable;

var qe = new SysDaQueryObject(custTable);
qe.whereClause(new SysDaEqualsExpression(
    new SysDaFieldExpression(custTable, fieldStr(CustTable, AccountNum)),
    new SysDaValueExpression('US-001')));

// A query object is not executable — wrap it first.
var so = new SysDaSearchObject(qe);
var ss = new SysDaSearchStatement();
while (ss.next(so))
{
    // custTable is populated on each iteration
}
```

## 3. Direct SQL — last resort

Direct SQL bypasses the X++ data layer, and with it every guarantee that layer
provides.

- **The permission assert is required**, not optional:
  `new SqlStatementExecutePermission(sql).assert();` immediately before execute,
  `CodeAccessPermission::revertAssert();` immediately after. Without it you get a
  CAS runtime error.
- **Never concatenate external input into the statement.** Validate or whitelist
  every value; treat anything from a user, a file or a service as hostile.
- **Qualify by `DataAreaId` and `Partition` yourself.** Direct SQL does not apply
  the automatic company/partition filter that `select` does — this is the defect
  that turns "a report is slightly wrong" into "a report shows another company's
  data".
- Raw SQL uses the SQL names (`RECID`, `DATAAREAID`), not the AOT casing.
- Try the set-based X++ operators first (`insert_recordset`, `update_recordset`,
  `delete_from`); they are usually what direct SQL was reached for.
- DDL, cross-database access and `forceLiterals` are restricted or forbidden in
  the cloud. Keep direct SQL to parameterised `SELECT`s against the AX database.

```xpp
Connection conn = new Connection();
Statement  stmt = conn.createStatement();
str        custGroup = this.getValidatedCustGroup();   // whitelisted, never raw input
str        sql = strFmt(
    'SELECT RECID, ACCOUNTNUM FROM CUSTTABLE WHERE DATAAREAID = \'%1\' AND CUSTGROUP = \'%2\'',
    curExt(), custGroup);

new SqlStatementExecutePermission(sql).assert();
try
{
    ResultSet rs = stmt.executeQuery(sql);
    while (rs.next())
    {
        // rs.getInt64(1), rs.getString(2) …
    }
}
finally
{
    CodeAccessPermission::revertAssert();
}
```

## Hard rules

- Ground every class before you call it — this area is full of near-identical
  names: `d365fo get class SysDaQueryObject --output json`,
  `d365fo get class QueryBuildDataSource --output json`.
- `SysQuery::findOrCreateRange` over `addRange` anywhere the code can run twice.
- Never build a query with string concatenation of field names; use `fieldNum()` /
  `fieldStr()` so a rename is a compile error.
- Direct SQL needs a written justification in the code comment: which X++
  construct was tried and why it was not enough.
