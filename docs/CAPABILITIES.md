# d365fo-cli — Capabilities & Feature Overview

Full reference of what the tool provides. For setup see [SETUP.md](SETUP.md), for worked examples see [EXAMPLES.md](EXAMPLES.md), for internals see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## Index

The SQLite index (`$D365FO_INDEX_DB`) stores AOT metadata extracted from `PackagesLocalDirectory`. It covers 22 AOT object types and is populated by `d365fo index extract`.

By default the index stores object/method *metadata* only — method bodies (the X++ source) are parsed for lint flags and then discarded; the canonical source stays in the AOT XML on disk. Pass `--index-source` to additionally full-text index method bodies into `MethodSourceFts` (see below).

### Index maintenance commands

| Command | What it does |
|---------|-------------|
| `index build` | Create or migrate the schema in-place |
| `index extract` | Full extraction of all packages |
| `index extract --model <M>` | Scoped extraction of one model (seconds) |
| `index extract --index-source` | Also full-text index X++ method bodies (opt-in; enlarges the DB, accelerates `find refs`). Also available on `index refresh`. |
| `index refresh` | Re-extract only models whose content fingerprint changed |
| `index refresh --force` | Re-extract all models regardless of fingerprint |
| `index status` | Show per-model row counts and last-extracted timestamps |
| `index history` | Show per-model extraction run history |
| `index export` | Dump the index to a portable archive |
| `index import` | Restore from an archive |
| `index optimize` | Run `VACUUM` + `ANALYZE` to compact and re-plan |
| `doctor` | End-to-end health check: paths, schema version, object counts |

---

## Search & Discovery

### `search`

Fuzzy substring search across every indexed type.

```
search table|class|edt|enum|form|menu-item|query|view|entity|report
search service|service-group|workflow|map|label|role|duty|privilege
search business-event|security-policy|configuration-key|tile|workspace
search batch-jobs
search any <query>   — UNIONs all types, returns byKind counts
```

### `get`

Full metadata for one named object. Returns a structured JSON envelope.

```
get table|class|edt|enum|form|menu-item|query|view|entity|report
get service|service-group|map|role|duty|privilege|label
get business-event|security-policy
```

`get table <T> --odata-metadata` emits an OData `<EntityType>` + `<EntitySet>` XML fragment for the table.

### `security`

Security hierarchy, mirroring the MCP `security_info` tool. (`get role|duty|privilege` and `get security` remain as aliases.)

```
security role <Name>           — Role: duties + privileges
security duty <Name>           — Duty: privileges
security privilege <Name>      — Privilege: entry points
security coverage <Object>     — Role → Duty → Privilege routes that reach an object
```

### `form-pattern`

Form-pattern advisor, spec catalog, and structural validator (mirrors the MCP `object_patterns` tool, `domain=form`).

```
form-pattern analyze [--pattern P|--table T|--similar-to Form]  — pattern histogram / advisor
form-pattern spec [Name]        — required structure tree, versions, reference forms
form-pattern validate [File]    — FP001–FP010 structural validation of AxForm XML
form-pattern repair [File]      — auto-fix the deterministic violations; dry-run unless --apply/--out
```

`repair` applies the fixes the validator already describes, and only those with a
single correct outcome: insert a missing required control (FP003), reorder the
Design root into spec order (FP005), pin `PatternVersion` (FP002), reset a drifted
pattern-default property (FP009), apply an unambiguous container sub-pattern (FP006),
and adopt an unpatterned form into `--pattern X` (FP010). It never deletes a control:
a disallowed child (FP004), a sub-pattern on the wrong control type (FP007), a
datasource gap (FP008), or a container with several candidate sub-patterns are
reported under `skipped` with the reason, for a human to resolve. Exit code 2 when
violations remain.

### `find`

Cross-reference queries.

```
find coc <Class> [--method <M>]       — Chain-of-Command wrappers
find usages <needle> [--kind k,k,…]  — All references by kind
find extensions <target>              — All object extensions
find handlers <object>                — Event subscribers
find relations <table>                — FK relations
find refs <needle>                    — References inside method bodies (FTS5 when `--index-source` was used, else on-demand source scan)
find refs --xref                      — Path/line/kind via DYNAMICSXREFDB
find form-patterns [--pattern P]      — Form pattern histogram / filter
find batch-jobs [--model M]           — RunBaseBatch subclasses
```

### `models`

