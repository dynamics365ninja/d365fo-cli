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
| `index cross-check` | Report where this tool's catalogs are narrower than the installation |
| `index optimize` | Checkpoint the WAL, then `VACUUM` + `ANALYZE` to compact and re-plan |
| `doctor` | End-to-end health check: paths, schema version, object counts |

`index sync <TARGET>` re-reads a single model — pass the model name, or a path to any file
inside it and the model is read off the packages layout. It is the repair for an edit this tool
did not make (Visual Studio, a git pull, a colleague); writes through `generate` / `modify`
already refresh the index themselves. A model is the unit because the writer replaces a model's
rows atomically: a single-object write would delete the rest of that model.

### `index cross-check`

Every catalog this tool answers from — the form-pattern registry, the object-type registry, the
DataContract catalog — is generated from one platform version and then committed. That makes it
right when it was made and silent about drift afterwards. This asks the installation instead.

```sh
d365fo index cross-check                       # gaps only
d365fo index cross-check --show-uncovered      # plus families the tool does not cover
```

Two findings, deliberately kept apart:

- **Gaps** are where the tool will be *wrong* — something the installation uses that a catalog
  claims to cover and does not. A form pattern in the wild that the registry has never heard of
  means `generate form`, `form-pattern validate` and `form-pattern repair` cannot judge those
  forms, and will say so in the confident voice of a tool that has a catalog. Exit code 2.
- **Uncovered** families are where the tool is merely *narrow* — an AOT folder it was never built
  to handle. On a real installation that is dozens of entries (40 of 83 on the box this was
  written against), so it is off by default and never fails the command.

The fix for a gap is to regenerate the named catalog on the installation that produced it
(`scripts/emit-form-patterns.ps1`, `scripts/emit-metadata-contracts.ps1`), not to hand-add an entry.

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
| `generate entity` | `AxDataEntityView` — fields, keys, `--relation` (constraint-joined), `--computed-field` (unmapped, method-backed). `--data-management` also emits the DMF staging table |
| `generate extension` | `AxTableExtension` / `AxFormExtension` / `AxEdtExtension` / `AxEnumExtension` / `AxViewExtension` / `AxQuerySimpleExtension` / `AxDataEntityViewExtension` / `AxMapExtension` / `AxSecurityDutyExtension` / `AxSecurityRoleExtension` |
| `generate event-handler` | X++ event subscriber class with correct attribute |
| `generate privilege` | `AxSecurityPrivilege` (entry point and/or `--data-entity` OData/DMF grant) |
| `generate duty` | `AxSecurityDuty` — privileges plus `--description`, `--context-string`, `--disabled` |
| `generate role` | `AxSecurityRole` (new or merge into existing) — duties, privileges, `--sub-role` composition, `--context-string`, `--disabled`, `--no-delete-from-ui` |
| `generate menu-item` | `AxMenuItemDisplay` / `AxMenuItemAction` / `AxMenuItemOutput` |
| `generate report` | `AxReport` + DP class + optional DataContract class |
| `generate edt` | `AxEdt` |
| `generate enum` | `AxEnum` |
| `generate query` | `AxQuery` with root datasource and optional nested joins |
| `generate view` | `AxView` projecting a query: bound and computed fields |
| `generate map` | `AxMap` field template plus table mappings |
| `generate configuration-key` | `AxConfigurationKey` — label, parent key, license code, `--disabled-by-default` |
| `generate form-part` | `AxFormPart` registering a form as a hostable part (info part, fact box, preview pane) |
| `generate label-file` | `AxLabelFile` manifest for one language plus the `.label.txt` it points at (`--entry Key=Text`) |
| `generate menu` | `AxMenu` — sub-menus, menu items (`[<submenu>/]<item>[:Action|Output]`), tiles, menu references |
| `generate resource` | `AxResource` manifest; `--source` copies the file into `ResourceContent/<Type>/` |
| `generate tile` | `AxTile` bound to a menu item — Standard / Count (`--query`) / KPI (`--kpi`) / Link |
| `generate workflow-category` | `AxWorkflowCategory` under a `ModuleAxapta` module (validated against the enum) |
| `generate composite-entity` | `AxCompositeDataEntityView` — root entities with embedded entities bound by relation |
| `generate aggregate-entity` | `AxAggregateDataEntity` — read-only projection of an aggregate measurement's measures and dimension attributes |
| `generate sysoperation` | Contract + Service + Controller class triple |
| `generate number-sequence` | Module extension + EDT + form handler class |
| `generate workflow` | `AxWorkflowTemplate` + document class + submit stub, plus `AxWorkflowApproval` / `AxWorkflowTask` elements when named |
| `generate business-event` | Event class + contract class |
| `generate custom-service` | `AxService` + service class + `AxServiceGroup` |
| `generate runbase` | `RunBase` / `RunBaseBatch` skeleton with dialog parameters |
| `generate security-policy` | `AxSecurityPolicy` (XDS row-level security), including the nested `--constrained` table tree |
| `generate systest` | `SysTestCase` skeleton — `[SysTestMethod]` Arrange/Act/Assert stub, optional `[SysTestCaseDataDependency]` and `--atl` `AtlDataRootNode` wiring (ATL-ready MVP, no test-logic generation) |
| `generate migration-script` | Data-fix `Runnable` class with `ttsbegin`/`ttscommit` batching |
| `generate form-clone` | Copy of an existing `AxForm` under a new name, datasources optionally re-bound |
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

