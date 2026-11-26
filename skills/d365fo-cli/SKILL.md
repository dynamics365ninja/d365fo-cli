---
name: d365fo-cli
description: "D365 Finance & Operations X++ AI development skill powered by the d365fo CLI. Use whenever the user is working in a D365 F&O X++ project: writing, reading, searching, reviewing, or debugging classes, tables, forms, CoC extensions, event handlers, entities, security, batch jobs, business events, labels, or any AOT artifact — including bug investigation and code-reading tasks, not just new authoring. Prefer `d365fo search`/`get`/`find` over generic grep/glob/file_search when locating or inspecting AOT objects. Loads topic-specific guidance lazily from references/."
compatibility: Requires GitHub Copilot agent mode with shell/terminal tool access and d365fo CLI in PATH — Visual Studio 2022 17.14+, Visual Studio 2026, VS Code, or the GitHub Copilot CLI (terminal-only sessions).
---

# D365 Finance & Operations X++ Development — `d365fo` CLI

<!--
  Deployed to your X++ project via Install-D365FoCopilotSkills.ps1.
  Primary target: GitHub Copilot in Visual Studio (2022 17.14+ or 2026), agent mode with built-in tools.
  The .instructions.md layout in skills/copilot/ is the fallback for Copilot builds that do not
  discover skills; the two layouts coexist.
  Secondary target: VS Code with Copilot in agent mode (can run d365fo directly via terminal).
  Also supported: GitHub Copilot CLI (terminal-only sessions, no IDE host) — same commands via
  the shell/powershell tool.
  References in the form `[learn:<page>]` link to Microsoft Learn pages
  (see "Authoritative X++ syntax source" at the bottom).
-->

This skill gives **GitHub Copilot** the rules for assisting with D365 Finance & Operations X++ development. It is deployed to your X++ project's `.github/skills/d365fo-cli/` folder by `Install-D365FoCopilotSkills.ps1` and is loaded automatically by Copilot when you are working on D365 F&O tasks — including reading, searching, reviewing, and debugging existing AOT objects, not only writing new ones.

> **Primary environment — Visual Studio agent mode (2022 17.14+ or 2026):** GitHub Copilot runs `d365fo` commands via the built-in terminal tool (`run_command_in_terminal`). Topic-specific rules in `references/` load on demand. No copy-paste, no MCP overhead. (The skills panel that lists discovered skills is VS 2026 18.6+ only; on VS 2022 the skill still loads — Copilot names it in its reply.)
>
> **Secondary environment — VS Code agent mode:** Same approach, different terminal tool name (`run_in_terminal`). Identical experience.
>
> **Also supported — GitHub Copilot CLI (terminal-only, no IDE host):** Same approach — the CLI's shell/`powershell` tool runs `d365fo` commands directly; no VS/VS Code host is required. The only prerequisite is `d365fo` being resolvable in PATH (confirm with `d365fo doctor`). The CLI finds project skills in `.github/skills/`, so start it in the folder the installer deployed to (next to the `.sln`), or copy the skill to the personal folder `~/.copilot/skills/`. These rules apply to **every** D365 F&O X++ task in a Copilot CLI session — bug investigation, code review and read-only searches as much as new code. Prefer `d365fo search`/`get`/`find`/`read` over generic `grep`/`glob`/`Get-ChildItem -Recurse` when locating or inspecting AOT XML: the CLI's model-aware index resolves enum/label/relation semantics that plain filesystem search cannot, and is far faster than recursive scans of `PackagesLocalDirectory`.
>
> **Fallback — VS Chat mode (no agent tools):** Copilot must ask the user to run `d365fo` commands manually and paste back JSON output. See the fallback workflow section below.

---

## Mandatory first steps

```sh
d365fo doctor --output json           # confirm index is healthy
d365fo models list --output json      # confirm target model — NEVER guess it
```

| Result | Action |
|---|---|
| `ok: false / NO_INDEX` | Run `d365fo index extract` first |
| `warnings: ["stale-index"]` | Run `d365fo index refresh` (incremental) |
| `⛔ CONFIGURATION PROBLEM` | Stop. Relay message to user. Wait. |
| Healthy + model confirmed | Note model name. Proceed. |

Models are ISV / customer policy boundaries — never infer from search results; always ask or read from `.rnrproj`.

### Model and existing-artifact selection

