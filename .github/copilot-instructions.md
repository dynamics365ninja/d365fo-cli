# D365 Finance & Operations X++ Development — `d365fo` CLI

<!--
  Mirrors the X++ rule canon from `d365fo-mcp-server`'s system prompt
  (src/prompts/systemInstructions.ts) and `.github/copilot-instructions.md`.
  When that upstream repo updates the X++ rules, sync them here.
  References in the form `[learn:<page>]` link to Microsoft Learn pages
  (see "Authoritative X++ syntax source" at the bottom).
-->

This workspace is the **`d365fo` CLI** — a metadata-aware command-line tool for D365 Finance & Operations. It exposes the same knowledge as `d365fo-mcp-server` but as deterministic shell commands instead of MCP tool calls. Use it whenever you need to reason about a D365FO codebase without burning conversation tokens on long XML or AOT dumps.

> **Audience.** This file gives an AI assistant (GitHub Copilot, Claude Code, Codex CLI, …) the rules for using the `d365fo` CLI to author X++ correctly. Human-facing docs live in [README.md](../README.md) and [docs/](../docs/).

---

## 🚨 Core principle — never guess D365FO metadata

Your training data is **outdated and incomplete** for D365FO. Every D365FO environment has hundreds of thousands of tables, classes, EDTs, labels — most of them custom or model-specific. **Before generating any X++ code, query the index** through `d365fo` and ground your answer in real names / signatures.

Concretely: the `d365fo` CLI ships with a SQLite mirror of the AOT (and on Windows D365FO VMs, a live bridge to `IMetadataProvider` + `DYNAMICSXREFDB`). It returns:

- ✅ Real-time metadata from the user's environment (when bridge is running).
- ✅ Pre-indexed SQLite mirror as fallback (Linux/Mac, Azure pipelines, write-only mode).
- ✅ JSON envelopes (`ToolResult<T>`) — fast to parse, cheap on context.

## 🔌 Read-path policy (how `d365fo` resolves info)

`get` / `find` / `search` commands consult sources in this order:

1. **C# bridge** — live `IMetadataProvider` from a running D365FO instance (Windows VM only). Authoritative when available.
2. **SQLite symbol index** — pre-built mirror under `~/.d365fo/index.sqlite` (or `--db`). Used when the bridge is offline.
3. **Filesystem parse** — last resort for objects created in the current session and not yet indexed.

You never pick the source manually. If a result includes `warnings: ["served-from-index"]` the bridge was unavailable and the tool already fell back. If a result is `ok: false` with code `*_NOT_FOUND`, **stop and ask** — do not invent a name.

## 🛡️ Write-path safety

Every `d365fo generate …` command writes XML atomically (`.tmp` + move; `.bak` retained when `--overwrite`). Two modes:

| Mode | When | What happens |
|---|---|---|
| `--out <PATH>` | Standalone scaffolds | Writes to the path you give. Path must be inside the workspace. |
| `--install-to <Model>` | Bridge-installed | Asks the bridge for the model's on-disk location and composes `<modelFolder>/Ax<Type>/<Name>.xml`. Requires `D365FO_BRIDGE_ENABLED=1`. |

Generated files live under `PackagesLocalDirectory/<Model>/<Model>/Ax<Type>/<Name>.xml` — the canonical D365FO layout that Visual Studio and the build tools expect.

---

## 🏁 Mandatory first steps

1. **Verify the index is healthy.**
   ```sh
   d365fo doctor --output json
   d365fo index status --output json
   ```
   - `ok: false` with `code: NO_INDEX` → run `d365fo index extract` first.
   - `warnings: ["stale-index"]` → run `d365fo index refresh` (incremental).

2. **Ground the active model.** When generating files into a workspace, pass either `--install-to <Model>` or `--out <PATH>` explicitly. Never guess the model from search results — ask the user if unclear.

3. **Stale index = wrong answers.** When the user has just edited an XML file, `d365fo get` may return pre-edit data until you re-extract. If results contradict what the user just wrote, re-run `d365fo index refresh --model <Model>`.