#### Cloning a reference form

A Microsoft form that already has the pattern, the control tree and the wiring right is a better
starting point than any template, and cloning one is what a developer does by hand anyway.

```sh
d365fo generate form-clone ConVehicleGroup --from CustGroup     --rebind CustGroup=ConVehicleGroupTable --out ConVehicleGroup.xml
```

`--from` takes a form name (resolved through the index) or a path to the AxForm XML. `--rebind`
moves a datasource onto another table, renames the datasource when it was named after the old
table, and follows that rename into every control that referenced it — including the datasource
entry under `<SourceCode>`, where override methods live.

The edits are string-level and narrow. An `AxForm` is a V6 contract whose Design subtree is
written in the empty namespace with `i:type` on every control, so loading it into an `XDocument`
and writing it back rewrites namespace declarations nobody asked to change — which is also why
`FormPatternTemplates` renders forms as strings. Verified against a shipped 16 KB `CustGroup`
form: the clone differs on exactly the intended lines and is byte-identical everywhere else.

What it deliberately does *not* do is a blind replace of the old form name. Form names are short
and appear inside unrelated identifiers (`CustGroup` inside `Grid_CustGroupId`), so only the root
`<Name>`, the class declaration and `formStr()` self-references move. Everything it cannot reach —
menu items, privileges, extensions, callers elsewhere in the AOT — comes back as a warning, as
does the fact that a rebind does not check the new table actually has the bound fields.

#### The grounding gate

Every `generate` subcommand runs the same gate before it writes anything — not just the
extension-shaped ones. The gate does three things:

- **Token.** `--grounding-token` from `d365fo prepare change` / `prepare create` proves the
  index was consulted first. Tokens are object-bound and expire after 30 minutes. Under
  `D365FO_GROUNDING_ENFORCE=true` a missing or mismatched token fails the write; otherwise it
  is a warning.
- **Self-check.** Every identifier in the generated X++ must resolve in the index, and the
  code must be free of error-severity BP findings. Each command also names the AOT objects it
  is claiming exist — a form's datasource, an EDT's `Extends`, a workflow's driving table, a
  CoC target's methods. Under enforcement, unresolved references fail the write.
- **Property honesty.** After the write, everything the caller asked for is looked for in the
  document that actually reached disk. Anything missing comes back as a `property-honesty`
  warning naming the option and the value:

  ```
  property-honesty: --primary-key NotAField — "NotAField" is not in the generated object.
  The scaffolder either does not carry that option onto this AOT type, or the value was
  dropped on the way to disk.
  ```

  This is the only check that can see an option being accepted and quietly discarded; every
  other validator judges the document on its own terms, and a document missing a property
  nobody asked it for is perfectly valid.

The gate is not something a subcommand can opt out of: `GenerateInstaller.Write` takes the
gate's result as an argument, so there is no way to reach the writer without having gated, and
`GenerateGateSurfaceTests` fails the build if a command reaches around the shared path.

### Scaffolding validation helpers

```sh
d365fo find form-patterns --similar-to CustGroup   # pick the right form pattern
d365fo suggest extension CustTable                  # rank extension strategies
d365fo validate name Table FmVehicle --prefix Fm    # naming-rule check
```