`D365FO_CUSTOM_MODELS` is a hard boundary for where customizations may be
installed. It can contain multiple comma-separated models. Before writing,
resolve the active target model from that list by checking the artifact the user
named, the model that already contains the related extension/handler, or the
model currently being edited in the task. Use `--install-to <ActiveCustomModel>`
only after that resolution. If more than one custom model could own the change,
stop and ask.

The artifact suffix is a separate decision from the model name. Extract
`<ExistingSuffix>` from existing related elements in the active model, such as
`<Target>_<ExistingSuffix>_Extension`, `<Form>_<ExistingSuffix>_Form_EH`, or
`<Form>_<ExistingSuffix>_Form_EventHandler`. If no existing suffix can be
derived and the user did not provide one, stop and ask for the suffix. Do
**not** derive new suffixes from feature names, customer names, tickets, labels,
or the custom model name.

Before creating any extension or event-handler class, search for an existing
artifact in the target custom model and reuse it when it already represents
the same target object or integration family:

```sh
d365fo find extensions <TargetObject> --output json
d365fo find event-handlers <TargetObject> --output json
d365fo search class <TargetOrPrefix> --output json
```

Examples:
- If `<Target>_<ExistingSuffix>_Extension` exists in `<ActiveCustomModel>`, add
  the new method there. Do not create `<Target>_<Feature>_Extension`.
- If `<Form>_<ExistingSuffix>_Form_EH` or
  `<Form>_<ExistingSuffix>_Form_EventHandler` exists for `<Form>` events in
  `<ActiveCustomModel>`, add the new handler there. Do not create a parallel
  `<Form>_<Feature>_EH` or `<Form>_<Feature>_EventHandler` unless the user
  explicitly requests a separate handler class.
- Feature names, integration names, customer names, and issue titles are allowed
  in method names and labels, but they are not a reason to create a new
  suffix when a related artifact already exists.

### VS / VS Code operating modes

| Environment | How Copilot runs d365fo | Token cost |
|---|---|---|
| **VS 2026 agent mode** | Built-in terminal tool → `d365fo` CLI | ~100 tokens |
| **VS Code agent mode** | `run_in_terminal` → `d365fo` CLI | ~100 tokens |
| **GitHub Copilot CLI** (terminal-only) | `powershell`/shell tool → `d365fo` CLI | ~100 tokens |
| **VS Chat mode** (no agent tools) | User runs manually, pastes JSON | collaborative |

In agent mode Copilot calls `d365fo` commands autonomously — it reads topic rules from `references/` in this skill, decides which commands to run, executes them in the terminal, and interprets the JSON output. No copy-paste required.

### ⛔ Chat mode only (no agent tools) — fallback workflow

If Copilot is running in **chat mode without agent tools** (no tool list visible), it cannot call `d365fo` directly. The built-in code search / `@workspace` **always fails on AOT XML** — do not attempt it.

```
// ❌ WRONG — do not attempt this
Copilot: "Let me search for existing table examples in your codebase…"
         → "There was an error executing code search"
Copilot: "Since I cannot access the codebase, I'll provide generic guidance…"
         → hallucinated X++ templates
```

- ❌ **Never** attempt code search / `@workspace` on a D365FO project.
- ❌ **Never** say "Since I cannot access the codebase" and fall back to generic guidance.

**Instead:** ask the user to run the required `d365fo` commands in their Developer PowerShell and paste back the JSON output before you proceed with code generation.

---

## Core tool mapping