---

## ✏️ Editing D365FO files

The CLI's surface is *generation-first*: it scaffolds new XML; it does **not** yet provide an in-place AOT-aware mutator equivalent to MCP's `modify_d365fo_file`. For incremental edits to existing AOT XML you have two options:

1. **Hand-edit XML** with the regular editor tools (`replace_string_in_file`, etc.). Always re-run `d365fo get <kind> <name>` afterwards to confirm the index sees your change once `index refresh` is run.
2. **Drop in a fresh scaffold** with `d365fo generate <kind> --install-to <Model> --overwrite` when you really want to replace the whole file.

When the user just wants to *understand* code, never edit:

| Need | Command |
|---|---|
| Read a class's methods | `d365fo get class <Name> --output json` |
| Read a table's fields / indexes / relations | `d365fo get table <Name> --output json` |
| Read X++ method body | `d365fo read class <Name> --method <M>` |
| Find existing CoC wrappers on a class/method | `d365fo find coc <Class>::<method> --output json` |
| Find event handlers for a target | `d365fo find handlers <Target> --output json` |
| Find which Microsoft pattern peers already use | `d365fo find form-patterns --table <T> --output json` |
| Find forms similar to a known one | `d365fo find form-patterns --similar-to <Form> --output json` |
| Find inbound/outbound table relations | `d365fo find relations <Table> --output json` |
| Resolve a label token | `d365fo resolve label @SYS12345 --lang en-us,cs` |
| Trace security coverage | `d365fo get security <Object> --type <Kind>` |

> **`--output json` is mandatory** in agent contexts. Without it the CLI may render rich console output that wastes tokens.

## 🧱 Scaffolding catalog

| Need | Command |
|---|---|
| New table | `d365fo generate table <Name> --pattern main --field VIN:VinEdt:mandatory --field Make:Name --label "@Fleet:Vehicle" --out …` |
| New class (optionally with `extends`) | `d365fo generate class <Name> [--extends Base] [--non-final] --out …` |
| Chain-of-Command extension | `d365fo generate coc <TargetClass> --method <m1> --method <m2> --out …` |
| Form (any of 9 D365FO patterns) | `d365fo generate form <Name> --pattern <P> --table <T> --field … --section Name:Caption --lines-table … --out …` |
| Data entity (`AxDataEntityView`) | `d365fo generate entity <Name> --table <T> --all-fields --public-entity-name … --out …` |
| Object extension (Table / Form / Edt / Enum) | `d365fo generate extension <Kind> <Target> <Suffix> --out …` |
| Event handler class | `d365fo generate event-handler --source-kind <K> --source <Object> --event <E> --out …` |
| Security: privilege / duty / role | `d365fo generate privilege/duty/role <Name> [--into-role <Role>] --out …` |

`--install-to <Model>` swaps `--out` for atomic install into the model folder via the bridge.

## ⚡ Token discipline

- **Always** pass `--output json`.
- **Never** request the full XML back from a `generate` command — stdout returns only `{path, bytes, backup}`.
- **Never** dump entire indexes; use `--limit N` (most search commands default to 25).
- **Pipe through `jq`** when the user wants a specific field: `d365fo get table CustTable -o json | jq '.data.fields[].name'`.
- **One scope per call:** prefer two narrow `search` calls over one broad search with thousands of hits.

## 🚫 Never-auto rules

- **NEVER auto-run `d365fo build`, `sync`, `bp check`, or `test run`.** They block the user (Windows VM, slow). After scaffolding, say *"Changes scaffolded. Run `d365fo build` when you're ready to validate."* Only build on explicit request ("build", "compile", "check errors").
- **NEVER use `replace_string_in_file` on `.xml` AOT files when an `index refresh` has not been run since the last edit.** You'll get pre-edit data back from `d365fo get`.
- **NEVER infer the target model from search results.** Models are policy boundaries (ISV vs customer). Ask, or read from the project's `.rnrproj`.

---