```
models list                   — All indexed models with publisher/layer/custom flag
models deps <Model>           — Dependency tree
models coupling [--top N]     — Fan-in / fan-out / instability (Martin metric)
models coupling --only-cycles — Tarjan SCC: circular dependencies only
```

### `stats`

Per-model object counts + top-N tables by field count and classes by method count.

```
stats [--top N]
stats --perf     — Command timing percentiles from local telemetry
```

### `read`

Pull X++ source snippets directly from AOT XML — no compiler or VM needed.

```
read class <C> --method <M>
read table <T> [--declaration]
read form <F> [--lines 10-40]
```

---

## Lint

`d365fo lint` runs 16 in-process heuristics against the index without touching the VM.

```sh
d365fo lint                                         # all rules, custom models only
d365fo lint --all-models                            # include MS/ISV content
d365fo lint --category insert-in-loop,force-literals  # specific rules
d365fo lint --format sarif > lint.sarif             # SARIF 2.1.0 for CI
```

### Rule catalogue

| Rule | Finds | Severity |
|------|-------|---------|
| `table-no-index` | Tables without cluster or alternate-key index | warning |
| `ext-named-not-attributed` | `*_Extension` classes missing `[ExtensionOf]` | warning |
| `string-without-edt` | String fields without an EDT | warning |
| `today-usage` | `today()` calls (`BPUpgradeCodeToday`) | warning |
| `do-insert-update` | `doInsert()` / `doUpdate()` / `doDelete()` in non-migration code | warning |
| `doc-comment-missing` | Public/protected methods without `/// <summary>` | warning |
| `nested-select` | `while select` nested inside another loop | warning |
| `insert-in-loop` | `.insert()` inside a loop body — suggest `RecordInsertList` | warning |
| `tts-try-catch` | `try` inside `ttsbegin`/`ttscommit` without catching `UpdateConflict` | warning |
| `empty-table-method` | Table method override with empty body | warning |
| `batch-no-cango` | `RunBaseBatch` subclass without `canGoBatch() { return true; }` | warning |
| `force-literals` | `forceLiterals` in a select — SQL injection risk | error |
| `public-instance-field` | Public instance fields on a class — violates encapsulation | warning |
| `cache-lookup-mismatch` | `CacheLookup` inconsistent with `TableGroup` | warning |
| `missing-delete-action` | Table relation without `DeleteAction` or `OnDelete` | warning |
| `no-alternate-key` | Tables with unique indexes but no `AlternateKey` | warning |

---

## Scaffolding (`generate`)

All scaffolders write atomically (`.tmp` + move, `.bak` on overwrite). Pass `--install-to <Model>` to drop straight into a model folder via the Bridge.

### AOT object types

| Command | Emits |
|---------|-------|
| `generate table` | `AxTable` XML with fields, indexes, relations (9 patterns) |
| `generate class` | `AxClass` skeleton |
| `generate coc` | Chain-of-Command extension class |
| `generate form` | `AxForm` XML (9 patterns: SimpleList, DetailsMaster, Workspace …) |
| `generate datasource-method` | Add/override a method on a form datasource (form-level `SourceCode`); `--list` shows overridable methods |
| `generate control-method` | Add/override a method on a form control (form-level `SourceCode`); `--list` shows overridable methods |
| `generate entity` | `AxDataEntityView` (`DataManagementEnabled=No` by default; opt in via `--data-management`) |
| `generate extension` | `AxTableExtension` / `AxFormExtension` / `AxEdtExtension` / `AxEnumExtension` / `AxSecurityDutyExtension` / `AxSecurityRoleExtension` |
| `generate event-handler` | X++ event subscriber class with correct attribute |
| `generate privilege` | `AxSecurityPrivilege` (entry point and/or `--data-entity` OData/DMF grant) |
| `generate duty` | `AxSecurityDuty` |
| `generate role` | `AxSecurityRole` (new or merge into existing) |
| `generate menu-item` | `AxMenuItemDisplay` / `AxMenuItemAction` / `AxMenuItemOutput` |
| `generate report` | `AxReport` + DP class + optional DataContract class |
| `generate edt` | `AxEdt` |
| `generate enum` | `AxEnum` |
| `generate query` | `AxQuery` with root datasource and optional nested joins |
| `generate view` | `AxView` projecting a query: bound and computed fields |
| `generate map` | `AxMap` field template plus table mappings |
| `generate sysoperation` | Contract + Service + Controller class triple |
| `generate number-sequence` | Module extension + EDT + form handler class |
| `generate workflow` | `AxWorkflow` + document class + submit stub |
| `generate business-event` | Event class + contract class |
| `generate custom-service` | `AxService` + service class + `AxServiceGroup` |
| `generate runbase` | `RunBase` / `RunBaseBatch` skeleton with dialog parameters |
| `generate security-policy` | `AxSecurityPolicy` (XDS row-level security) |
| `generate systest` | `SysTestCase` skeleton — `[SysTestMethod]` Arrange/Act/Assert stub, optional `[SysTestCaseDataDependency]` and `--atl` `AtlDataRootNode` wiring (ATL-ready MVP, no test-logic generation) |
| `generate migration-script` | Data-fix `Runnable` class with `ttsbegin`/`ttscommit` batching |
| `generate simple-list` | Alias for `generate form --pattern SimpleList` |
| `modify method` | Replace an existing method's body on a live class/table/edt/form via D365FO.Bridge (`IMetadataProvider`, structured `XDocument` replace — no CDATA string surgery, no on-disk fallback). Reference/BP validation always blocks on error-severity findings. |
| `modify property` | Set a property (`Label`, `ConfigurationKey`, `TableGroup`, …) on a live object. |
| `modify add-field` | Add a field to a live table; the concrete `AxTableField*` subtype is resolved from the EDT's indexed base type. |
| `modify add-enum-value` | Add a value to a live base enum — positional, never a hard-coded ordinal. |
| `modify add-control` | Add a control to a live form's Design, optionally inside `--parent` and bound to `--datasource`/`--datafield`. |