`validate xpp` carries the offline half of the same question as rule **XML007**: a member the
AOT type does not declare is silently discarded on read, so `<Datasets>` (it is `DataSets`) or
`<Image>` on a menu item leaves a file that looks deliberate and is missing data. The member
catalog is generated from the metadata assembly by `scripts/emit-metadata-contracts.ps1` and
committed, so this works with no D365FO install. Generated files are also written in contract
order, which is what keeps a table's field groups from being dropped.

#### The XML rule canon, and which families each rule speaks for

| Rule | Finds | Families |
|------|-------|----------|
| `XML001` | Table with no `<AlternateKey>Yes</AlternateKey>` index | `AxTable` only |
| `XML002` | Table missing `<Label>` (mined) | `AxTable` only |
| `XML003` | Table missing `<TableGroup>` (mined) | `AxTable` only |
| `XML004` | Field with neither `<ExtendedDataType>` nor `<EnumType>` (mined) | `AxTable` only |
| `XML005` | Table missing `<ClusteredIndex>` (mined) | `AxTable` only |
| `XML007` | Member the type does not declare — silently dropped on read | every family |
| `XML008` | Value outside the enum its member is typed as — the read throws | every family |
| `XML009` | Root element names no AOT type, or one no shipped AOS has | every family |
| `XML010` | Abstract root with no concrete `i:type`, or one that resolves to nothing | every family |
| `XML011` | `xmlns:i` missing where the reader needs it | every family |
| `XML012` | Document not in the XML namespace its contract declares | every family |
| `XML013` | File sitting in an AOT folder another family owns | every family (path-aware) |

XML001–XML005 are AxTable-only by nature: they are property-presence rules mined from standard
tables, and there is no equivalent evidence for other families. Everything from XML007 down is
driven by the `MetadataContracts` catalog (565 types, generated from
`Microsoft.Dynamics.AX.Metadata.dll`) and by `ObjectTypeRegistry`, so it applies uniformly —
form, EDT, enum, entity, report, query, view, map and every security type included. XML009–XML012
are the offline approximation of what the bridge's `Handlers.WriteArtifact` rejects before the
provider ever sees the document (`TYPE_NOT_FOUND`, `ABSTRACT_TYPE`, and the two
`XML_DESERIALIZE_FAILED` shapes), which is what lets non-Windows CI catch those failures.

There is deliberately **no member-order lint**. Order matters and generated files are written in
contract order, but shipped Microsoft files deviate from it in places and the provider reads them
back with no loss — so flagging deviation in other people's files would assert a defect the
evidence does not support. The ordering knowledge is applied where it is proven (canonical
output), not asserted as a rule.

### `validate metadata` — the provider's own verdict

Every other validator checks the XML against *our* expectations. This one hands the file to
Microsoft's metadata serializer and reports what it cannot keep:

```sh
d365fo validate metadata MyTable.xml                       # one file
d365fo validate metadata src/MyModel --recursive           # a whole model
```

Two ways to be invalid, and the second is the dangerous one:

- **Cannot be read at all** — wrong contract namespace, an abstract root without `i:type`, an
  enum value the platform does not define. Visual Studio refuses to open the file.
- **Reads, but loses data** — `DataContractSerializer` ignores elements a type does not declare
  and stops matching once elements fall out of contract order, so a misspelled, invented, or
  misplaced property is *silently dropped*. The file looks right, offline validators pass, and
  the object is quietly missing the property.

Nothing is written and no model is touched. Requires `D365FO_BRIDGE_ENABLED=1` and
`D365FO_BIN_PATH`; off a D365FO machine it reports itself skipped rather than guessing.

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

### `analyze patterns` · `analyze implementations` · `analyze api-usage`

Three readings of the X++ corpus, for grounding a change in what this installation actually does:

| Command | Answers |
|---|---|
| `analyze patterns <SCENARIO>` | Which APIs the code around a scenario reaches for, ranked by how many distinct objects use each. Reads whole method bodies, not just the matching line. |
| `analyze implementations <METHOD>` | Who else declares this method, with the signature each declared — before writing an override or a CoC wrapper. |
| `analyze api-usage <API>` | Construction vs. static calls vs. declarations, with the lines that do it. Says so when an API is never constructed. |

Each result carries a `coverage` block. A search over method bodies returns nothing when nothing
matches — and also when the corpus could not be read at all, which is a different answer.
`coverage.searched: false` means the second, and the result says so rather than letting an empty
list read as "unused".

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

### `verify` — does the model on disk match its project?

```sh
d365fo verify ConFleet --output json
d365fo verify --path /repo/PackagesLocalDirectory/ConFleet/ConFleet --expect ConFleetVehicle
```

