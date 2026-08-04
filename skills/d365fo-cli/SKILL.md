---
name: d365fo-cli
description: |
  D365 Finance & Operations X++ AI development skill powered by the d365fo CLI.
  Use whenever the user is working in a D365 F&O X++ project: writing classes, tables,
  forms, CoC extensions, event handlers, entities, security, batch jobs, business events,
  labels, or any AOT artifact. Loads topic-specific guidance lazily from resources/.
applies_when: |
  User is working in a D365 Finance & Operations X++ project, using the d365fo CLI,
  or performing any AOT object authoring, extension, review, or scaffolding task.
---

# D365 Finance & Operations X++ Development — `d365fo` CLI

<!--
  Deployed to your X++ project via Install-D365FoCopilotSkills.ps1.
  Primary target: GitHub Copilot in Visual Studio 2022 / 2026 (agent mode with built-in tools).
  Secondary target: VS Code with Copilot in agent mode (can run d365fo directly via terminal).
  References in the form `[learn:<page>]` link to Microsoft Learn pages
  (see "Authoritative X++ syntax source" at the bottom).
-->

This skill gives **GitHub Copilot** the rules for assisting with D365 Finance & Operations X++ development. It is deployed to your X++ project's `.github/skills/d365fo-cli/` folder by `Install-D365FoCopilotSkills.ps1` and is loaded automatically by Copilot when you are working on D365 F&O tasks.

> **Primary environment — VS 2022 / VS 2026 agent mode:** GitHub Copilot runs `d365fo` commands via the built-in terminal tool (`run_command_in_terminal`). Topic-specific rules in `resources/` load on demand. No copy-paste, no MCP overhead.
>
> **Secondary environment — VS Code agent mode:** Same approach, different terminal tool name (`run_in_terminal`). Identical experience.
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
| **VS 2022/2026 agent mode** | Built-in terminal tool → `d365fo` CLI | ~100 tokens |
| **VS Code agent mode** | `run_in_terminal` → `d365fo` CLI | ~100 tokens |
| **VS Chat mode** (no agent tools) | User runs manually, pastes JSON | collaborative |

In agent mode Copilot calls `d365fo` commands autonomously — it reads topic rules from `resources/` in this skill, decides which commands to run, executes them in the terminal, and interprets the JSON output. No copy-paste required.

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
| Read class structure (methods, signatures) | `d365fo get class <Name> --output json` |
| Read X++ method body | `d365fo read class <Name> --method <M>` |
| Read table fields / indexes / relations | `d365fo get table <Name> --output json` |
| Read form controls / data sources | `d365fo get form <Name> --output json` |
| Search objects by name | `d365fo search class\|table\|form\|edt\|enum <query> --output json` |
| Multiple searches in one call | `d365fo search batch <q1> <q2> … --output json` |
| Multiple searches limited to one kind | `d365fo search batch <q1> <q2> … --kind class --output json` |
| Check existing CoC wrappers | `d365fo find coc <Class>::<method> --output json` |
| Find event handlers | `d365fo find event-handlers <Target> --output json` |
| Find label | `d365fo labels search "<text>" --output json` |
| Resolve label token | `d365fo labels resolve @SYS12345 --lang en-us,cs` |
| Trace security (Role → Duty → Privilege) | `d365fo security coverage <Role> --type Role --output json` |
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

## Key rules (condensed)