#### Extension fallback

Every `modify` sub-command decides where the write lands. If the object's model is
**not** in `D365FO_CUSTOM_MODELS`, the change is redirected to the
`<Target>.<Suffix>` extension object in a custom model — created if it does not
exist — rather than editing a Microsoft or ISV object in place, and the redirect is
reported in `warnings`. `--extension [SUFFIX]` forces that path for any object;
`--extension-model` picks the owning model; `--require-extension` refuses the
in-place path entirely. The suffix defaults to whatever an existing extension of the
same object already uses, so a model does not accumulate `CustTable.Fleet` next to
`CustTable.Extension`.

Every `modify` write (including `modify method`) records its exact pre-image in the
modification journal — revert with `d365fo undo`.

### Scaffolding validation helpers

```sh
d365fo find form-patterns --similar-to CustGroup   # pick the right form pattern
d365fo suggest extension CustTable                  # rank extension strategies
d365fo validate name Table FmVehicle --prefix Fm    # naming-rule check
```

---

## Analysis

### `analyze completeness`

Cross-checks a model folder (or single XML file) against the index. Reports missing duties, privileges, EDTs, labels, and parse errors.

```sh
d365fo analyze completeness src/MyModel --output json
d365fo analyze completeness src/MyModel --skip-labels
```

### `analyze integration`

Runs integration-health checks against the index for a model: OData entities without staging tables, services without security entry points, business events without contracts, and batch jobs without `canGoBatch`.

```sh
d365fo analyze integration [--model M] --output json
```

### `analyze impact`

Change-impact graph for a named object: CoC wrappers, event handlers, object extensions, form datasources, data entities, and queries that reference it.

```sh
d365fo analyze impact CustTable --output json
```

### `report-integrations`

Aggregated integration surface for a model: OData entities, custom services, business events, workflow types, and batch jobs — all in one call.

```sh
d365fo report-integrations [--model M] --output json
```

---

## Labels

All label operations live under the unified `labels` branch (mirrors the MCP
`labels` tool). The older `search label` / `resolve label` / `label *` forms
still work as aliases.

```sh
# Read
d365fo labels search "Customer invoice"
d365fo labels search "..." --fts                        # FTS5 ranked search
d365fo labels info @SYS12345 --language en-us
d365fo labels resolve @SYS12345 --lang en-US,cs

# Write (atomic, preserves BOM + comments)
d365fo labels create Key "Value" --file path/Foo.en-us.label.txt
d365fo labels create Key "New"   --file path/Foo.en-us.label.txt --overwrite
d365fo labels rename OldKey NewKey --file path/Foo.en-us.label.txt
d365fo labels delete Key           --file path/Foo.en-us.label.txt
```

---

## Modification journal & undo

Every metadata write — `generate *`, `labels create\|rename\|delete`, and `delete` — appends
an entry to a size-capped, FIFO-pruned journal at `<index-dir>/journal/`, whether the write
went to disk or through the metadata bridge. `d365fo undo` replays entries in reverse through
the SAME write path that produced them, restoring the exact pre-image.

