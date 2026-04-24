# Roadmap

> **Audience:** contributors and users who want to know what's coming next.
> **Living document.** Only items that are **not yet implemented** live here. Everything already shipped is documented in [SETUP.md](SETUP.md) / [EXAMPLES.md](EXAMPLES.md) (user-visible surface) and [ARCHITECTURE.md](ARCHITECTURE.md) (internals). Git history preserves earlier design notes.

Items are grouped by topic and ordered roughly by ROI; within a section you can pick any order.

## Contents

1. [Refresh & observability](#1-refresh--observability)
2. [Runtime / live data](#2-runtime--live-data)
3. [More AOT types](#3-more-aot-types)
4. [Output & integration](#4-output--integration)
5. [Scaffolding extensions](#5-scaffolding-extensions)
6. [Code quality & Best Practices](#6-code-quality--best-practices)
7. [Tests](#7-tests)
8. [Small items / technical debt](#8-small-items--technical-debt)

---

## 1. Refresh & observability

### 1.1 Fingerprint-based incremental refresh

`d365fo index refresh [--force]` and `d365fo index extract --since <ISO>` already skip models whose newest XML mtime is older than the DB's last-write timestamp (minus a 5-minute safety margin). Remaining work: schema bump adding `Models.LastExtractedUtc` / `Models.SourceFingerprint` so refresh becomes per-model content-addressed (no false-positive re-extracts when only the DB was touched).

### 1.2 `d365fo index diff <revision>`

Structural AOT diff vs. a git revision — e.g. "three new fields on `CustTable`, method `validate` signature changed". Requires a double extract or snapshotting.

### 1.3 Persisted extraction telemetry

Per-model timings are already emitted in the JSON envelope (`elapsedMs` per model + top-level total). Remaining work: an `_index_meta` table (or sidecar `.log`) so history survives across runs and can feed `d365fo stats`.

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

- `generate duty --into-role <NAME>` / `generate privilege --into-role <NAME>` — single-pass "scaffold + wire" (today the parts ship separately: scaffold the duty/privilege file, then `generate role --add-to <path>` merges references). Would fold those two steps into one.
- Hand-rolled serialisers for selected Ax* kinds (e.g. `AxTableExtension`, `AxSecurityRole`) where `AxSerializer`'s generic walker elides detail the agent actually wants — the depth cap + cycle guard means we currently fall back to `Name` on deeply nested overlays.

## 6. Code quality & Best Practices

### 6.1 Additional lint categories

`d365fo lint` ships with 3 categories (`table-no-index`, `ext-named-not-attributed`, `string-without-edt`) and SARIF output (`--format sarif`) for CI. Pending:

- "method with public API but no doc-comment" — requires method-source indexing (§3.6).
- "UI literal string without `@Label`" — requires parsing element captions on forms / menu items.

### 6.2 Coupling metrics

Graph metrics over `ModelDependencies` + `DYNAMICSXREFDB` — surfaces cyclic dependencies and top incoming / outgoing symbols.

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