| Need | Command |
|---|---|
| Read class structure (methods, signatures) | `d365fo get class <Name> --brief --output json` first (signatures, no bodies), `get method <Name> <M>` for one body |
| Read X++ method body | `d365fo get method <Owner> <M>` (class, table or form) or `d365fo read class <Name> --method <M>` |
| Read table fields / indexes / relations | `d365fo get table <Name> --brief --output json` first, the full `get table` (or `--include <parts>`) when you need labels, field groups or methods |
| Read form controls / data sources | `d365fo get form <Name> --brief --output json` first (data sources, pattern, control outline), the full `get form` when you need control properties |
| Search objects by name | `d365fo search class\|table\|form\|edt\|enum <query> --output json` |
| Multiple searches in one call | `d365fo search batch <q1> <q2> … --output json` |
| Multiple searches limited to one kind | `d365fo search batch <q1> <q2> … --kind class --output json` |
| Check existing CoC wrappers | `d365fo find coc <Class>::<method> --output json` |
| Find event handlers | `d365fo find event-handlers <Target> --output json` |
| Who uses X — calls, reads, writes, extends (where-used) | `d365fo find refs <X> --xref --output json` — the compiler's xref, one row per object; `Owner.member` for one method/field. No XRef DB: drop `--xref` for the text scan, which can miss references |
| Where X is bound in metadata — form/query data sources, relations, field EDTs, menu items, security, extensions | `d365fo find where-used <X> --output json` — from the index, no build; `--kind Form` (or Query, MenuItem, Security…) narrows, `byVia` counts come first |
| Find label | `d365fo labels search "<text>" --output json` |
| Resolve label token | `d365fo labels resolve @SYS12345 --lang en-us,cs` |
| Trace security (Role → Duty → Privilege) | `d365fo security <Name> --output json` (a role, duty or privilege, or the coverage of an object) |
| Find table relations | `d365fo find relations <Table> --output json` |
| Fetch several objects at once | `d365fo get batch table:CustTable class:CustTableType … --output json` (max 10) |
| Form pattern spec (required structure) | `d365fo form-pattern spec <Pattern> --output json` |
| Validate form XML against its pattern | `d365fo form-pattern validate <file> --output json` |
| Create new AOT object | `d365fo generate table\|class\|form\|coc\|entity\|edt\|enum … --install-to <Model>` |
| Edit method body (CDATA only) | `replace_string_in_file` — then `d365fo index refresh --model <Model>` |
| Structural change (add field, index, relation) | `d365fo generate … --overwrite` — NEVER `replace_string_in_file` on XML structure |
| Discover all CLI commands | `d365fo schema --output json` |
| Index health check | `d365fo doctor --output json` |

> **`--output json` is mandatory** in agent contexts. Write-path: `--install-to <Model>` for bridge-installed scaffolds; `--out <PATH>` for standalone. Generated files land at `PackagesLocalDirectory/<Model>/<Model>/Ax<Type>/<Name>.xml`.

---
## Key rules — driving this CLI

These are the rules about *how to use the tooling*. The X++ rule canon itself is
generated below from `skills/_source`, so it cannot drift from the topic files or
from the `d365fo agent-prompt` / MCP server instructions.

1. **Never create/edit AOT XML by hand or via scripts** (`Set-Content`, `Out-File`, `New-Item`, PS/Python scripts). If `d365fo` fails, stop and report — do not fall back to scripts.
2. **Never use code search / `@workspace` / `file_search` / `grep_search` on AOT XML.** Always `d365fo search`.
3. **Never use `replace_string_in_file` on `.xml` AOT files without running `d365fo index refresh` after** — a stale index returns pre-edit data.
4. **Form writes are pattern-gated.** `d365fo generate form` validates the generated XML against the pattern catalog (FP001–FP010) and **rejects structural violations** while `D365FO_FORM_PATTERN_ENFORCE=true` (default). Consult `d365fo form-pattern spec <Pattern>` for the required tree, and run `d365fo form-pattern validate <file>` after any manual form-XML edit.
5. **For new forms based on an existing example, preserve the pattern contract.** Read the example with `d365fo get form <Example> --output json`, scaffold with `d365fo generate form --pattern ...`, then verify the required datasources, design pattern/version and ActionPane/Body/Tab/FastTab/grid/QuickFilter elements survived. Never drop required pattern elements just because the prompt did not mention them.
6. **Triage build output with `d365fo explain-error`** rather than reading the log by eye — it returns the ranked cause plus the knowledge topic behind each message.

---

## X++ rule canon

> Generated from `skills/_source` by `scripts/emit-skills.py`. Do not edit by hand —
> edit the topic that owns the rule and re-run the script. CI fails on drift.

<!-- BEGIN canon -->
### Never-auto

- NEVER auto-run `d365fo build`, `sync`, `bp check`, `test run`. Slow + Windows-only.
  Say *"Changes scaffolded. Run `d365fo build` when you're ready."*
- NEVER hand-edit AOT XML when `index refresh` hasn't been run.
- NEVER infer the target model from search results — ask.

### Non-negotiable X++ rules

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

### Chain of Command

**NEVER copy default parameter values into the wrapper signature.** The base
method's defaults are already in effect when `next` runs.

- Wrapper must call `next` unconditionally (exception: `[Replaceable]`).
- `next` at first-level scope — NOT in `if`/`while`/`for`/`do-while`/boolean
  expressions/after `return`. PU21+: `try`/`catch`/`finally` allowed.