```sh
d365fo journal list --output json                  # inspect the stack, most-recent-first
d365fo undo --dry-run --output json                # preview what would be reverted
d365fo undo --output json                           # revert the last write
d365fo undo --steps 3 --output json                 # revert the last 3 writes

# Delete an AOT object (journaled, so it can be undone)
d365fo delete --kind table --name OldTable --path C:\pkg\MyModel\MyModel\AxTable\OldTable.xml
d365fo delete --kind class --name OldClass --install-to MyModel   # via the metadata bridge
```

- **create → undo** removes the file (and its `.rnrproj` entry, when the model has one with an explicit item list).
- **update → undo** restores the exact pre-image bytes.
- **delete → undo** recreates the file from its pre-image.
- The whole `modify` family journals too — including `modify method`, which previously
  read its pre-image and discarded it, leaving method edits un-undoable.
- Works identically with `D365FO_BRIDGE_ENABLED=1` or unset — bridge-mediated writes undo through `deleteObject`/`updateObject`/`createObject`.

MCP parity: `undo_last_modification` (`steps`, `dryRun`) and `journal_list` (`limit`).

---

## Knowledge & build-error triage

The verified X++/D365FO corpus in `skills/_source/*.md` is embedded in the binary and
served per topic or per section, so an agent with no skill-file support can still
ground itself. This is the CLI's equivalent of the upstream MCP `get_knowledge` tool,
and it reads the *same* documents the Copilot / Anthropic / `d365fo-cli` skill variants
are generated from — a fact is corrected in exactly one place.

```sh
d365fo knowledge list --output json                       # catalog + per-topic token cost
d365fo knowledge search "add a field to a standard table" # rank sections across the corpus
d365fo knowledge get table-scaffolding --outline          # cheap table of contents
d365fo knowledge get table-scaffolding --section "Pre-flight"
```

Prefer `search` → `get --section` over fetching a whole topic; a full topic runs to
~2.5k tokens.

`explain-error` scores compiler output against a rule table and returns the ranked
cause plus the knowledge topic behind it. It needs no VM and no index, so it works on
a log the user pasted:

```sh
d365fo build --output json | d365fo explain-error --output json
d365fo explain-error --file build.log --all --output json
d365fo explain-error "The label @SYS12345 does not exist."
```

Matching is scored, not first-match-wins: each rule declares the tokens it requires
and the tokens that disqualify it, every rule is evaluated, and the best-scoring one
wins. A message that matches nothing gets **no** hint rather than the nearest-looking
one. (The previous ordered `Contains` chain answered *"the label @SYS12345 does not
exist"* with generic identifier advice, and answered any message containing the word
"label" with label-creation advice.)

MCP parity: `get_knowledge` (`action=list|get|search`) and `explain_build_error`.

---

## Review

```sh
d365fo review diff --base HEAD
d365fo review diff --base main --head feature/my-branch
```

Rules: `FIELD_WITHOUT_EDT`, `FIELD_WITHOUT_LABEL`, `HARDCODED_STRING`, `DYNAMIC_QUERY`.

---

## Developer Experience

### Shell completion

```sh
d365fo completion bash       # bash tab-completion script
d365fo completion zsh        # zsh tab-completion script
d365fo completion powershell # PowerShell tab-completion script
```

Source the output in your shell profile to get `<Tab>` completion for all subcommands and flags.

### Daemon

Keeps the SQLite handle and read caches hot. Starts a `FileSystemWatcher` that auto-triggers incremental refresh on `*.xml` changes (debounce 3 s).

```sh
d365fo daemon start [--no-watch] [--watch-debounce 5000]
d365fo daemon status
d365fo daemon stop
```

---

## Windows-only (D365FO VM)

Wraps the Microsoft tools Visual Studio uses.

```powershell
d365fo build --project path/to/MyModel.rnrproj
d365fo sync --full
d365fo test run --suite MyModel.Tests
d365fo bp check --model MyModel
```

Returns `UNSUPPORTED_PLATFORM` on non-Windows.

---

## MCP server

Exposes the same index and scaffolding surface as the CLI over the `ModelContextProtocol` C# SDK via stdio (default) or HTTP (`--http`, for a shared team deployment). The tool surface is **consolidated** into **27 discriminator-based tools** (`search`, `get_object_info`, `get_method`, `labels`, `security_info`, `extension_info`, `object_patterns`, `generate_object`, `modify_method`, `modify_object`, `get_knowledge`, `explain_build_error`, `analyze`, `models`, …) instead of one tool per object type — mirroring the upstream `d365fo-mcp-server` (which sits at 26). A single tool dispatches on a `type` / `objectType` / `mode` / `action` / `domain` / `include` field. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md) for the full old→new mapping.