1. **Never create/edit AOT XML by hand or via scripts** (`Set-Content`, `Out-File`, `New-Item`, PS/Python scripts). If `d365fo` fails, stop and report — do not fall back to scripts.
2. **Never use code search / `@workspace` / `file_search` / `grep_search` on AOT XML.** Always `d365fo search`.
3. **Never auto-run `d365fo build`, `sync`, `bp check`, or `test run`** — slow, blocks the user. Only on explicit request.
4. **Never copy default parameter values into CoC wrapper signatures.** Causes compile errors.
5. **Never use `today()`** — use `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`.
6. **Never hardcode strings in `Info()` / `warning()` / `error()`** — use `@Model:Label`. Search first: `d365fo labels search`.
7. **Never nest `while select` loops** — use joins, `exists join`, or pre-load to `Map` / temp table.
8. **Never use `replace_string_in_file` on `.xml` AOT files without running `d365fo index refresh` after** — stale index returns pre-edit data.
9. **Never rewrite an existing AOT XML file wholesale.** Preserve all unrelated
   elements exactly. A diff that removes unrelated `<DataSourceModifications>`,
   `<DataSourceReferences>`, `<DataSources>`, `<Controls>`, methods, pattern
   metadata, or extension properties is a failed edit.
10. **Validate every changed AOT XML file before considering the task done:**
   parse it with an XML validator, run `d365fo validate xpp --file <file>
   --code-type xml-any --output json`, refresh the index for the model, and
   re-read the object with `d365fo get ... --output json`.
11. **For new forms based on an existing example, preserve the pattern contract.**
   First read the example with `d365fo get form <Example> --output json`; scaffold
   with `d365fo generate form --pattern ...`; then verify the generated form has
   the required pattern scaffolding (datasources, design pattern/version,
   ActionPane/Body/Tab/FastTab/grid/QuickFilter elements as applicable). Never
   drop required pattern elements just because they were not mentioned in the
   prompt.
12. **Form writes are pattern-gated.** `d365fo generate form` validates the
   generated XML against the pattern catalog (FP001–FP010) and **rejects
   structural violations** while `D365FO_FORM_PATTERN_ENFORCE=true` (default).
   Consult `d365fo form-pattern spec <Pattern>` for the required tree, and run
   `d365fo form-pattern validate <file>` after any manual form-XML edit.

---

## Full X++ rules — loaded on demand from resources

Detailed rules are in `resources/` (lazily loaded by Copilot when relevant):

| Resource file | Covers |
|---|---|
| `coc-extension-authoring` | CoC wrapper rules, `next` placement, signature matching, `[Hookable]`/`[Wrappable]` |
| `xpp-database-queries` | `select` grammar, `crossCompany`, `in` operator, joins, aggregates, SysDa, QueryRun |
| `x++-class-authoring` | Class structure, visibility, constructor patterns, extension methods, `var`, constants |
| `xpp-class-and-method-rules` | Method modifiers, override visibility, optional params, `this`, pass-by-value |
| `xpp-statement-and-type-rules` | `switch`, ternary, null handling, `using`, casting, `is`/`as` |
| `xpp-best-practice-rules` | BP rules: `today()`, labels, nested loops, alternate keys, `[SysObsolete]` |
| `form-pattern-scaffolding` | FormRun lifecycle, 9 form patterns, Display/Action/Output menu items |
| `table-scaffolding` | Table creation, EDT assignment, relations, indexes, number sequences, `TableGroup` vs `TableType` |
| `data-entity-scaffolding` | Data entity (`AxDataEntityView`) patterns, OData exposure |
| `event-handler-authoring` | `[DataEventHandler]`, `[SubscribesTo]`, pre/post handlers |
| `object-extension-authoring` | Table / Form / EDT / Enum extensions; new EDT and Enum creation |
| `security-hierarchy-trace` | Role → Duty → Privilege → Entry Point tracing; scaffold privilege/duty/role |
| `sysoperation-batch-patterns` | SysOperation batch jobs, RunBase/RunBaseBatch, data migration scripts |
| `business-events-authoring` | Custom business event contract + class scaffolding |
| `custom-service-authoring` | JSON/SOAP custom service scaffolding |
| `integration-patterns` | OData, DMF, business events, number sequences |
| `label-translation` | Label search, reuse, creation, multi-language |
| `model-dependency-and-coupling` | Model reference chains, ISV/customer boundary rules |
| `review-and-checkpoint-workflow` | Git checkpoint, `d365fo review diff`, accept/reject workflow |