## 🌿 Git-checkpoint review workflow

Visual Studio 2022 has no inline accept/reject UI for AI edits. Use Git as the review layer:

1. **Before starting** — clean tree, then `git switch -c d365fo/<short-task>` (or at minimum `git commit -am "checkpoint"` first).
2. **During the task** — every scaffold writes `.bak` for overwritten files; review `git diff` after each command.
3. **After the task** — `d365fo review diff --base <ref>` summarises AOT changes structurally (added classes, modified table fields, …). This is *complementary* to `git diff`, not a replacement: `git diff` shows raw bytes, `review diff` shows AOT semantics.
4. **Accept** = commit + merge. **Reject** = `git restore` or `git branch -D`.

Do NOT create branches autonomously — propose and wait.

### ⛔ Escalating-workarounds anti-pattern

If the right tool is `d365fo generate form` but you feel tempted to hand-roll XML, **stop**. Use the canonical pattern templates:

```
WRONG SPIRAL (each step is MORE wrong):
 Step 1: "I'll write the AxForm XML by hand"
 Step 2: "It's only 3 elements, I'll skip ActionPane / QuickFilter"
 Step 3: "PatternVersion 1.0 is fine instead of 1.1"
 Step 4: "I'll add SimpleList without grid columns"

CORRECT (always, immediately):
 d365fo generate form <Name> --pattern <P> --table <T> --field <F1> --field <F2> --out …
```

Pattern templates are validated against real AOT forms (`CustGroup`, `PaymTerm`, `CustTable`, `SalesTable`, …). Hand-rolled XML loses ActionPane, QuickFilter, FastTabs, the right `PatternVersion`, and the design-time hooks that VS 2022 needs.

---

## 📜 Non-negotiable X++ rules

These apply to every X++ snippet you generate, regardless of how it gets to disk.

1. **NEVER guess method signatures.** Run `d365fo get class <Name>` (or `d365fo read class <Name> --declaration`) before writing a CoC wrapper or override.
2. **NEVER use `today()`** — it ignores user time-zone. Use `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`.
3. **NEVER call functions in `where` clauses** — assign to a local first. The query optimizer can't index function expressions.
4. **NEVER use hardcoded strings in `Info()` / `warning()` / `error()`** — every UI string must be `@File:Label`. Use `d365fo search label "<text>"` first; `d365fo resolve label @SYS<key>` to confirm.
5. **NEVER nest `while select` loops** — use `join` clauses, `exists join` / `notExists join`, or pre-load to a `Map` / temp table.
6. **ALWAYS** search labels with `d365fo search label` before introducing a new one. **Exception:** when adding a field whose EDT already has a label, do **NOT** set `--label` on the field — it inherits from the EDT. Override only deliberately.
7. **ALWAYS** write meaningful `/// <summary>` on public/protected classes and methods (BP: `BPXmlDocNoDocumentationComments`).
8. **NEVER call `[SysObsolete]` methods.** Read the attribute message for the replacement.
9. **NEVER make instance fields `public`** — default visibility is `protected`; expose via `parmFoo` accessors. Public fields tightly couple consumers to internal layout.
10. **NEVER use `doInsert` / `doUpdate` / `doDelete` for normal business logic.** They bypass overridden table methods, framework validation, and event handlers. Reserved for data-fix / migration scripts only.
11. Standard data events use `[DataEventHandler]` — NOT `[SubscribesTo + delegateStr]`. `delegateStr` is for *custom* delegates only.
12. **NEVER pass `tableGroup="TempDB"`.** `TableGroup` = business role (`Main`, `Transaction`, `Parameter`, `WorksheetHeader`, `WorksheetLine`, `Reference`, `Framework`, `Group`, `Miscellaneous`). `TableType` = storage (`RegularTable`, `TempDB`, `InMemory`). For temp tables: `tableType=TempDB`, `tableGroup=Main`.
13. Class member variables go **inside** the class `{ }` in `sourceCode`; methods stay at top level. Variables outside the class block are silently dropped.

