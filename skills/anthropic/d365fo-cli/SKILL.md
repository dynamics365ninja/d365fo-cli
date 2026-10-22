---
name: d365fo-cli
description: "D365 Finance & Operations X++ AI development skill powered by the d365fo CLI. Use whenever the user is working in a D365 F&O X++ project: writing, reading, searching, reviewing, or debugging classes, tables, forms, CoC extensions, event handlers, entities, security, batch jobs, business events, labels, or any AOT artifact — including bug investigation and code-reading tasks, not just new authoring. Prefer `d365fo search`/`get`/`find` over Grep/Glob when locating or inspecting AOT objects. Points to the topic-specific d365fo skills installed next to it."
---

# D365 Finance & Operations X++ Development — `d365fo` CLI

<!--
  Deployed to your X++ project's .claude/skills/d365fo-cli/ by Install-D365FoClaudeSkills.ps1,
  next to one skill per knowledge topic (.claude/skills/<topic>/SKILL.md).
  Target: Claude Code (CLI, VS Code and JetBrains extensions) and Claude Desktop.
  This is the Anthropic-format sibling of skills/d365fo-cli/SKILL.md (the Copilot router).
  The canon and topic-table regions are generated from skills/_source by
  scripts/emit-skills.py; everything outside them is hand-written.
-->

This skill gives **Claude** the rules for assisting with D365 Finance & Operations X++ development. It is the entry point: it says which `d365fo` commands to run and which rules always apply, and the table at the bottom names the topic skill to load for each kind of task. Apply it to **every** D365 F&O X++ task — bug investigation, code review and read-only questions as much as new code.

> **How Claude runs `d365fo`:** through the shell tool (Bash, or PowerShell on Windows). The only prerequisite is `d365fo` being resolvable in PATH — confirm with `d365fo doctor`. If it is not, stop and ask the user to install it (see docs/SETUP.md in the d365fo-cli repository); do not fall back to generic file search or generic X++ guidance.
>
> **Prefer `d365fo search`/`get`/`find`/`read` over Grep, Glob or recursive `ls`/`Get-ChildItem`** when locating or inspecting AOT XML: the CLI's model-aware index resolves enum/label/relation semantics that plain filesystem search cannot, and is far faster than recursive scans of `PackagesLocalDirectory`.

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

---

## Core tool mapping

| Need | Command |
|---|---|
| Read class structure (methods, signatures) | `d365fo get class <Name> --output json` |
| Read X++ method body | `d365fo read class <Name> --method <M>` |
| Read table fields / indexes / relations | `d365fo get table <Name> --output json` |
| Read form controls / data sources | `d365fo get form <Name> --output json` |
| Search objects by name | `d365fo search class\|table\|form\|edt\|enum <query> --output json` |
| Multiple searches in one call | `d365fo search batch <q1> <q2> … --output json` |
| Multiple searches limited to one kind | `d365fo search batch <q1> <q2> … --kind class --output json` |
| Check existing CoC wrappers | `d365fo find coc <Class>::<method> --output json` |
| Find event handlers | `d365fo find event-handlers <Target> --output json` |
| Who uses X — calls, reads, writes, extends (where-used) | `d365fo find refs <X> --xref --output json` — the compiler's xref, one row per object; `Owner.member` for one method/field. No XRef DB: drop `--xref` for the text scan, which can miss references |
| Find label | `d365fo labels search "<text>" --output json` |
| Resolve label token | `d365fo labels resolve @SYS12345 --lang en-us,cs` |
| Trace security (Role → Duty → Privilege) | `d365fo security coverage <Role> --type Role --output json` |
| Find table relations | `d365fo find relations <Table> --output json` |
| Fetch several objects at once | `d365fo get batch table:CustTable class:CustTableType … --output json` (max 10) |
| Form pattern spec (required structure) | `d365fo form-pattern spec <Pattern> --output json` |
| Validate form XML against its pattern | `d365fo form-pattern validate <file> --output json` |
| Create new AOT object | `d365fo generate table\|class\|form\|coc\|entity\|edt\|enum … --install-to <Model>` |
| Edit method body (CDATA only) | Edit tool — then `d365fo index refresh --model <Model>` |
| Structural change (add field, index, relation) | `d365fo generate … --overwrite` — NEVER the Edit tool on XML structure |
| Discover all CLI commands | `d365fo schema --output json` |
| Index health check | `d365fo doctor --output json` |

