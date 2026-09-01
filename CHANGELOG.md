# Changelog

All notable changes to `d365fo-cli` are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Versioning

This repository has no release tags yet, and the assembly carries no explicit version, so
entries below are grouped by the date the work landed on `main` rather than by a version
number that does not exist. `d365fo version` reports the assembly version. Once the first
tag is cut, this file switches to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
and the dated sections become the pre-1.0 history.

Upstream references of the form "upstream 1.15.0" point at the sibling
[`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) release the work
was ported from.

---

## [Unreleased]

### Added — the structural half of `modify`
- **Seventeen new `modify` sub-commands**, taking the write surface from 5 operations to 22:
  `add-index`, `add-relation`, `add-field-group`, `add-delete-action`, `rename-field`,
  and the removals `remove-field`, `remove-index`, `remove-relation`, `remove-field-group`,
  `remove-delete-action`, `remove-enum-value`, `remove-control`. Before these, an agent that
  wanted to add an index to an existing table had no path through the CLI at all and fell back
  to editing AOT XML by hand — which walks straight past the grounding, form-pattern and
  reference gates the tool exists to enforce. Each goes through `ObjectModifyEngine`, so each
  inherits the extension fallback (never overwrite a model this installation does not own),
  the journalled pre-image (`d365fo undo` reverts it), and contract-order canonicalisation.
- Guards that cost nothing and save a build cycle: `add-index` refuses a field the table does
  not declare (kernel fields excepted), is unique unless `--allow-duplicates`, and rejects
  `--allow-duplicates` alongside `--alternate-key`; `add-delete-action` accepts only the four
  actions the platform defines; `add-relation` pins the concrete
  `AxTableRelationConstraintField` discriminator, without which the metadata reader throws on
  the abstract base; `rename-field` rewrites the indexes, field groups and relation constraints
  that name the field, and says plainly that X++ `fieldStr()`/`fieldNum()` references are not
  rewritten.
- `D365FoErrorCodes.MemberNotFound` for the collection members the removals address.
- **`modify` reaches every kind the bridge can write, not five.** `SupportedKinds` was a
  hand-written set of `class, table, edt, enum, form` while the bridge resolves its collections
  from `ObjectTypeRegistry.BridgeCollections()`, which names **41** — so `modify property`
  refused a query, a privilege, a menu item, a service group, a view, a report and a tile the
  layer underneath was perfectly able to write, and the refusal named the five as if they were
  the platform's limit. Same drift as the extension-type lookup in #171, same fix: derive it.
  An unknown kind now names the near misses rather than printing all 41.
- **`modify add-query-range` / `remove-query-range`** for AOT queries. The datasource is optional
  when the query has exactly one; with two or more the command refuses and lists them, because a
  range on the wrong datasource returns the wrong rows silently instead of failing.
- **`modify add-entry-point` / `remove-entry-point`** for security privileges, writing the six
  independent permissions the security model actually has — there is no `AccessLevel` member, and
  writing one produces a privilege that grants nothing while reading as deliberate.
- **`modify batch`** — several changes to one object in a single read-edit-write, from
  `--operations` JSON or `--operations-file`. Adding a field, an index over it and a field group
  showing it was three bridge round trips, three journal entries, and two intermediate states
  published to disk (one of them a table carrying a field no index covers). Batched, the object
  moves from one valid state to the next in one write, and a refused step discards the batch with
  **nothing written** — the answer says so, because "step 3 of 5 failed" otherwise reads like a
  half-applied change.
- **`get table --merged`** and `extension_info(mode="table-merge")` now return the effective
  schema: base fields, indexes, relations and field groups with every TableExtension folded in,
  each member labelled with the object contributing it. An extension whose file cannot be read is
  **reported**, not skipped — a merge missing a contributor answers "that field does not exist"
  with the same confidence as a complete one. Verified against the shipped `VendInvoiceInfoTable`:
  126 fields, 11 of them from 4 extensions.

### Added
- **`prepare test` resolves a TABLE, not only a class.** A table method is named the way a
  developer says it — `d365fo prepare test CustTable.validateWrite` — and the dotted form is
  split before resolution. The symbol index stores *declared* members only, so a table that has
  never overridden `validateWrite` has no row for it, which is exactly the method the caller
  came for; the inherited kernel data methods are now listed from `TableDataMethods` beside the
  table's own overrides, each labelled with where it comes from. The answer carries the scaffold
  call already spelled `--table`, and the shape a table test needs that a class test does not:
  arrange a buffer with `initValue()` rather than selecting a row, assert the verdict **and** the
  infolog line the rule writes, and keep the accepting case beside the rejecting one — without it
  a rule that refuses every row passes its own test. Write-path methods (`insert`/`update`/
  `delete`) get a transaction and an assertion against a re-read instead of the buffer.
- `TableDataMethods.All`, `.IsWritePath()` and `.IsVerdictMethod()` — the kernel data-method
  catalog is now enumerable, not only addressable by name.

### Fixed — found by running against the live installation
These five were invisible to a green suite: every one needed a real `IMetadataProvider`, and
three of them were wrong about what Microsoft's own AOT actually writes.

- **The bridge could not read a table at all.** `readObjectXml` serialised with
  `XmlSerializer`, which reflects a type eagerly and refuses anything implementing
  `IEnumerable` without `Add(object)` — Microsoft's `AccessGrant` does exactly that, so every
  artifact transitively holding one (`AxTable`, `AxSecurityPrivilege`, `AxMenuItem*`) failed
  with *"There was an error reflecting type 'AxTable'"*. The write path had the identical
  fault. `modify` therefore never worked against a real installation for the kinds people use
  most. Both now use `DataContractSerializer`, which is what the `validateArtifact` path
  already used and already explained: the MetaModel types are DataContract-annotated and that
  contract is what the on-disk format encodes.
- The read path reported only the outer exception, so *"error reflecting type 'AxTable'"* was
  undiagnosable. It chains the inner exceptions now, as the validate path already did.
- **Every `modify remove-*` command failed on invocation.** `ModifyRemoveSettings` was
  `abstract`, and Spectre.Console.Cli constructs a command's settings by reflection — so all
  seven compiled, registered, and appeared in `--help`, then died with *"Could not resolve type
  'ModifyRemoveSettings'"*. Engine tests could not see it because they call the engine, not the
  command. `CommandSurfaceTests` now walks all 175 registered settings types.
- **A delete action was written naming nothing.** `AxTableDeleteAction` declares
  `Name, DeleteAction, Relation, Table, Tags` — the related table is `<Table>`. The tool wrote
  `<RelatedTable>`, which the contract does not have, so the serializer dropped it and a
  `Cascade` rule with no target reached disk while the write reported `ok`. The extractor read
  the same wrong element, which is worse: measured on this repo's own host, the index held
  **0 delete actions across 214 packages** and `get table` answered `deleteActions: []` for
  every table in the installation.
- **The index held 4,896 queries and zero datasources.** Shipped queries use
  `AxQuerySimpleRootDataSource` and `AxQuerySimpleEmbeddedDataSource`; the extractor matched
  `AxQuerySimpleDataSource` / `AxQueryDataSource`, which occur in no shipped query at all. Only
  the sample fixtures use the shorter name, which is exactly why the suite stayed green. Matching
  now keys on the `DataSource` suffix, which also keeps `…DataSourceField`, `…Range` and
  `…Relation` out — a prefix match would have swept those in.

### Fixed
- **`modify` never canonicalised member order before writing back.** The disk write path has
  always done it (`ScaffoldFileWriter.Finalize`) but the bridge path serialised the edited
  document as-is. `DataContractSerializer` — which is what reads AOT XML — matches children in
  contract order and *skips* anything arriving out of turn: it is not rejected, it is dropped.
  So an edit that had to create a missing collection (`<Fields>` on a table that had none,
  `<EnumValues>`, `<Controls>`) appended it after a later-ordered sibling, the write came back
  `ok`, and the change was silently absent. `modify property` had the mirror problem, inserting
  every property directly after `<Name>` — far too early for most of the AxTable contract. Both
  are covered by tests that assert the written element order.
- **A doc comment was reported as a method's signature.** `ExtractSignatureLine` skipped blank
  lines and attribute blocks but not comments, so any method opening with `/// <summary>` — or a
  `//` note, or a `/* … */` banner — stored that line as its exact declaration. Measured on the
  shipped `SalesTable`: **362 of its 621 methods** had an unusable signature; after the fix, zero.
  This is worse than an empty signature, because `prepare change`, `get table` and `get class`
  present it as the declaration a CoC wrapper has to match exactly, and a green build cannot
  correct it. `IsStatic` and the inferred return type were derived from the same line and were
  wrong with it. Block comments that close on the declaration line are handled.
  **An index built before this fix keeps the bad signatures — re-run `d365fo index extract`.**
- `extension_info(mode="table-merge")` promised an "effective merged schema" and returned the
  extension roster. Returning a list under a contract that says "merged" is worse than returning
  a list, because a caller that trusts it reads a missing field as a field that does not exist.
  The merge is now computed — see `TableMergeAnalyzer` under Added.
- README claimed 310+ tests (1 373), 19 skills (38), 29 `generate` commands (33), 27 adapter
  tools (28), and enumerated 19 of the 38 topic files. The skill listing is now generated from
  `skills/_source`, so it cannot drift again silently.

### Security
- **`Microsoft.Data.Sqlite` 8.0.10 → 10.0.11**, which moves the transitive `SQLitePCLRaw`
  stack from 2.1.6 to 2.1.12 and clears **GHSA-2m69-gcr7-jv3q** (high severity). NuGet had
  been printing `NU1903` on every build and nothing was watching. The bump also aligns the
  package with the `net10.0` target. Verified against the shape people actually install:
  `scripts/smoke-published-cli.ps1 -Rid win-x64` passes, including the bundled native SQLite
  in the trimmed single-file binary that issue #182 was about.

### Changed — CI
- **Warning-count ratchet** (`scripts/check-build-warnings.ps1`, baseline 117). The count may
  fall freely; any rise fails the run with the new warnings grouped by code. A hundred-odd
  standing warnings are exactly how `NU1903` stayed invisible.
- **The test logger no longer collides.** Both assemblies wrote `test-results.trx` into one
  directory, so the second overwrote the first — and when a run went red, the surviving file
  was usually the assembly that PASSED, leaving the failing test unnamed in the CI artifact.
  Now each writes its own default-named file.
- **The test suite wrote into the developer's real index — and pruned their undo history.**
  `ModificationJournal.ForIndex` resolves the journal to `<dirname(D365FO_INDEX_DB)>/journal`,
  and every scaffold-writing test journals. On a machine where `d365fo init` has configured
  `D365FO_INDEX_DB` — the normal state — a full run appended into the real journal, and the
  500-entry cap then evicted the developer's genuine entries oldest-first. Measured on this
  repo's host: exactly 500 entries, the newest a `scaffold-write` naming a test temp directory.
  Both assemblies also ran as concurrent processes appending to and pruning that one directory.
  A `[ModuleInitializer]` now points each test process at its own index; `D365FO_PACKAGES_PATH`
  is deliberately left alone so the AOT-gated tests still run against a real installation.
- **The suite's intermittent failure had a name after all.** Twenty test classes called the
  process-wide `SqliteConnection.ClearAllPools()` in `Dispose` to unlock their temp database.
  That disposes every pooled connection in the process, including one another test is mid-query
  on, so the loser failed with `ObjectDisposedException: 'SQLitePCL.sqlite3'` from somewhere
  unrelated — about one full-suite run in ten, and never reproducible when an assembly ran
  alone. Replaced with `SqlitePool.ReleaseFor(path)`, which clears only that database's pools
  (all three connection-string spellings this codebase opens). **0 failures in 14 consecutive
  full-suite runs**, against 2–3 per 10 before.
- A flaky assertion in `WorkflowScaffolderTests` and `ScaffoldFileWriterAtomicityTests` read
  "which files were written" off a `Directory.GetFiles` listing taken immediately after the
  writes. Under load that listing can miss an entry the rename just created, which surfaced
  as "Expected 3, Actual 2" with no exception anywhere — the writer had already proven the
  file was there by taking `FileInfo.Length` on it. Existence is now queried per path
  (`WrittenFilesAssert`). A real but secondary cause — the `ClearAllPools` entry above is what
  the remaining failures turned out to be.

### Added — documentation
- This file, and `SECURITY.md`.

---

## 2026-09-01 — trimmed publish

### Fixed
- The trimmed, single-file, self-contained build — the shape `install.ps1` / `install.sh`
  actually produce — failed on every SQLite query and on the native SQLite load, and returned an
  empty journal instead of an error, all while the test suite stayed green (#182). CI now
  publishes that shape and diffs it against an ordinary build
  (`scripts/smoke-published-cli.ps1`).

## 2026-08-31 — the upstream 1.15.0 wave

### Added
- **Compiler-oracle validator**: the validator wave ported from upstream, with the compiler as
  the oracle rather than hand-held expectations.
- **`prepare test`** (`mode=test`): everything needed to write a SysTest, before writing it.
- **Kernel table-method catalog** for `prepare`, EDT `StringSize` inheritance, CoC target
  normalization.
- **`generate report-extension`** — the three compiler-checked ways to extend a shipped report.
- **Report scaffold depth**: `--pre-process`, `--controller-type print-mgmt`, `--ui-builder`.
- **`generate table --field-group` / `--index`**, with valid-time-state index properties.
- `generate table-relation` and `generate find-methods` augmenters, and the catalog-driven form
  expander.
- Three knowledge topics — warehouse-app, barcode-scanning, runtime-functions — plus
  language-core additions, taking the corpus from 35 to 38.
- Labels: search/resolve hits are confirmed against the `.label.txt` before being recommended.

### Fixed
- Every pattern `generate form` emits now compiles; the expanded forms are something the AOS
  accepts.
- The `#184` report DP and contract scaffolds now compile.
- Every scaffolder wraps `Source`/`Declaration` in CDATA (#183).
- Labels resolve the `@File:Id` token shape the AOT actually writes.
- Knowledge corrections carried over from the upstream 1.15.0 wave.
- Kernel enums, red-first SysTest, `NumberSeqFormHandler`, form `DataGroup` honesty.

## 2026-08-21 / 2026-08-23 — generate and modify correctness

### Added
- Fields can be added to a table extension offline.

### Fixed
- `--verify` is honoured on every `generate` subcommand, and only a refused artefact fails it.
- `modify` resolves extension kinds from the registry rather than a private table.
- EDT lookup recovers truncated whole-name matches and matches `GetEdt` case-insensitively.
- Skills frontmatter is quoted and parsed in CI (#172 shipped frontmatter no YAML parser
  could read while CI stayed green).

## 2026-08-06 / 2026-08-07 — forms, eval and validation depth

### Added
- Clone a reference form under a new name (#164).
- Cross-check the shipped catalogs against the installation (#164).
- `test run` invokes the real SysTest runner, and its result document is parsed (#160).
- The grounding gate applies uniformly across `generate`, with property honesty (#161).
- Security and data-entity scaffolding depth (#162).
- Per-family root-shape validation rules XML009–XML013 (#163).

### Fixed
- A renamed datasource is followed into the design and the join (#164).
- A bound field's control type is derived from the field (#164).
- The atomic write is actually atomic (#158).

## 2026-04-23 — bootstrap

### Added
- The `d365fo` CLI, the dual Agent Skills layer (Anthropic + Copilot), the extract pipeline and
  the first parity commands.