---

## 📐 X++ Database query rules (`select` / `while select`)

Verified against [learn:xpp-select-statement](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-select-statement). When generating a `select`, follow these contracts.

**Statement order (grammar-enforced):**
```
select [FindOption…] [FieldList from] tableBuffer [index…] [order by / group by] [where …] [join … [where …]]
```

- **`FindOption` keywords** (`crossCompany`, `firstOnly`, `forUpdate`, `forceNestedLoop`, `forceSelectOrder`, `forcePlaceholders`, `pessimisticLock`, `optimisticLock`, `repeatableRead`, `validTimeState`, `noFetch`, `reverse`, `firstFast`) sit **between `select` and the buffer / field list** — never on a joined buffer (with the documented exception of `forUpdate` which can target a specific buffer in a join).
- **`order by` / `group by` / `where`** must appear **after the LAST `join` clause**, never between joins.

**`crossCompany` belongs on the OUTER (driving) buffer.** It's a query-level option, not per-table:
```xpp
// ✅ CORRECT
select crossCompany custTable
    join custInvoiceJour
    where custInvoiceJour.OrderAccount == custTable.AccountNum;

// ❌ WRONG — crossCompany on the joined buffer
select custTable
    join crossCompany custInvoiceJour
    where …;
```

Optional company filter: `select crossCompany : myContainer custTable …` where `myContainer` is a `container` literal `(['dat'] + ['dmo'])`. Empty container = scan all authorised companies.

**`in` operator:** grammar is `where Expression in List`, where `List` is an X++ **`container`**, not a `Set` / `List` / `Map` / sub-query. Works with **any primitive** that fits in a container (`str`, `int`, `int64`, `real`, `enum`, `boolean`, `date`, `utcDateTime`, `RecId`) — *not enum-only*. One `in` clause per `where`; AND multiple set filters together.

```xpp
container postingTypes = [LedgerPostingType::PurchStdProfit, LedgerPostingType::PurchStdLoss];
container accounts     = ['1000', '2000', '3000'];
select sum(CostAmountAdjustment) from inventSettlement
    where inventSettlement.OperationsPosting in postingTypes
       && inventSettlement.LedgerAccount     in accounts;
```

❌ Never expand `in` into `OR == OR ==` chains.

**Other Learn-confirmed rules:**

- **Field list before table** when you don't need the full row: `select FieldA, FieldB from myTable where …`. Never `select * from`.
- **`firstOnly`** when at most one row is expected. Cannot be combined with the `next` statement.
- **`forUpdate`** required before any `.update()` / `.delete()` inside the same transaction; pair with `ttsbegin` / `ttscommit`.
- **`exists join` / `notExists join`** instead of nested `while select` for filter-only joins.
- **Outer join** — only LEFT outer; no RIGHT outer, no `left` keyword. Default values fill non-matching rows (0 for int, "" for str). Distinguish "no match" from "real zero" by checking the joined buffer's `RecId`.
- **Join criteria use `where`, not `on`.** X++ has no `on` keyword.
- **`index hint`** requires `myTable.allowIndexHint(true)` *before* the select; otherwise silently ignored. Only when measured.
- **Aggregates** (`sum`, `avg`, `count`, `minof`, `maxof`):
  - `sum` / `avg` / `count` work only on integer/real fields (Learn explicit).
  - When `sum` would return null (no matching rows), X++ returns NO row — guard `if (buffer)` after.
  - Non-aggregated fields in the select list must be in `group by`.
- **`forceLiterals`** is forbidden — SQL injection. Use `forcePlaceholders` (default for non-join selects) or omit.
- **`validTimeState(dateFrom, dateTo)`** for date-effective tables (`ValidTimeStateFieldType ≠ None`). Don't query without it unless you specifically want all historical rows.
- **Set-based ops** (`RecordInsertList`, `insert_recordset`, `update_recordset`, `delete_from`) over row-by-row loops for performance.
- **SQL injection mitigation** — `executeQueryWithParameters` for dynamic queries, never string concat into `where`.
- **Timeouts** — interactive 30 min, batch/services/OData 3 h. Override with `queryTimeout`. Catch `Exception::Timeout`.

