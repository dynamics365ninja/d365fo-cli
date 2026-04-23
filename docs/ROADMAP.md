# Roadmap

> **Audience:** contributors and users who want to know what's coming next.
> **Living document.** Only items that are **not yet implemented** live here. Everything already shipped is documented in [USAGE.md](USAGE.md) (user-visible surface) and [ARCHITECTURE.md](ARCHITECTURE.md) (internals). Git history preserves earlier design notes.

Items are grouped by topic and ordered roughly by ROI; within a section you can pick any order.

## Contents

1. [Refresh & observability](#1-refresh--observability)
2. [Runtime / live data](#2-runtime--live-data)
3. [More AOT types](#3-more-aot-types)
4. [Search & ergonomics](#4-search--ergonomics)
5. [Output & integration](#5-output--integration)
6. [Scaffolding extensions](#6-scaffolding-extensions)
7. [Code quality & Best Practices](#7-code-quality--best-practices)
8. [Tests](#8-tests)
9. [Small items / technical debt](#9-small-items--technical-debt)

---

## 1. Refresh & observability

### 1.1 `d365fo index refresh` (mtime-based incremental)

Per model, compute max `LastWriteTimeUtc` under `Descriptor/*.xml` + `Ax*/*.xml`. Compare against new columns `Models.LastExtractedUtc` / `Models.SourceFingerprint`. Re-extract only changed models. Schema bump to v6. CLI: `d365fo index refresh [--force]`.

### 1.2 `d365fo index diff <revision>`

Structural AOT diff vs. a git revision — e.g. "three new fields on `CustTable`, method `validate` signature changed". Requires a double extract or snapshotting.

### 1.3 Extraction telemetry

Per-model timings + error summaries. Either an `_index_meta` table or a sidecar `.log`.

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

## 4. Search & ergonomics

### 4.1 Full-text search (SQLite FTS5)

`LabelFts(Value, Key, File, Language)` (and optionally `Source`). Moves `d365fo search label "customer invoice"` from `LIKE` scans to rank-sorted FTS — tens of ms instead of hundreds.

### 4.2 Parametric aggregation (`d365fo stats`)

Per-model counts, top-N largest tables, classes missing Best-Practice attributes, etc.

### 4.3 Multi-scope search (`d365fo search any <substring>`)

Scope-agnostic quick jump across all indexed kinds.

## 5. Output & integration

### 5.1 Persistent daemon mode (JSON-RPC)

Skeleton exists (`D365FO.Cli.Commands.Daemon`). Add 1:1 request routing with the CLI, warm SQLite pool, file-watcher triggering `index refresh`. Also hosts the bridge child process across calls (sub-50 ms warm vs. ~300 ms cold).

### 5.2 MCP stdio parity (`D365FO.Mcp`)

Bring CLI → MCP tool surface to 1:1 parity. Include `tools/list` with descriptions and sample JSON args for LLM prompting.

### 5.3 Structured diff output

`--output patch` for `generate *` — apply as a text patch without touching the workspace.

### 5.4 Session cache

`.d365fo-session.json` next to the index; keeps the last active model / recent `get`s for prompt hints.

## 6. Scaffolding extensions

- `generate extension <table|form|edt|enum> <Target>`.
- `generate entity <Table>` — `AxDataEntityView` with all table fields, OData names by convention.
- `generate privilege <EntryPoint>`, `generate duty <Privilege[]>`, wire into role.
- `generate event-handler <SourceKind> <SourceObject> <Event>`.

## 7. Code quality & Best Practices

### 7.1 In-process BP runner (no VM)

Static checks over the index:

- "table has no cluster index" (`TableIndexes`),
- "class named `*_Extension` without `[ExtensionOf]`",
- "method with public API but no doc-comment",
- "UI literal string without `@Label`".

CLI: `d365fo lint [--category X,Y] --output sarif` for CI.

### 7.2 Coupling metrics

Graph metrics over `ModelDependencies` + `DYNAMICSXREFDB` — surfaces cyclic dependencies and top incoming / outgoing symbols.

## 8. Tests

- End-to-end: freeze a sample `AxRepo` in `tests/Samples/MiniAot/`, run `MetadataExtractor` + `MetadataRepository`, verify counts.
- Snapshot tests for JSON output of `get table`, `get class`, `models deps`.
- Performance smoke: `MeasureExtract(ApplicationSuite)` cap (runs only when `D365FO_PACKAGES_PATH` is set).
- Bridge: end-to-end harness that spins the net48 exe against a sample `PackagesLocalDirectory` fixture and asserts round-trip for read / create / update / delete.

## 9. Small items / technical debt

- `tests/D365FO.Cli.Tests` is empty — at least a smoke test of Spectre registration.
- Audit every `Render` call site for `StringSanitizer` coverage.
- `Models.IsCustom` single source of truth between `UpsertModelInternal` (first-seen deps) and `ApplyExtract` (`UPDATE`).
- Error codes (`TABLE_NOT_FOUND`, `MODEL_NOT_FOUND`, …) should live in an enum, not magic strings.
- Log `schema v{X} applied` line in `index build`.
- Hand-rolled serialisers for selected Ax* kinds (e.g. `AxTableExtension`, `AxSecurityRole`) where `AxSerializer`'s generic walker elides detail the agent actually wants — the depth cap + cycle guard means we currently fall back to `Name` on deeply nested overlays.

---

## See also

- [USAGE.md](USAGE.md) — what you can do today.
- [ARCHITECTURE.md](ARCHITECTURE.md) — where each item fits in the codebase.