```jsonc
{
  "mcpServers": {
    "d365fo": {
      "command": "dotnet",
      "args": ["run", "--project", "/abs/path/to/src/D365FO.Mcp", "--no-build"],
      "env": { "D365FO_INDEX_DB": "/abs/path/d365fo-index.sqlite" }
    }
  }
}
```

### HTTP transport (shared team deployment)

```sh
d365fo-mcp --http --port 8080
```

| Env var | Purpose |
|---|---|
| `API_KEY` | Shared secret required in the `X-Api-Key` header on `POST /mcp`. Unset = unauthenticated (logs a startup warning); `GET /health` never requires it. |
| `MCP_SERVER_MODE` | `full` (default) \| `read-only` \| `write-only` — gates the tool surface. `read-only` drops the tools that need the local package tree — `generate_object`, `labels`, `get_workspace_info`, `get_method`, the bridge-backed `modify_method`/`modify_object`/`undo_last_modification`, and `journal_list`; `write-only` exposes only those. No tool that writes is reachable in `read-only`. A disallowed `tools/call` fails with `MODE_NOT_ALLOWED`. |
| `MCP_HTTP_PORT` | Listen port when `--port` is omitted (default `3000`). |

`POST /mcp` is one JSON-RPC request per call (no SSE/session state) — reuses the same dispatch, routing, and mode gate as stdio. `GET /health` reports `{status, mode, indexReachable}` and needs no auth. A simple in-memory rate limiter (429 + `RATE_LIMITED`) protects `/mcp` per API key / IP. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md#http-transport--shared-deployment-azure-app-service) for the read-only-shared / write-only-local deployment pattern this mirrors from upstream.

---

## Copilot Skills

The `d365fo-cli` agent skill (`skills/d365fo-cli/`) covers the full X++ authoring and review canon: `SKILL.md` holds the rule canon and tool mapping, and 19 topic files in `references/` are loaded on demand. Deploy to an X++ project with the `Install-D365FoCopilotSkills.ps1` script (see [SETUP.md](SETUP.md)), which installs it to `.github/skills/d365fo-cli/`.

The same 19 topics are also emitted as `skills/copilot/*.instructions.md` (legacy `applyTo` format) and `skills/anthropic/<id>/SKILL.md` (Claude Code / Claude Desktop).

| Skill | Covers |
|-------|--------|
| `coc-extension-authoring` | Chain-of-Command patterns, wrapping rules, extension naming |
| `data-entity-scaffolding` | OData entities, staging tables, field mapping |
| `event-handler-authoring` | Pre/post event subscribers, delegate pattern |
| `form-pattern-scaffolding` | 9 form patterns, datasource setup, controls |
| `label-translation` | Label file format, BOM, multi-language workflow |
| `model-dependency-and-coupling` | Layering, reference scanning, circular deps |
| `object-extension-authoring` | Table/form/EDT/enum extension conventions |
| `review-and-checkpoint-workflow` | PR review rules, BP check integration |
| `security-hierarchy-trace` | Role → duty → privilege → entry-point chain |
| `table-scaffolding` | TableGroup, indexes, relation, delete actions |
| `x++-class-authoring` | Class hierarchy, CoC, access modifiers |
| `xpp-best-practice-rules` | CAR rule set cross-reference |
| `xpp-class-and-method-rules` | Method-level BP rules |
| `xpp-database-queries` | `select`/`while select`, `forUpdate`, TTS scope |
| `xpp-statement-and-type-rules` | Type system, container, enum usage |
| `business-events-authoring` | `BusinessEventsBase`, contract class, payload |
| `integration-patterns` | OData, custom services, Dual-write surface |
| `custom-service-authoring` | JSON/SOAP services |

---

## When to use built-in editor tools vs. `d365fo`

> Quick reference for developers and AI agents working in VS 2022 / VS Code.