Two findings, both invisible otherwise:

| Finding | What it means |
|---|---|
| `UNREGISTERED` | The XML is in the model folder and the `.rnrproj` does not list it, so it is **not compiled** — the AOT never sees the object and nothing anywhere reports that. |
| `MISSING_FILE` | The project references a file that is gone; the build fails on a path rather than a reason. |

`--expect <NAME>` answers by name (`ConFleetVehicle` or `AxTable/ConFleetVehicle`) for the objects
you believe you just created. A project with **no explicit item list** is not judged: some project
shapes glob their content, and calling every file unregistered there would be a wall of findings
that are all wrong — the result says so instead.

MCP parity: `verify_project` (`model` / `path`, `expect[]`).

### `labels update` — correct a label that already exists

```sh
# One language: the corrected text is positional
d365fo labels update ConVehicle "Vehicle" --install-to ConFleet --lang en-us

# Several languages: one --text per language, because a translation is not the same string twice
d365fo labels update ConVehicle --install-to ConFleet --lang en-us,cs \
  --text en-us=Vehicle --text cs=Vozidlo
```

Deliberately **not** `labels create --overwrite`: create writes a *new* entry when the key is
absent, so a mistyped key in a correction produces a second label and reports success. `update`
refuses a key that is not there and leaves the file untouched.

Targeting several languages with a single text is refused rather than applied — writing one
language's string into every file silently replaces the other translations. The refusal names the
`--text` arguments to pass instead.

MCP parity: `labels (action=update)`.

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
d365fo knowledge audit --output json                      # prove the corpus itself
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

## Oracles (`oracle`) — measuring the tool against the platform

A green test suite proves the code does what its author expected. These five commands ask a
different question: does what this tool believes match what the installation actually contains,
compiles and runs? They are maintainer tools — a measurement of `d365fo-cli`, not an operation
on your X++ — and none of them is published over MCP.

```powershell
d365fo oracle sweep                       # every rule over every AOT file in the installation
d365fo oracle sweep --model ApplicationFoundation --warnings
d365fo oracle sweep --dry                 # the checked-in fixtures, for CI with no installation
d365fo oracle census AxTable              # what shipped XML actually carries, member by member
d365fo oracle members AxTable             # declared-but-never-seen and seen-but-not-declared
d365fo oracle probe MyClass.xml           # compile one artefact with the real xppc.exe
d365fo oracle runtime                     # is SysTestConsole wired to a database, and does it discriminate?
d365fo oracle runtime --negative-control  # a test class that passes, fails and throws on purpose
```

**The bar for `sweep` is zero errors on Microsoft's own X++.** Not "few": a rule that fires on
shipped code teaches a caller to ignore findings, which costs more than the rule ever earned.
Warnings are counted separately and do not fail the bar — several are style rules that shipped
code legitimately breaks. The first full run (242 858 files, 107 241 X++ blocks) reported 4 674
errors, every one of them the validator being wrong; `SweepFalsePositiveTests` pins each with the
count it accounted for. The bar holds as of that work: the same sweep now reports **zero errors**
in 48 minutes.

`probe` compiles into a throwaway model built around the artefact, so a clean result means the
compiler ran and said nothing — an xppc that rejects its own argument list prints usage, which
parses as "no diagnostics", and that case is reported as a failure of the probe rather than a
pass. `runtime` answers the question nobody asks about a green suite: "all tests passed" is also
what a runner prints when it ran nothing, so the negative control has to fail before any pass
means anything.

---

## MCP server

Exposes the same index and scaffolding surface as the CLI over the `ModelContextProtocol` C# SDK via stdio (default) or HTTP (`--http`, for a shared team deployment). The tool surface is **consolidated** into **32 discriminator-based tools** (`search`, `get_object_info`, `get_method`, `labels`, `security_info`, `extension_info`, `object_patterns`, `generate_object`, `modify_method`, `modify_object`, `get_knowledge`, `explain_build_error`, `analyze`, `models`, …) instead of one tool per object type — mirroring the upstream `d365fo-mcp-server` (which sits at 26). A single tool dispatches on a `type` / `objectType` / `mode` / `action` / `domain` / `include` field. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md) for the full old→new mapping.

### Keeping the two surfaces the same