- Signature otherwise matches base EXACTLY.
- Static methods: repeat `static`. Forms cannot be wrapped statically.
- Cannot wrap constructors.
- Class shape: `[ExtensionOf(...)] final class <Target>_<Suffix>`.
- `[Hookable(false)]` blocks all CoC + handlers.
- `[Wrappable(false)]` blocks wrapping; allows handlers.
- Form-nested wrapping (`formdatasourcestr`, `formdatafieldstr`,
  `formControlStr`) cannot ADD new methods.
- Wrappers can read `protected` (PU9+); not `private`.
- Reuse existing target/model extension and handler classes before creating new
  ones. If no existing suffix can be derived and the user did not provide one,
  ask; do not create parallel feature-named artifacts unless the user explicitly
  requests that separation.

### AOT XML safety

- Never rewrite an existing AOT XML file wholesale. Preserve unrelated
  `<DataSourceModifications>`, `<DataSourceReferences>`, `<DataSources>`,
  `<Controls>`, methods, extension properties, and pattern metadata.
- Validate every changed XML file: XML parser first, then
  `d365fo validate xpp --file <f> --code-type xml-any --output json`, then
  `d365fo index refresh --model <Model>`, then re-read the object with
  `d365fo get ... --output json`.
- For new forms based on an example, read the example and keep the same pattern
  contract unless the user asked otherwise. Required pattern controls/datasources
  are mandatory, not optional inspiration.

### Best practice — must pass `d365fo bp check`

- `BPUpgradeCodeToday` — never `today()`.
- `BPErrorLabelIsText` — `info`/`warning`/`error` need labels.
- `BPErrorEDTNotMigrated` — modern `EDT.Relations` element only.
- `BPCheckNestedLoopinCode` — no nested `while select`.
- `BPCheckAlternateKeyAbsent` — every table needs a unique alternate key.
- `BPErrorUnknownLabel` — referenced labels must exist.
- `BPXmlDocNoDocumentationComments` — meaningful `/// <summary>`.
- `BPDuplicateMethod` — no duplicates on the inheritance chain.
<!-- END canon -->

---

## Full X++ rules — loaded on demand from references

Detailed rules are in the `references/` folder next to this file, one file per topic:
`references/<topic>.md`, which an installed skill has at `.github/skills/d365fo-cli/references/<topic>.md`
in the X++ repo. Open a topic's file when the task touches it. Do not look for `.github/<topic>.md`:
that file does not exist.

If the file tool cannot open the file (in Visual Studio it may not see files outside the
solution, and `.github/` usually is), run `d365fo knowledge get <topic>` in the
terminal. Its `body` field holds the same text, and it works from any folder.