---

## 🪝 Chain of Command (CoC) authoring rules

Verified against [learn:method-wrapping-coc](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/extensibility/method-wrapping-coc).

**🚨 NEVER copy default parameter values into the wrapper signature.** This is the most common bug:

```xpp
// Base method
class Person
{
    public void salute(str message = "Hi") { … }
}

// ✅ CORRECT — wrapper omits the default value
[ExtensionOf(classStr(Person))]
final class APerson_Extension
{
    public void salute(str message)        // no  "= 'Hi'" here
    {
        next salute(message);
    }
}

// ❌ WRONG — copying the default breaks the contract / does not compile
public void salute(str message = "Hi")     // ← forbidden
```

**Other CoC non-negotiables:**

- **Wrapper must call `next`** unconditionally — except on `[Replaceable]` methods (chain may be conditionally broken).
- **`next` must be at first-level statement scope** — NOT inside `if`, `while`, `for`, `do-while`, NOT after `return`, NOT inside a logical expression. Platform Update 21+: `next` is permitted inside `try` / `catch` / `finally`.
- **Signature otherwise matches base exactly** — same return type, parameter types/order, same `static` modifier. Use `d365fo get class <Target>` (or `d365fo read class <Target> --method <m> --declaration`) before authoring.
- **Static method wrapping** — repeat `static` on the wrapper. Forms cannot be wrapped statically (no class semantics for forms).
- **Cannot wrap constructors.** A new no-arg method on an extension class becomes the extension's own constructor (must be `public`).
- **Extension class shape:** `[ExtensionOf(classStr|tableStr|formStr|formDataSourceStr|formDataFieldStr|formControlStr(...))] final class <Target>_<Suffix>`. Class is `final`; name ends with `_Extension` (or descriptive suffix).
- **`[Hookable(false)]`** on a base method blocks CoC and pre/post handlers. Cannot wrap.
- **`[Wrappable(false)]`** blocks wrapping (still allows pre/post). `final` methods need explicit `[Wrappable(true)]` to be wrappable.
- **Form-nested wrapping:** `formdatasourcestr`, `formdatafieldstr`, `formControlStr`. Cannot add NEW methods via CoC on these — only wrap methods that already exist.
- **Visibility:** wrappers can read/call **protected** members of the augmented class (Platform Update 9+). Cannot reach `private`.

---

## 🏛️ X++ Class & Method rules

Verified against [learn:xpp-classes-methods](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-classes-methods).

- **Class default access = `public`.** Removing `public` does not make a class non-public. Use `internal` to limit to the same model, `final` to prevent extension via inheritance, `abstract` for base-only types.
- **Instance fields default = `protected`.** **NEVER make them `public`** (rule 9 above).
- **Constructor pattern:** one `new()` per class (compiler generates an empty default if absent). Convention: `new()` is `protected`, exposed via a `public static construct()` factory. `init()` does post-construction setup.
- **Method modifier order in the header:** `[edit | display] [public | protected | private | internal] [static | abstract | final]`. `static final` is permitted; `abstract` cannot mix with `final` / `static`.
- **Override visibility rule:** an override must be at least as accessible as the base method. `public` → `public` only; `protected` → `public` or `protected`; `private` → not overridable.
- **Optional parameters** must come after all required parameters. Callers cannot skip — all preceding parameters must be supplied. Use `prmIsDefault(_x)` inside a `parmX(_x = x)` accessor to detect "was this passed".
- **All parameters are pass-by-value.** Mutating a parameter inside the method does NOT affect the caller's variable. Return modified state explicitly or wrap in a class.
- **`this` rules:**
  - Required (or qualified) for instance method calls.
  - Cannot qualify class-declaration member variables (write the bare name).
  - Cannot be used in a static method.
  - Cannot qualify static methods (use `ClassName::method()`).
