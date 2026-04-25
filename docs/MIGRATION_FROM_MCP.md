# Migrating from `d365fo-mcp-server`

> **Audience:** existing users of the legacy TypeScript `d365fo-mcp-server`.
> **Good news:** you do **not** have to migrate. The CLI is additive, the database schema is compatible, and `d365fo-mcp-server` continues to work.

## Contents

1. [Decision: which path is right for you?](#decision-which-path-is-right-for-you)
2. [Path A — keep MCP, add CLI (recommended)](#path-a--keep-mcp-add-cli-recommended)
3. [Path B — go CLI-only (maximum token saving)](#path-b--go-cli-only-maximum-token-saving)
4. [Path C — stay on MCP (no shell in your AI harness)](#path-c--stay-on-mcp-no-shell-in-your-ai-harness)
5. [Index compatibility](#index-compatibility)
6. [Command mapping](#command-mapping)

---

## Decision: which path is right for you?

| Situation | Path |
|---|---|
| You want both options side-by-side; let the agent pick the cheaper one per task. | **A** (keep MCP, add CLI) |
| You've moved to an agent harness with a shell (Copilot, Claude Code, Codex, Gemini CLI) and want the full token saving. | **B** (CLI-only) |
| Your agent runs in a host **without** a shell tool (plain Claude.ai chat, plain ChatGPT Web). | **C** (stay on MCP) |

All three paths use the **same SQLite index schema** — switching between them is a configuration change, not a re-index. See [Index compatibility](#index-compatibility) below.

## Path A — keep MCP, add CLI (recommended)

Your existing `.mcp.json` and `~/.github/copilot-instructions.md` stay as-is. Layer the CLI on top:

1. Build the CLI:
   ```sh
   dotnet build d365fo-cli.slnx -c Release
   ```
2. Publish a self-contained binary:
   ```sh
   dotnet publish src/D365FO.Cli -r win-x64 -c Release \
     --self-contained -p:PublishSingleFile=true
   ```
3. Drop `d365fo.exe` on `PATH`.
4. Copy `skills/copilot/*.instructions.md` into your solution's `.github/instructions/`. Copilot will load frontmatter only; bodies load when a glob matches.
5. Delete or keep MCP entries in your `.mcp.json` as you see fit. The LLM will use whichever is cheaper for each task.

## Path B — go CLI-only (maximum token saving)

1. Steps 1–4 from Path A above.
2. Remove `d365fo-*` entries from `.mcp.json`.
3. Remove the old TS server from disk — it's versioned in the upstream repo under the tag `legacy-typescript`, so you can recover it any time.

## Path C — stay on MCP (no shell in your AI harness)

Keep `d365fo-mcp-server` as-is, or switch to the new `D365FO.Mcp` adapter (JSON-RPC 2.0 over stdio; 16 read tools today). Both read from the same SQLite index.

## Index compatibility

The CLI's SQLite schema lives in [`src/D365FO.Core/Index/Schema.sql`](../src/D365FO.Core/Index/Schema.sql) and is currently at **v5** (tracked via `PRAGMA user_version`). It is a **superset** of the upstream MCP server's layout — pointing the CLI at an existing `d365fo-mcp-server` database just works:

```sh
export D365FO_INDEX_DB=/path/to/existing/d365fo.sqlite
d365fo index status
```

- `EnsureSchema` runs on first connection, so older databases are migrated forward transparently.
- No destructive migrations run without explicit confirmation.
- `ApplyExtract` is idempotent per-model (re-extract replaces that model's rows only).

## Knowledge transfer

The CLI repo ports the **complete X++ rule canon** that has been refined over time in `d365fo-mcp-server`'s `.github/copilot-instructions.md` and `src/prompts/systemInstructions.ts`. Same wisdom, CLI-flavoured surface:

| Source (MCP) | Destination (CLI) |
|---|---|
| `.github/copilot-instructions.md` (master rule sheet) | [`.github/copilot-instructions.md`](../.github/copilot-instructions.md) — same structure, MCP tool names mapped to `d365fo` shell commands. |
| `src/prompts/systemInstructions.ts` (runtime prompt) | `d365fo agent-prompt` (emits the same canon to stdout or a file). |
| MS Learn citations (`xpp-select-statement`, `method-wrapping-coc`, `xpp-classes-methods`, `xpp-conditional`, `xpp-variables-data-types`) | Cited verbatim in both the master sheet and per-skill files. |
| Workflow templates (refactor / CoC / table fields / data-event / form / security) | Same workflows, `d365fo` commands instead of MCP tool calls. |

Skills coverage in `skills/_source/` (regenerate Copilot + Anthropic flavours via `python3 scripts/emit-skills.py`):

| Skill | Topic |
|---|---|
| `coc-extension-authoring.md` | CoC rules: no defaults in wrapper, `next` scope, `[Hookable]`/`[Wrappable]`, form-nested wrapping. |
| `xpp-class-and-method-rules.md` | Class default access, instance fields protected, constructor pattern, override visibility, extension methods. |
| `xpp-database-queries.md` | `select` grammar, `crossCompany` on outer buffer, `in container`, no nested `while select`, set-based ops, `validTimeState`. |
| `xpp-statement-and-type-rules.md` | `switch break`, ternary, no-DB-null sentinels, `as`/`is`, `using` blocks. |
| `xpp-best-practice-rules.md` | BP rules: today→DateTimeUtil, label-typed messages, alternate keys, doc comments, EDT migration. |
| `table-scaffolding.md` | Pattern presets (Main/Transaction/Parameter/Group/Worksheet/Reference), TableGroup vs TableType, EDT label inheritance, alternate-key rule. |
| `form-pattern-scaffolding.md` | Nine D365FO form patterns (SimpleList, SimpleListDetails, DetailsMaster, DetailsTransaction, Dialog, TableOfContents, Lookup, ListPage, Workspace) — anti-patterns of hand-rolled XML. |
| `data-entity-scaffolding.md` | `AxDataEntityView` for OData / DMF; PublicEntityName / PublicCollectionName conventions. |
| `object-extension-authoring.md` | Table / Form / Edt / Enum extensions (NOT class CoC); `<Target>.<Suffix>` shape; collision check. |
| `event-handler-authoring.md` | `[DataEventHandler]` for standard data events vs `[SubscribesTo + delegateStr]` for custom delegates. |
| `label-translation.md` | Label CRUD (`label create / rename / delete`), reuse over create, `--raw-text` injection guard. |
| `security-hierarchy-trace.md` | Role / duty / privilege traversal; `get security`. |
| `model-dependency-and-coupling.md` | `models deps`, `models coupling`, layer ordering, cycle detection. |
| `review-and-checkpoint-workflow.md` | Git-checkpoint workflow + AOT-semantic `review diff`. |
| `x++-class-authoring.md` | General workflow recipe (class authoring with the index). |

If upstream `d365fo-mcp-server` updates its rule canon, sync this repo's [`.github/copilot-instructions.md`](../.github/copilot-instructions.md), `src/D365FO.Cli/Commands/Agent/AgentPromptCommand.cs`, and `skills/_source/`.

## Command mapping

### MCP tool → CLI command

| MCP tool | CLI command |
|---|---|
| `search_classes` | `d365fo search class <q>` |
| `get_table_details` | `d365fo get table <name>` *(now also includes indexes, methods, delete actions)* |
| `get_edt_details` | `d365fo get edt <name>` |
| `find_coc_extensions` | `d365fo find coc <Class>[::<method>]` |
| `get_security_coverage_for_object` | `d365fo get security <obj> --type <kind>` |
| `search_labels` | `d365fo search label <q> --lang en-us,cs` |
| `get_menu_item_details` | `d365fo get menu-item <name>` |
| `get_table_relations` | `d365fo find relations <table>` |
| `generate_smart_form` | `d365fo generate form <Name> --pattern <P>` (all 9 patterns: `SimpleList`, `SimpleListDetails`, `DetailsMaster`, `DetailsTransaction`, `Dialog`, `TableOfContents`, `Lookup`, `ListPage`, `Workspace`) |

### CLI-only surface (no upstream MCP equivalent)

| Command | Purpose |
|---|---|
| `d365fo search query\|view\|entity\|report\|service\|workflow` | Index queries, views, data entities (by name or OData `PublicEntityName`/`PublicCollectionName`), SSRS/RDL reports, SOAP services, workflow types. |
| `d365fo get form\|role\|duty\|privilege\|query\|view\|entity\|report\|service\|service-group` | Full details for each object type. |
| `d365fo find extensions <Target>` | Enumerate Table / Form / Edt / Enum extensions targeting an object. |
| `d365fo find handlers <Source>` | Event subscribers bound to a form / table / delegate. |
| `d365fo resolve label @SYS12345 [--lang …]` | Resolve an `@File+Key` token to its text across indexed languages. |
| `d365fo read class\|table\|form <Name> [--method X] [--declaration]` | Read embedded X++ source from the AOT XML. |
| `d365fo models list` / `d365fo models deps <Name>` | List indexed models or show their Descriptor-declared dependency graph. |
| `d365fo index extract --model <Name>` | Incremental per-model re-extract. |
| `d365fo generate table\|class\|coc\|form\|entity\|extension\|event-handler\|privilege\|duty\|role` | Scaffold new AOT XML (table, class, CoC class, AxForm with 9 patterns, data entity, extension, event handler, security artefacts). |
| `d365fo review diff` | Lint AOT XML changes between git revisions. |
| `d365fo build` / `sync` / `test run` / `bp check` | Drive MSBuild / SyncEngine / SysTestRunner / xppbp (Windows + VM). |

See [ROADMAP.md](ROADMAP.md) for items still planned — including full MCP / CLI parity and deeper live-runtime integration.

---

## See also

- [README](../README.md) — the pitch and quick start.
- [SETUP.md](SETUP.md) — install and configure.
- [EXAMPLES.md](EXAMPLES.md) — one worked example per command.
- [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) — why Path B saves tokens.
- [ARCHITECTURE.md](ARCHITECTURE.md) — how the CLI, MCP adapter and Core relate.