<!-- BEGIN references -->
| Resource file | Covers |
|---|---|
| [`analytics-and-er`](references/analytics-and-er.md) | Tiles/cues, KPIs, aggregate measurements, Electronic Reporting |
| [`barcode-scanning`](references/barcode-scanning.md) | barcode printing vs scanning, GS1-128 parsing, item-barcode resolution |
| [`build-error-triage`](references/build-error-triage.md) | Compiler/BP/runtime message to the specific fix, via `explain-error` |
| [`business-events-authoring`](references/business-events-authoring.md) | `BusinessEventsBase`, contract class, payload, catalog activation |
| [`coc-extension-authoring`](references/coc-extension-authoring.md) | CoC wrapper rules, `next` placement, signature matching, `[Hookable]`/`[Wrappable]` |
| [`custom-service-authoring`](references/custom-service-authoring.md) | JSON/SOAP custom service scaffolding |
| [`data-entity-scaffolding`](references/data-entity-scaffolding.md) | Data entity (`AxDataEntityView`) patterns, OData exposure |
| [`document-attachments`](references/document-attachments.md) | DocuRef, DocuValue, DocuType, DocumentManagement::attachFile, attachment retrieval |
| [`event-handler-authoring`](references/event-handler-authoring.md) | `[DataEventHandler]`, `[SubscribesTo]`, pre/post handlers |
| [`form-pattern-scaffolding`](references/form-pattern-scaffolding.md) | FormRun lifecycle, 9 form patterns, Display/Action/Output menu items |
| [`forms-and-navigation`](references/forms-and-navigation.md) | Form lifecycle extension points, menus and submenu nesting |
| [`global-class-statics`](references/global-class-statics.md) | Global statics, bare vs qualified calls, overlap with predefined functions |
| [`integration-dmf-dualwrite`](references/integration-dmf-dualwrite.md) | DMF, dual-write, virtual entities, uploaded-file readers |
| [`integration-patterns`](references/integration-patterns.md) | OData, custom services, DMF, business events |
| [`inventory-and-warehouse`](references/inventory-and-warehouse.md) | InventTrans/InventDim/InventSum, on-hand, WHS work and waves |
| [`label-translation`](references/label-translation.md) | Label search, reuse, creation, multi-language |
| [`model-dependency-and-coupling`](references/model-dependency-and-coupling.md) | Model reference chains, ISV/customer boundary rules |
| [`number-sequence-patterns`](references/number-sequence-patterns.md) | `NumberSeqApplicationModule`, form handler, runtime fetch |
| [`object-extension-authoring`](references/object-extension-authoring.md) | Table / Form / EDT / Enum extensions; AOT XML safety |
| [`performance-and-caching`](references/performance-and-caching.md) | Set-based work, `CacheLookup`, explicit caches, parallel batch |
| [`posting-and-financials`](references/posting-and-financials.md) | Financial dimensions, voucher posting, currency, pricing |
| [`rdl-design-expressions`](references/rdl-design-expressions.md) | RDL expression syntax, aggregate scope, formatting, where RDL lives |
| [`report-print-destinations`](references/report-print-destinations.md) | SRSPrintDestinationSettings, SRSPrintMediumType, setting a destination on a controller |
| [`review-and-checkpoint-workflow`](references/review-and-checkpoint-workflow.md) | Git checkpoint, `d365fo review diff`, accept/reject workflow |
| [`runtime-frameworks`](references/runtime-frameworks.md) | Feature Management, SysExtension, telemetry, Global Address Book |
| [`security-hierarchy-trace`](references/security-hierarchy-trace.md) | Role to duty to privilege to entry-point tracing |
| [`security-modeling`](references/security-modeling.md) | Privilege/duty/role chain, XDS policies, configuration keys |
| [`ssrs-report-authoring`](references/ssrs-report-authoring.md) | TmpTable to Contract to DP to Controller to AxReport, Print management |
| [`sysoperation-batch-patterns`](references/sysoperation-batch-patterns.md) | SysOperation batch jobs, RunBase, migration scripts, retryable/async batch |
| [`system-objects`](references/system-objects.md) | kernel types, xRecord/Common members, system fields, why the index cannot carry them |
| [`table-scaffolding`](references/table-scaffolding.md) | Table creation, EDTs, relations, indexes, `TableGroup` vs `TableType`, inheritance, validation and defaulting methods |
| [`testing-and-quality`](references/testing-and-quality.md) | SysTestCase, ATL, and the offline validate/lint gates |
| [`transactions-and-concurrency`](references/transactions-and-concurrency.md) | tts scoping, OCC retry, `UnitOfWork`, error handling |
| [`warehouse-mobile-app`](references/warehouse-mobile-app.md) | warehouse-app screens, ProcessGuide flows, legacy WHSWorkExecuteDisplay, work execution |
| [`workflow-authoring`](references/workflow-authoring.md) | `AxWorkflowTemplate`, document class, approvals and tasks |
| [`x++-class-authoring`](references/x++-class-authoring.md) | Class hierarchy, CoC, access modifiers, constructor patterns |
| [`xpp-best-practice-rules`](references/xpp-best-practice-rules.md) | BP rules: `today()`, labels, nested loops, alternate keys, `[SysObsolete]` |
| [`xpp-class-and-method-rules`](references/xpp-class-and-method-rules.md) | Method modifiers, override visibility, optional params, `this` |
| [`xpp-data-access-apis`](references/xpp-data-access-apis.md) | `Query`/`QueryRun`, SysDa, direct SQL |
| [`xpp-database-queries`](references/xpp-database-queries.md) | `select` grammar, `crossCompany`, `in`, joins, aggregates |
| [`xpp-runtime-functions`](references/xpp-runtime-functions.md) | predefined function catalog, arities, gone/obsolete names |
| [`xpp-runtime-types`](references/xpp-runtime-types.md) | Collections, date/time zones, .NET interop, `Dict*`, macros |
| [`xpp-statement-and-type-rules`](references/xpp-statement-and-type-rules.md) | `switch`, ternary, null handling, `using`, casting, `is`/`as` |
<!-- END references -->
