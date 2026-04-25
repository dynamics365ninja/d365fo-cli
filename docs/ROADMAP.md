# Roadmap

> **Audience:** contributors and users who want to know what's coming next.
> **Living document.** Only items that are **not yet implemented** live here. Everything already shipped is documented in [SETUP.md](SETUP.md) / [EXAMPLES.md](EXAMPLES.md) (user-visible surface) and [ARCHITECTURE.md](ARCHITECTURE.md) (internals). Git history preserves earlier design notes.

Items are ordered top-down by priority. Sections labelled **P0 / P1 / …** are
the active backlog (lower number = ship sooner). Sections without a P-tag are
long-tail ideas grouped by topic.

## Contents

### Active backlog (priority-ordered)

- [P0 — Form pattern parity with `d365fo-mcp-server`](#p0--form-pattern-parity-with-d365fo-mcp-server)
- [P1 — Smart-table pattern presets](#p1--smart-table-pattern-presets--shipped)
- [P2 — Form / table pattern analyzer (`find form-patterns`)](#p2--form--table-pattern-analyzer-find-form-patterns)
- [P3 — Smart report scaffold (`generate report`)](#p3--smart-report-scaffold-generate-report)
- [P4 — Extension strategy advisor + completeness analyzer](#p4--extension-strategy-advisor--completeness-analyzer)

### Long-tail / topical

1. [Refresh & observability](#1-refresh--observability)
2. [Runtime / live data](#2-runtime--live-data)
3. [More AOT types](#3-more-aot-types)
4. [Output & integration](#4-output--integration)
5. [Scaffolding extensions](#5-scaffolding-extensions)
6. [Code quality & Best Practices](#6-code-quality--best-practices)
7. [Tests](#7-tests)
8. [Small items / technical debt](#8-small-items--technical-debt)

---

## P0 — Form pattern parity with `d365fo-mcp-server`

**Status:** ✅ shipped. The CLI now ships `d365fo generate form <Name> --pattern <P>` with full parity to upstream MCP's `generate_smart_form`. Templates live as embedded resources under [`src/D365FO.Core/Scaffolding/FormTemplates/`](../src/D365FO.Core/Scaffolding/FormTemplates/) and are exercised by [`FormPatternScaffoldingTests`](../tests/D365FO.Cli.Tests/FormPatternScaffoldingTests.cs).

The legacy `d365fo generate simple-list` command still works as a thin alias for `--pattern SimpleList`.

| Pattern | Reference form | Use case |
|---|---|---|
| `SimpleList` | `CustGroup` | setup / config tables |
| `SimpleListDetails` | `PaymTerm` | medium entities, list + detail panel |
| `DetailsMaster` | `CustTable` | full master record |
| `DetailsTransaction` | `SalesTable` | header + lines (orders) |
| `Dialog` | `ProjTableCreate` | popup dialog |
| `TableOfContents` | `CustParameters` | tabbed parameter pages |
| `Lookup` | `SysLanguageLookup` | dropdown lookups |
| `ListPage` | `CustTableListPage` | navigation list page |
| `Workspace` | `VendPaymentWorkspace` | KPI tiles + panorama sections |

See [EXAMPLES.md → Form](EXAMPLES.md#form-any-of-nine-d365fo-patterns) for usage.

## P1 — Smart-table pattern presets ✅ shipped

`d365fo generate table` now accepts `--pattern <P>` and emits the canonical
`<TableGroup>`, default field skeleton, and an alternate-key
`<AxTableIndex AlternateKey=Yes>` index — so the scaffold passes
BP `BPCheckAlternateKeyAbsent` out of the box.

| Surface | Reference |
|---|---|
| Pattern enum + alias normaliser | [src/D365FO.Core/Scaffolding/TablePattern.cs](../src/D365FO.Core/Scaffolding/TablePattern.cs) |
| Default field presets per pattern | `TablePatternPresets.DefaultFieldsFor` |
| Scaffolder integration (TableGroup / TableType / index) | [src/D365FO.Core/Scaffolding/XppScaffolder.cs](../src/D365FO.Core/Scaffolding/XppScaffolder.cs) |
| CLI flags `--pattern`, `--table-type`, `--primary-key` | [src/D365FO.Cli/Commands/Generate/GenerateCommands.cs](../src/D365FO.Cli/Commands/Generate/GenerateCommands.cs) |
| Tests (16 cases incl. TempDB→TableType guard, alias matrix, alt-key) | [tests/D365FO.Cli.Tests/TablePatternScaffoldingTests.cs](../tests/D365FO.Cli.Tests/TablePatternScaffoldingTests.cs) |
| Skill | [skills/_source/table-scaffolding.md](../skills/_source/table-scaffolding.md) |

Patterns shipped: `Main`, `Transaction`, `Parameter`, `Group`,
`WorksheetHeader`, `WorksheetLine`, `Reference`, `Framework`, `Miscellaneous`.
Aliases (`master`, `setup`, `config`, `transactional`, `lookup`, `header`,
`line`, …) are normalised. Passing `TempDB` / `InMemory` to `--pattern` is
**rejected** with a hint pointing to `--table-type`.

Deferred to a later iteration: live "copy fields from CustTable" via the
bridge, EDT auto-relation migration (BP `BPErrorEDTNotMigrated`), and
pattern *analysis* of indexed tables (P2).

## P2 — Form / table pattern analyzer (`find form-patterns`) ✅ shipped

`d365fo find form-patterns` analyses every indexed `AxForm` by reading
`<Design><Pattern>` / `<PatternVersion>` and the primary datasource. The
index schema bumped to **v8** (extra columns on `Forms`/`FormDataSources`
backfilled lazily on next `index extract`).

| File | Purpose |
|---|---|
| [src/D365FO.Core/Index/Schema.sql](../src/D365FO.Core/Index/Schema.sql) | `Forms.Pattern/PatternVersion/Style/TitleDataSource`, `FormDataSources.OrderIndex/JoinSource`. |
| [src/D365FO.Core/Index/MetadataRepository.cs](../src/D365FO.Core/Index/MetadataRepository.cs) | v8 migration; `FindFormPatterns(...)` + `SummarizeFormPatterns()`. |
| [src/D365FO.Core/Extract/MetadataExtractor.cs](../src/D365FO.Core/Extract/MetadataExtractor.cs) | `ParseForm` reads `<Design>` pattern hints. |
| [src/D365FO.Cli/Commands/Find/FindCommands.cs](../src/D365FO.Cli/Commands/Find/FindCommands.cs) | `FindFormPatternsCommand`. |
| [tests/D365FO.Core.Tests/FormPatternAnalyzerTests.cs](../tests/D365FO.Core.Tests/FormPatternAnalyzerTests.cs) | 4 tests covering extractor + repository + filters. |

CLI surface:

```sh
d365fo find form-patterns                        # histogram across the index
d365fo find form-patterns --pattern SimpleList   # all forms with that pattern (prefix)
d365fo find form-patterns --table CustTable      # every form bound to CustTable
d365fo find form-patterns --similar-to CustGroup # peers with same pattern + primary table
d365fo find form-patterns --pattern ListPage --table CustTable --model ApplicationSuite
```

Deferred follow-ups: permission histogram (`AllowEdit/Create/Delete`),
table-pattern *analysis* of indexed tables (cluster by group/index/relation
shape), and a `find table-patterns --similar-to <Table>` peer.

## P3 — Smart report scaffold (`generate report`)

Port `generate_smart_report` → `XppScaffolder.Report(...)`. Produces an
`AxReport` with one DP class reference, one design and a tablix, plus a
matching `AxClass` skeleton implementing `SrsReportDataProviderBase`.

## P4 — Extension strategy advisor + completeness analyzer

Two MCP capabilities still missing:

- **`extension_strategy_advisor`** — `d365fo suggest extension <Target>` recommends *Class CoC* vs *Event handler* vs *Form/Table extension* based on what the target exposes (delegate count, sealed methods, attribute usage). Outputs ranked options with one-line rationale.
- **`analyze_completeness`** — `d365fo analyze completeness <Project>` cross-checks a workspace project against indexed AOT (e.g. role references a missing duty, table has an EDT not present in any model, label key has no translation).

---

## 1. Refresh & observability

### 1.1 `d365fo index diff <revision>`

Structural AOT diff vs. a git revision — e.g. "three new fields on `CustTable`, method `validate` signature changed". Requires a double extract or snapshotting. (The complementary fingerprint-based incremental refresh and the `ExtractionRuns` telemetry table shipped in schema v7; see EXAMPLES.md `index refresh` and `index history`.)

## 2. Runtime / live data

### 2.1 Live OData connector

`d365fo live entity <Name> --tenant … --env …` → calls `/data/$metadata` + `/data/<Collection>?$top=1`. Auth via `DefaultAzureCredential` or `D365FO_CLIENT_ID/SECRET`. Follow-on: `live call`, `live batch`.

### 2.2 Live metadata reconciliation

Compare offline `DataEntities` against live `$metadata` — surfaces entities inactive in an AOS or missing between Tier-1 / Tier-2.

### 2.3 Health / DMF (Windows VM)

`d365fo health entities`, `d365fo dmf push <Project>.zip`. Builds on existing `build` / `sync`.

## 3. More AOT types

Long-tail metadata not yet indexed:

- **3.1** AggregateDimension / Kpi / Perspective.
- **3.2** Tile / Workspace.
- **3.3** ReferenceGroup / Map / MapExtension.
- **3.4** ConfigurationKey / LicenseCode — cross-referenced to tables / fields / EDTs.
- **3.5** Feature (Feature Management).
- **3.6** X++ method source indexing — upstream MCP parity (`get_method_source`, `analyze_code_patterns`, `suggest_method_implementation`). Needs schema bump for `MethodSources(ClassOrTable, Method, Body, Signature, Kind)`.

## 4. Output & integration

### 4.1 Persistent daemon mode (JSON-RPC)

Skeleton exists (`D365FO.Cli.Commands.Daemon`). Add 1:1 request routing with the CLI, warm SQLite pool, file-watcher triggering `index refresh`. Also hosts the bridge child process across calls (sub-50 ms warm vs. ~300 ms cold).

### 4.2 Structured diff output

`--output patch` for `generate *` — apply as a text patch without touching the workspace.

### 4.3 Session cache

`.d365fo-session.json` next to the index; keeps the last active model / recent `get`s for prompt hints.

## 5. Scaffolding extensions

- Hand-rolled serialisers for selected Ax* kinds (e.g. `AxTableExtension`, `AxSecurityRole`) where `AxSerializer`'s generic walker elides detail the agent actually wants — the depth cap + cycle guard means we currently fall back to `Name` on deeply nested overlays.

## 6. Code quality & Best Practices

### 6.1 Additional lint categories

`d365fo lint` ships with 3 categories (`table-no-index`, `ext-named-not-attributed`, `string-without-edt`) and SARIF output (`--format sarif`) for CI. Pending:

- "method with public API but no doc-comment" — requires method-source indexing (§3.6).
- "UI literal string without `@Label`" — requires parsing element captions on forms / menu items.

### 6.2 Richer coupling metrics

`d365fo models coupling` ships fan-in / fan-out / instability plus Tarjan SCC cycle detection over `ModelDependencies`. Remaining ideas:

- DYNAMICSXREFDB-backed object-level coupling (which class references which EDT / table) beyond the descriptor graph.
- HTML / DOT graph export (GraphViz `digraph`) so the output can feed CI dashboards.

## 7. Tests

- End-to-end: freeze a sample `AxRepo` in `tests/Samples/MiniAot/`, run `MetadataExtractor` + `MetadataRepository`, verify counts.
- Snapshot tests for JSON output of `get table`, `get class`, `models deps`.
- Performance smoke: `MeasureExtract(ApplicationSuite)` cap (runs only when `D365FO_PACKAGES_PATH` is set).
- Bridge: end-to-end harness that spins the net48 exe against a sample `PackagesLocalDirectory` fixture and asserts round-trip for read / create / update / delete.

## 8. Small items / technical debt

- Migrate remaining magic-string error codes to `D365FO.Core.D365FoErrorCodes` (canonical constants exist; call-sites are converting incrementally).
- Audit every `Render` call site for `StringSanitizer` coverage.

---

## See also

- [EXAMPLES.md](EXAMPLES.md) — what you can do today.
- [ARCHITECTURE.md](ARCHITECTURE.md) — where each item fits in the codebase.
