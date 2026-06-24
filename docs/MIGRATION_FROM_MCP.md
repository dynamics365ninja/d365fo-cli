# MCP Server vs CLI

Both tools share an **identical purpose** — give GitHub Copilot access to real D365FO metadata so it generates X++ code that compiles on the first attempt. They differ in protocol and deployment, but share the same data layer.

| | `d365fo-mcp-server` | `d365fo` CLI |
|---|---|---|
| Protocol | MCP tools (JSON-RPC over stdio / HTTP) | Shell commands |
| Implementation | TypeScript + Node.js | C# / .NET 10 |
| Data layer | SQLite index + C# Bridge | **Shared** — same index, schema v5 is a superset |
| Deployment | Local or Azure App Service (shared team instance) | Local |
| Copilot integration | MCP tool calls | Shell tool |
| Token economy | Larger JSON envelopes | Smaller output — see [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) |
| CLI-only commands | — | `review diff`, `build/sync/test/bp`, `schema`, batch operations, `read`, `models` |

---

## Which solution to use?

| Situation | Recommendation |
|---|---|
| GitHub Copilot Chat (VS / VS Code) — primary target | **CLI** (shell tool) — or both side by side |
| Team shares one instance without a local install | MCP on Azure App Service |
| Agent without a shell tool (Claude.ai web, ChatGPT web) | MCP |
| Want both — Copilot picks the cheaper one per task | Side by side (Path A below) |

### When to run both side by side

Run them side by side only when a single index must serve agents with **mixed shell-tool support** (e.g. Copilot in VS Code alongside Claude.ai web), or during a **migration grace period** before you confirm the CLI path covers everything.

Be aware of the cost: keeping MCP registered injects its full schema overhead (~1,800 tok/turn — see [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md)) on **every** turn, including turns where only the CLI is called. The "Copilot picks the cheaper one" benefit applies per *call*, not per *turn*, so for a single agent with a working shell tool, prefer **CLI only (Path B)** — it is both cheaper and simpler.

---

## Migration

### Path A — side-by-side operation (mixed environments / migration)

The existing `.mcp.json` and `copilot-instructions.md` stay unchanged. The CLI is added alongside:

1. Build and deploy the CLI — see [SETUP.md](SETUP.md).
2. Copy `skills/copilot/*.instructions.md` to `.github/instructions/` in your X++ project.
3. Copilot automatically uses the shell tool for CLI commands and MCP for tool calls — both from the same index.

> **Heads-up — `copilot-instructions.md` collision.** The per-topic skills in `.github/instructions/*.instructions.md` have unique filenames and coexist fine. But both the CLI and `d365fo-mcp-server` ship a top-level `.github/copilot-instructions.md` and install it with `Copy-Item -Force`, so it's last-installer-wins, not a merge — re-running the other installer clobbers it again. Keep **one** canon file: the CLI's, which is the schema-v5 superset and already documents the shell-first flow plus a no-shell fallback. You don't need the MCP server's instruction file for its tools to work — MCP tools are registered via `.mcp.json` and their schemas are self-describing; the instruction file only guides behaviour, it doesn't enable the tools.

### Path B — CLI only

1. Steps 1–2 from Path A.
2. Remove the `d365fo-*` entries from `.mcp.json`.

### Path C — MCP only

Leave `d365fo-mcp-server` unchanged. The `D365FO.Mcp` adapter in this repository reads from the same SQLite index via JSON-RPC 2.0 stdio.

---

## Index compatibility

The CLI schema (`src/D365FO.Core/Index/Schema.sql`, `PRAGMA user_version = 5`) is a superset of the MCP server. Point an existing database at it:

```sh
export D365FO_INDEX_DB=/path/to/existing/d365fo.sqlite
d365fo index status
```

`EnsureSchema` migrates the schema forward automatically on first connection. Re-extract is idempotent per model.

---

## The CLI's own MCP tool surface (unified)

This repository also ships `d365fo-mcp`, a JSON-RPC 2.0 adapter over the same
index. Its tool surface was **consolidated to mirror the upstream server**:
~70 per-type tools collapsed into **20 discriminator-based** tools (a single
tool dispatches on a `type` / `objectType` / `mode` / `action` / `domain` /
`include` field). Fewer tools for the agent to choose from, identical coverage.
This tracks the upstream `d365fo-mcp-server`, which itself consolidated 34 → 26
tools — folding the per-mechanism extension/handler readers into one
`extension_info`, merging the generate scaffolders into `generate_object`, and
renaming `form_pattern` → `object_patterns`.