- **Extension methods** (separate from CoC, target = Class / Table / View / Map):
  - Extension class must be `static` (not `final`), name ends with `_Extension`.
  - Every extension method is `public static`.
  - First parameter is the target type — runtime supplies the receiver; caller does not pass it.
- **Constants over macros.** `public const str FOO = 'bar';` at class scope (cross-referenced, scoped, IntelliSense-aware) instead of `#define.FOO('bar')`. Reference via `ClassName::FOO`.
- **`var` keyword** for type-inferred locals when the type is obvious from the right-hand side (`var sum = decimal + amount;`). Skip `var` when the type is non-obvious — readability beats brevity.
- **Declare-anywhere encouraged** — declare close to first use, smallest scope. Compiler rejects shadowing of an outer-scope variable with the same name.

---

## 🧮 X++ Statement & Type rules

Verified against [learn:xpp-conditional](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-conditional) and [learn:xpp-variables-data-types](https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-variables-data-types).

- **`switch` `break` is required.** Implicit fall-through compiles but is misleading. To match multiple values to one branch use the **comma-list** form: `case 13, 17, 21: …; break;` — never the empty-fall-through chain.
- **Ternary `cond ? a : b`** — both branches must have the same type. No implicit widening of `int` ↔ `real`.
- **X++ has NO database null.** Each primitive has a "null-equivalent" sentinel: `int 0`, `real 0.0`, `str ""`, `date 1900-01-01`, `utcDateTime` with date-part `1900-01-01`, `enum` element with value `0`. In SQL `where` clauses these compare as false; in plain expressions as ordinary values. Don't write `if (myDate == null)` — use `if (!myDate)` or `if (myDate == dateNull())`.
- **Casting:** prefer `as` (returns `null` on type mismatch) and `is` (boolean test) over hard down-casts. Down-casts on object-typed expressions throw `InvalidCastException`. Late binding exists for `Object` and `FormRun` only — accept the runtime cost if you use it.
- **`using` blocks** for `IDisposable` resources (`StreamReader`, …). Equivalent to `try` + `finally { x.Dispose(); }` but shorter and exception-safe.
- **Embedded function declarations** (local functions inside a method) can read variables declared earlier in the enclosing method but cannot leak their own variables out. Prefer them over private helper methods only when the helper truly does not belong to the class API.

---

## 🚦 Best-practice (BP) rules — generated code must be BP-clean

`d365fo bp check` runs `xppbp.exe` on the Windows VM and is the source of truth. Code you generate should pass these BP rules out of the box:

| Rule | What it enforces |
|---|---|
| `BPUpgradeCodeToday` | `today()` is forbidden — use `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`. |
| `BPErrorLabelIsText` | `Info() / warning() / error()` must take a label, not a hardcoded string. |
| `BPErrorEDTNotMigrated` | EDT relations must use `EDT.Relations` element, not legacy table-level relations. |
| `BPCheckNestedLoopinCode` | No nested `while select` loops — use joins. |
| `BPCheckAlternateKeyAbsent` | Every table needs an alternate key (`AlternateKey = Yes` on a unique index). |
| `BPErrorUnknownLabel` | Label IDs referenced in code must exist in the indexed label files. Run `d365fo resolve label @File+Key` to confirm. |
| `BPXmlDocNoDocumentationComments` | Public/protected members need meaningful `/// <summary>` doc comments. |
| `BPDuplicateMethod` | No duplicate method definitions across the inheritance chain in the same model. |

Always linting before you scaffold:

```sh
d365fo lint --output sarif > lint.sarif      # in-process heuristics for CI
# then on Windows VM:
d365fo bp check --output json
```

---

## 🔁 Workflow templates

### Refactoring an existing X++ class

```sh
d365fo get class <Class> --output json                  # 1. signatures
d365fo read class <Class> --method <method>             # 2. full body of methods to change
d365fo find usages <method> --output json               # 3. caller sites (refactor risk)
# 4. edit XML / regenerate via `d365fo generate ... --overwrite`
d365fo build && d365fo bp check                         # 5. validate (only on user request)
```