"The same surface as the CLI" is a claim, and for a while it was not true in either direction:
`modify_object` dispatched four of the engine's twenty operations (and silently treated every
other `action` as `property`), `object_patterns` served only the form domain, the BP-moniker
catalog was unreachable, and the merged table schema was reachable *only* over MCP. Meanwhile
the JSON manifest `d365fo schema` publishes — the map an agent reads to translate between the
two — listed 130 commands out of 200 registered and named routes that did not exist.

`CliMcpParityTests` now holds the claim up:

| Check | What fails the build |
|---|---|
| Published surface | A registered command that is neither in the manifest nor in the test's declared out-of-scope list (with a reason). |
| No phantoms | A manifest entry naming a command the app does not register. |
| Route truth | A manifest claim like `analyze (mode=completeness)` whose tool, or whose discriminator value, the catalog does not dispatch. |
| No orphan tools | An MCP tool no CLI command reaches, unless declared MCP-only with a reason. |
| Write surface | Any `ObjectModifyEngine.Operation` not reachable as both `d365fo modify <op>` and `modify_object(action=<op>)`. |
| Scaffold surface | A `d365fo generate <x>` sub-command with no `generate_object` objectType behind it. |
| Stated intent | A published command with no MCP route and no reason recorded for it. |

One consequence is worth stating on its own: **`D365FO_GROUNDING_ENFORCE=true` now means the
same thing on both surfaces.** The gate — index-proved identifiers, the offline BP validator, and
an object-bound token from `prepare` — used to live beside the CLI's generate commands, so it ran
on every CLI write and on no MCP write. `generate_object` takes a `groundingToken`, reports what
the gate saw in `grounding`, and refuses an ungated write under enforcement.

The full `index extract` / `index refresh` stay shell-only — they walk every package for
minutes, which is not the shape of a call anyone waits on. What an agent actually needs after an
edit made elsewhere is the narrow form, and that is exposed: `index sync` / `index_sync`
re-reads ONE model, named directly or by a path to a file inside it.

The shared implementations behind that live in Core — `CompletenessAnalyzer`, `FormPatternMiner`,
`ExtensionAnswers`, `BpMonikerAnswers`, `PatternCatalogAnswers`, `BatchStepParser` — so the two
surfaces return the same answer rather than two answers that agree by inspection.

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

The `d365fo-cli` agent skill (`skills/d365fo-cli/`) covers the full X++ authoring and review canon: `SKILL.md` holds the rule canon and tool mapping, and 38 topic files in `references/` are loaded on demand. Deploy to an X++ project with the `Install-D365FoCopilotSkills.ps1` script (see [SETUP.md](SETUP.md)), which installs it to `.github/skills/d365fo-cli/`.

The same 38 topics are also emitted as `skills/copilot/*.instructions.md` (legacy `applyTo` format) and `skills/anthropic/<id>/SKILL.md` (Claude Code / Claude Desktop).

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
| `sysoperation-batch-patterns` | SysOperation triplet, RunBase, migration scripts, retryable/async batch |
| `analytics-and-er` | Tiles/cues, KPIs, aggregate measurements, Electronic Reporting |
| `build-error-triage` | Compiler/BP/runtime message → the specific fix, via `explain-error` |
| `forms-and-navigation` | Form lifecycle extension points, menus and submenu nesting |
| `integration-dmf-dualwrite` | DMF, dual-write, virtual entities, uploaded-file readers |
| `inventory-and-warehouse` | InventTrans/InventDim/InventSum, on-hand, WHS work and waves |
| `number-sequence-patterns` | `NumberSeqApplicationModule`, form handler, runtime fetch |
| `performance-and-caching` | Set-based work, `CacheLookup`, explicit caches, parallel batch |
| `posting-and-financials` | Financial dimensions, voucher posting, currency, pricing |
| `runtime-frameworks` | Feature Management, SysExtension, telemetry, Global Address Book |
| `security-modeling` | Privilege/duty/role chain, XDS policies, configuration keys |
| `ssrs-report-authoring` | TmpTable → Contract → DP → Controller → AxReport, Print management |
| `testing-and-quality` | SysTestCase, ATL, and the offline validate/lint gates |
| `transactions-and-concurrency` | tts scoping, OCC retry, `UnitOfWork`, error handling |
| `workflow-authoring` | `AxWorkflowTemplate`, document class, approvals and tasks |
| `xpp-data-access-apis` | `Query`/`QueryRun`, SysDa, direct SQL |
| `xpp-runtime-types` | Collections, date/time zones, .NET interop, `Dict*`, macros |

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