| Unified tool | Discriminator | Replaces |
|---|---|---|
| `search` | `type`, `queries[]` (batch) | all per-type `search_*`, `search_any`, `batch_search`, `find_usages` |
| `get_object_info` | `objectType` (+ `relations`/`methods`/`indexes`/`deleteActions` for tables) | all `get_*_details`, `get_form/query/view/...`, `get_table_*` |
| `get_method` | `include` (signature/source/both) | reads X++ source (was CLI `read` only) |
| `labels` | `action` (search/fts/info/resolve/create/rename/delete); `create` also accepts a bulk `labels:[{key,value}, …]` array with shared top-level fields | `search_labels(_fts)`, `get_label`, `resolve_label`, `create/rename/delete_label` |
| `security_info` | `mode` (artifact/coverage) | `get_security_role/duty/privilege`, `get_security_coverage_for_object` |
| `object_patterns` | `domain` (form) + `action` (spec/validate) | `get_form_pattern_spec`, `validate_form_pattern`, `form_pattern` |
| `generate_object` | `objectType` — table/class/coc/form (writes to disk) **and** edt/enum/query/sysoperation/business-event/runbase/security-policy (XML only) | `generate_table/class/coc/form` + the XML-only scaffolders (was two tools `generate` + `generate_xml`) |
| `extension_info` | `mode` (coc/events/table-merge/points/strategy) | `find_coc_extensions`, `find_event_handlers`, `find_extensions`, `get_table_extension_info`, `analyze_extension_points` (5 tools → 1) |
| `analyze` | `mode` (integration/impact/report) | `analyze_integration`, `analyze_impact`, `report_integrations` |
| `models` | `action` (list/deps/coupling) | `list_models`, `get_model_dependencies`, `models_coupling` |
| `prepare` | `mode` (change/create) | single-round context aggregator + grounding token (was CLI-only) |
| `find_references` | — | reverse references via regex scan of indexed X++ source (was CLI-only) |
| `validate_object_naming`, `get_workspace_info`, `suggest_edt`, `batch_get_info`, `lint`, `stats`, `index_status`, `index_history` | — | kept (parity names) |

This adapter exposes **20 unified MCP tools** (the upstream `d365fo-mcp-server`
sits at 26 — it additionally ships `get_knowledge`, `analyze_code`,
`d365fo_file`, `undo_last_modification`, `validate_code`, `verify_d365fo_project`
and the SDLC/build tools, which here are CLI-only or covered by Skills).

Run `d365fo schema --full` for the machine-readable command/tool manifest; every
CLI command's `mcpTool` field names the unified MCP tool it maps to.

---

## Command mapping

Map of the upstream `d365fo-mcp-server`'s **consolidated** tool surface onto CLI
commands. Both surfaces use the same discriminator-based naming.

### Discovery & read

| MCP tool | CLI command |
|---|---|
| `search` (default) | `d365fo search any <q>` |
| `search` (`queries[]`) | `d365fo search batch <q1> <q2> …` |
| `batch_get_info` | `d365fo get batch table:CustTable class:X …` (max 10) |
| `extension_info` (`mode=points`) | `d365fo find extensions <Target>` |
| `labels` (`action=search`) | `d365fo labels search <q> --lang en-us,cs` |
| `get_object_info` (`objectType=class\|table\|edt\|enum\|form\|query\|view\|report\|entity\|menu-item`) | `d365fo get <kind> <name>` or `d365fo get object <kind> <name>` |
| `extension_info` (`mode=table-merge`) | `d365fo find extensions <Table> --kind Table` |
| `get_method` (`include=signature`) | `d365fo get class <Name>` (signatures included) |
| `get_method` (`include=source`) | `d365fo read class <Name> --method <M>` |
| `labels` (`action=info\|resolve`) | `d365fo labels info` / `d365fo labels resolve @SYS12345` |
| `security_info` (`mode=artifact`) | `d365fo security role\|duty\|privilege <Name>` |
| `security_info` (`mode=coverage`) | `d365fo security coverage <obj> --type <kind>` |
| `extension_info` (`mode=coc`) | `d365fo find coc <Class>[::<method>]` |
| `extension_info` (`mode=events`) | `d365fo find event-handlers <Target>` |
| `find_references` / `resolve_references` | `d365fo find references <Name>` / `d365fo validate references --file <F>` |
| `get_workspace_info` | `d365fo doctor` + `d365fo index status` (incl. stale-index detection) |

### Grounded generation & gates