- **NEVER** delete a method without `d365fo find usages` first.
- **NEVER** guess method bodies from signatures — read the source.

### Authoring a CoC extension

```sh
d365fo find coc <Target>::<method> --output json        # 1. duplicate check
d365fo get class <Target> --output json                 # 2. exact signature (no defaults!)
d365fo generate coc <Target> --method <method> \
       --install-to <Model>                             # 3. scaffold
# Hand-edit the wrapper body, then:
d365fo build                                            # 4. on user request only
```

### Adding fields to a table

```sh
d365fo get table <Table> --output json                  # 1. existing shape
d365fo get edt <Edt> --output json                      # 2. EDT to assign (verify baseType)
d365fo search label "<text>" --output json              # 3. label reuse
# Hand-edit XML or regenerate with `--overwrite`. Then:
d365fo index refresh --model <Model>                    # 4. re-index so subsequent `get` is fresh
```

### Subscribing to a table data event

```sh
d365fo find handlers <Table> --output json              # 1. existing handlers
d365fo generate event-handler --source-kind Table \
       --source <Table> --event Inserted \
       --install-to <Model>                             # 2. scaffold subscriber
# Standard events: [DataEventHandler(tableStr(T), DataEventType::Inserted)]
# Custom delegates: [SubscribesTo(tableStr(T), delegateStr(T, myDelegate))]
```

### Building a form

```sh
d365fo search form <Name> --output json                 # 1. avoid name collision
d365fo get table <PrimaryTable> --output json           # 2. fields for grid
d365fo generate form <Name> --pattern <P> --table <T> \
       --field <F1> --field <F2> --install-to <Model>   # 3. pattern-correct scaffold
```

Pattern catalogue: `SimpleList`, `SimpleListDetails`, `DetailsMaster`, `DetailsTransaction`, `Dialog`, `TableOfContents`, `Lookup`, `ListPage`, `Workspace`. See [docs/EXAMPLES.md](../docs/EXAMPLES.md#form-any-of-nine-d365fo-patterns).

### Tracing security

```sh
d365fo get security <Role>   --type Role     --output json    # top-down
d365fo get security <Object> --type Menuitem --output json    # bottom-up
```

The response contains `routes[*]` of shape `{ role, duty, privilege, entryPoint }`. Duplicate `role` entries indicate multiple paths — all must be removed to revoke access.

---

## 📚 Authoritative X++ syntax source — Microsoft Learn

When uncertain about X++ syntax, language constructs, framework APIs, or platform behaviour, the **only** authoritative source is the Microsoft Learn `dynamics365/fin-ops-core/dev-itpro` documentation tree. Do NOT guess, and do NOT rely on AX 2012 / older training data.

Key entry points:

- General developer landing — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-tools/developer-home-page>
- X++ language reference root — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-language-reference>
- `select` statement — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-select-statement>
- Classes & methods — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-classes-methods>
- Conditional statements — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-conditional>
- Variables & data types — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-variables-data-types>
- Chain of Command / method wrapping — <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/extensibility/method-wrapping-coc>

Combine Learn (syntax authority) with `d365fo` (real metadata: table/field/method names from THIS environment). Learn for "how is `while select` written"; `d365fo` for "does field `BalanceMST` exist on `CustTable`".

---

## 📦 See also

- [README.md](../README.md) — features, install, agent integration.
- [docs/SETUP.md](../docs/SETUP.md) — install + configure the CLI and bridge.
- [docs/EXAMPLES.md](../docs/EXAMPLES.md) — one worked example per command.
- [docs/MIGRATION_FROM_MCP.md](../docs/MIGRATION_FROM_MCP.md) — moving from `d365fo-mcp-server`.
- [docs/ROADMAP.md](../docs/ROADMAP.md) — what's still planned.
- [skills/](../skills/) — focused workflow skills (Copilot + Anthropic flavours) for `Claude Code`, GitHub Copilot, and other agent harnesses.