> **`--output json` is mandatory** in agent contexts. Write-path: `--install-to <Model>` for bridge-installed scaffolds; `--out <PATH>` for standalone. Generated files land at `PackagesLocalDirectory/<Model>/<Model>/Ax<Type>/<Name>.xml`.

---
## Key rules — driving this CLI

These are the rules about *how to use the tooling*. The X++ rule canon itself is
generated below from `skills/_source`, so it cannot drift from the topic skills or
from the `d365fo agent-prompt` / MCP server instructions.

1. **Never create/edit AOT XML by hand or via scripts** (Write tool, `Set-Content`, `Out-File`, `New-Item`, shell redirection, PS/Python scripts). If `d365fo` fails, stop and report — do not fall back to scripts.
2. **Never use Grep / Glob / recursive `ls` on AOT XML to locate objects.** Always `d365fo search`.
3. **Never use the Edit tool on `.xml` AOT files without running `d365fo index refresh` after** — a stale index returns pre-edit data.
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

## Topic skills — load the one that matches the task

Each topic is its own skill in this folder's parent (`.claude/skills/<topic>/`).
Load the matching one before writing or reviewing code in that area:

<!-- BEGIN references -->
| Skill | Covers |
|---|---|
| [`analytics-and-er`](../analytics-and-er/SKILL.md) | Tiles/cues, KPIs, aggregate measurements, Electronic Reporting |
| [`barcode-scanning`](../barcode-scanning/SKILL.md) | barcode printing vs scanning, GS1-128 parsing, item-barcode resolution |
| [`build-error-triage`](../build-error-triage/SKILL.md) | Compiler/BP/runtime message to the specific fix, via `explain-error` |
| [`business-events-authoring`](../business-events-authoring/SKILL.md) | `BusinessEventsBase`, contract class, payload, catalog activation |
| [`coc-extension-authoring`](../coc-extension-authoring/SKILL.md) | CoC wrapper rules, `next` placement, signature matching, `[Hookable]`/`[Wrappable]` |
| [`custom-service-authoring`](../custom-service-authoring/SKILL.md) | JSON/SOAP custom service scaffolding |
| [`data-entity-scaffolding`](../data-entity-scaffolding/SKILL.md) | Data entity (`AxDataEntityView`) patterns, OData exposure |
| [`document-attachments`](../document-attachments/SKILL.md) | DocuRef, DocuValue, DocuType, DocumentManagement::attachFile, attachment retrieval |
| [`event-handler-authoring`](../event-handler-authoring/SKILL.md) | `[DataEventHandler]`, `[SubscribesTo]`, pre/post handlers |
| [`form-pattern-scaffolding`](../form-pattern-scaffolding/SKILL.md) | FormRun lifecycle, 9 form patterns, Display/Action/Output menu items |
| [`forms-and-navigation`](../forms-and-navigation/SKILL.md) | Form lifecycle extension points, menus and submenu nesting |
| [`global-class-statics`](../global-class-statics/SKILL.md) | Global statics, bare vs qualified calls, overlap with predefined functions |
| [`integration-dmf-dualwrite`](../integration-dmf-dualwrite/SKILL.md) | DMF, dual-write, virtual entities, uploaded-file readers |
| [`integration-patterns`](../integration-patterns/SKILL.md) | OData, custom services, DMF, business events |
| [`inventory-and-warehouse`](../inventory-and-warehouse/SKILL.md) | InventTrans/InventDim/InventSum, on-hand, WHS work and waves |
| [`label-translation`](../label-translation/SKILL.md) | Label search, reuse, creation, multi-language |
| [`model-dependency-and-coupling`](../model-dependency-and-coupling/SKILL.md) | Model reference chains, ISV/customer boundary rules |
| [`number-sequence-patterns`](../number-sequence-patterns/SKILL.md) | `NumberSeqApplicationModule`, form handler, runtime fetch |
| [`object-extension-authoring`](../object-extension-authoring/SKILL.md) | Table / Form / EDT / Enum extensions; AOT XML safety |
| [`performance-and-caching`](../performance-and-caching/SKILL.md) | Set-based work, `CacheLookup`, explicit caches, parallel batch |
| [`posting-and-financials`](../posting-and-financials/SKILL.md) | Financial dimensions, voucher posting, currency, pricing |
| [`rdl-design-expressions`](../rdl-design-expressions/SKILL.md) | RDL expression syntax, aggregate scope, formatting, where RDL lives |
| [`report-print-destinations`](../report-print-destinations/SKILL.md) | SRSPrintDestinationSettings, SRSPrintMediumType, setting a destination on a controller |
| [`review-and-checkpoint-workflow`](../review-and-checkpoint-workflow/SKILL.md) | Git checkpoint, `d365fo review diff`, accept/reject workflow |
| [`runtime-frameworks`](../runtime-frameworks/SKILL.md) | Feature Management, SysExtension, telemetry, Global Address Book |
| [`security-hierarchy-trace`](../security-hierarchy-trace/SKILL.md) | Role to duty to privilege to entry-point tracing |
| [`security-modeling`](../security-modeling/SKILL.md) | Privilege/duty/role chain, XDS policies, configuration keys |
| [`ssrs-report-authoring`](../ssrs-report-authoring/SKILL.md) | TmpTable to Contract to DP to Controller to AxReport, Print management |
| [`sysoperation-batch-patterns`](../sysoperation-batch-patterns/SKILL.md) | SysOperation batch jobs, RunBase, migration scripts, retryable/async batch |
| [`system-objects`](../system-objects/SKILL.md) | kernel types, xRecord/Common members, system fields, why the index cannot carry them |
| [`table-scaffolding`](../table-scaffolding/SKILL.md) | Table creation, EDTs, relations, indexes, `TableGroup` vs `TableType`, inheritance |
| [`testing-and-quality`](../testing-and-quality/SKILL.md) | SysTestCase, ATL, and the offline validate/lint gates |
| [`transactions-and-concurrency`](../transactions-and-concurrency/SKILL.md) | tts scoping, OCC retry, `UnitOfWork`, error handling |
| [`warehouse-mobile-app`](../warehouse-mobile-app/SKILL.md) | warehouse-app screens, ProcessGuide flows, legacy WHSWorkExecuteDisplay, work execution |
| [`workflow-authoring`](../workflow-authoring/SKILL.md) | `AxWorkflowTemplate`, document class, approvals and tasks |
| [`x++-class-authoring`](../x++-class-authoring/SKILL.md) | Class hierarchy, CoC, access modifiers, constructor patterns |
| [`xpp-best-practice-rules`](../xpp-best-practice-rules/SKILL.md) | BP rules: `today()`, labels, nested loops, alternate keys, `[SysObsolete]` |
| [`xpp-class-and-method-rules`](../xpp-class-and-method-rules/SKILL.md) | Method modifiers, override visibility, optional params, `this` |
| [`xpp-data-access-apis`](../xpp-data-access-apis/SKILL.md) | `Query`/`QueryRun`, SysDa, direct SQL |
| [`xpp-database-queries`](../xpp-database-queries/SKILL.md) | `select` grammar, `crossCompany`, `in`, joins, aggregates |
| [`xpp-runtime-functions`](../xpp-runtime-functions/SKILL.md) | predefined function catalog, arities, gone/obsolete names |
| [`xpp-runtime-types`](../xpp-runtime-types/SKILL.md) | Collections, date/time zones, .NET interop, `Dict*`, macros |
| [`xpp-statement-and-type-rules`](../xpp-statement-and-type-rules/SKILL.md) | `switch`, ternary, null handling, `using`, casting, `is`/`as` |
<!-- END references -->