| MCP tool | CLI command |
|---|---|
| `prepare` (`mode=change`) | `d365fo prepare change <Object> --method <M> --goal "…"` → grounding token |
| `prepare` (`mode=create`) | `d365fo prepare create <Name> --type <T> --goal "…"` → grounding token |
| `validate_xpp` | `d365fo validate xpp [FILE]` (offline BP rules, data-driven property stats) |
| `validate_object_naming` | `d365fo validate name <KIND> <NAME>` |
| `object_patterns` (`domain=form, action=validate`) | `d365fo form-pattern validate [FILE]` (FP001–FP010) |
| `object_patterns` (`domain=form, action=analyze`) | `d365fo form-pattern analyze [--pattern\|--table\|--similar-to]` |
| `object_patterns` (`domain=form, action=spec`) | `d365fo form-pattern spec [NAME]` |
| `generate_object` (`objectType=table`, patterns) | `d365fo generate table --pattern <P>` (P1 patterns built in) |
| `extension_info` (`mode=strategy`) / `recommend_extension_strategy` | `d365fo suggest extension <Target>` |
| `suggest_edt` | `d365fo suggest edt <FieldName>` |
| `analyze` (`mode=…`) | `d365fo analyze completeness\|integration\|impact` |

### Writes

| MCP tool | CLI command |
|---|---|
| `d365fo_file` (`action=create`) / `generate_object` | `d365fo generate <kind> … --install-to <Model>` |
| `generate_object` (`objectType=table`) | `d365fo generate table <Name> --pattern <P> --field …` |
| `generate_object` (`objectType=form`) | `d365fo generate form <Name> --pattern <P>` (pattern-gated write) |
| `generate_object` (XML-only `objectType=…`) | `d365fo generate edt\|enum\|query\|sysoperation\|business-event\|runbase\|security-policy` (XML only) |
| `d365fo_file` (`action=modify`) | targeted editor edit of CDATA method bodies + `d365fo index refresh`; structural changes via `generate … --overwrite` |
| `undo_last_modification` | `.bak` backups written by every overwrite + `git checkout` |
| `labels` (`action=create\|rename\|delete`) | `d365fo labels create\|rename\|delete` (multi-language via `--lang`) |
| `review_workspace_changes` | `d365fo review diff` |
| `update_symbol_index` | `d365fo index refresh [--model <M>]` |

### Ops (Windows VM)

| MCP tool | CLI command |
|---|---|
| `build_d365fo_project` / `verify_d365fo_project` | `d365fo build` (structured `xppcDiagnostics`) |
| `trigger_db_sync` | `d365fo sync` |
| `run_bp_check` | `d365fo bp check` (offline subset: `d365fo validate xpp`) |
| `run_systest_class` | `d365fo test run --suite <S>` |

### Knowledge tools → Skills

| MCP tool | CLI equivalent |
|---|---|
| `get_knowledge` (`kind=knowledge`) | 19 lazy-loaded Skills (`skills/anthropic/`, `skills/copilot/`) — X++ rule canon loaded only when relevant |
| `get_knowledge` (`kind=error`) | `xpp-best-practice-rules` skill + `d365fo validate xpp` diagnostics with `fix` hints |
| `analyze_code` (`mode=api-usage\|patterns`) | `xpp-database-queries`, `x++-class-authoring`, … skills + `d365fo find references` |
| `analyze_code` (`mode=implementations`) / `code_completion` | covered by the host agent grounded via `prepare change` + `read class` |

### CLI-only commands (no MCP equivalent)

| Command | Purpose |
|---|---|
| `d365fo schema [--full]` / `d365fo agent-prompt` | Agent manifest / system prompt for any harness |
| `d365fo analyze integration\|impact`, `report-integrations` | OData/DMF readiness, change-impact, integration surface report |
| `d365fo models coupling` | Fan-in/fan-out/instability/cycles over model dependencies |
| `d365fo index export\|import\|optimize\|history` | Index snapshots for CI caching, VACUUM, extraction telemetry |
| `d365fo daemon start\|status\|stop\|warmup` | Warm-cache IPC server with AOT file watcher |
| `d365fo lint`, `d365fo stats`, `d365fo find batch-jobs` | Index-wide BP gate, aggregate counters, batch-job inventory |
| `d365fo completion` | Shell tab-completion (bash, zsh, powershell) |

---

## See also

- [SETUP.md](SETUP.md) — CLI installation and configuration.
- [EXAMPLES.md](EXAMPLES.md) — one example per command.
- [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) — why the CLI saves tokens compared to MCP.
- [ARCHITECTURE.md](ARCHITECTURE.md) — relationship between the CLI, MCP adapter, and Core layer.