| Scenario | Built-in editor / terminal tools | `d365fo` CLI |
|---|---|---|
| Read class structure (methods, signatures) | ❌ `get_file` on XML — unreliable schema | ✅ `d365fo get class <Name> --output json` |
| Read a method body (X++ source) | ❌ | ✅ `d365fo read class <Name> --method <M>` |
| Inspect table fields / indexes / relations | ❌ `get_file` on AxTable XML — unreliable | ✅ `d365fo get table <Name> --output json` |
| Inspect several objects at once | ❌ | ✅ `d365fo get batch table:CustTable class:CustTableType --output json` |
| Search for a class / table / method | ❌ `code_search` / `file_search` — can't parse AOT XML schema, returns misleading snippets | ✅ `d365fo search class <query> --output json` |
| Check for existing CoC wrappers | ❌ | ✅ `d365fo find coc <Class>::<method> --output json` |
| Form pattern structure / requirements | ❌ | ✅ `d365fo form-pattern spec <Pattern> --output json` |
| Validate a form against its pattern | ❌ | ✅ `d365fo form-pattern validate <file> --output json` |
| Create a new AOT object (class, table, form…) | ❌ `create_file` — wrong location, wrong XML schema | ✅ `d365fo generate class/table/form … --install-to <Model>` |
| Modify existing AOT XML — targeted method body edit (inside CDATA) | ⚠️ `replace_string_in_file` / `multi_replace_string_in_file` — allowed for method bodies only; run `d365fo index refresh` after | ✅ `d365fo generate … --overwrite` for full-file replace |
| Modify existing AOT XML — structural change (add field, index, relation…) | ❌ `replace_string_in_file` — corrupts XML structure | ✅ `d365fo generate extension … --overwrite` or VS AOT |
| Search for a label | ❌ | ✅ `d365fo labels search "<text>" --output json` |
| Resolve a label key | ❌ | ✅ `d365fo labels resolve @SYS12345 --lang en-us,cs` |
| Trace security (Role → Duty → Privilege) | ❌ | ✅ `d365fo security coverage <Role> --type Role --output json` |
| Run best-practice check | ❌ | ✅ `d365fo validate xpp <file>` (offline) or `d365fo bp check` (Windows VM, on user request) |
| Inspect model dependencies | ❌ | ✅ `d365fo models deps <Name> --output json` |
| Build / compile — check errors across workspace | ⚠️ `run_build` — on explicit user request only | ✅ `d365fo build` — **on explicit user request only** |
| Get compilation errors for a specific file (fast) | ✅ `get_errors` — per-file, no full build needed | ➖ not available |
| Navigate workspace structure (projects, file lists) | ✅ `get_projects_in_solution`, `get_files_in_project` | ➖ not needed |
| Read / edit non-AOT files (PS scripts, docs, JSON config) | ✅ `get_file`, `replace_string_in_file`, `multi_replace_string_in_file` | ➖ not needed |
| Git operations (commit, diff, branch) | ✅ `run_command_in_terminal` — `git …` | ➖ not needed |
| Refresh index after editing XML | ❌ | ✅ `d365fo index refresh --model <Model>` |
| Verify index health | ❌ | ✅ `d365fo doctor --output json` + `d365fo index status --output json` |

**One-line rule:** if the file ends in `.xml` and is an AOT object → always `d365fo`. Everything else (config, scripts, docs) → standard editor tools.

> ⛔ **When `d365fo` returns `ok: false`** — report the error to the user and stop. Metadata read from open XML files does **not** substitute for the CLI. Never fall back to PowerShell / Python scripts to write AOT XML: spawned processes hang forever in VS 2022 (no interactive terminal).

---

## Relevant source files

| Path | Purpose |
|------|---------|
| `src/D365FO.Core/Index/Schema.sql` | SQLite schema definition |
| `src/D365FO.Core/Index/MetadataRepository.cs` | All query and lint methods |
| `src/D365FO.Core/Extract/MetadataExtractor.cs` | AOT walkers + extraction-time flags |
| `src/D365FO.Core/Index/Models.cs` | DTOs for query results |
| `src/D365FO.Core/Scaffolding/` | Scaffolder classes |
| `src/D365FO.Cli/Program.cs` | Command registration |
| `src/D365FO.Cli/Commands/` | All CLI command implementations |
| `src/D365FO.Mcp/ToolCatalog.cs` | MCP tool descriptors |
| `src/D365FO.Mcp/ToolHandlers.cs` | MCP handler methods |
| `skills/_source/` | Skill source files (emitted to `skills/d365fo-cli/references/`, `skills/copilot/`, `skills/anthropic/`) |

---

## See also

- [SETUP.md](SETUP.md) — install, configure, connect an AI agent.
- [EXAMPLES.md](EXAMPLES.md) — one worked example per command.
- [ARCHITECTURE.md](ARCHITECTURE.md) — internals behind every feature above.
