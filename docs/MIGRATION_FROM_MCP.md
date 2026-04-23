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
| `d365fo generate table\|class\|coc\|simple-list` | Scaffold new AOT XML. |
| `d365fo review diff` | Lint AOT XML changes between git revisions. |
| `d365fo build` / `sync` / `test run` / `bp check` | Drive MSBuild / SyncEngine / SysTestRunner / xppbp (Windows + VM). |

See [ROADMAP.md](ROADMAP.md) for items still planned — including full MCP / CLI parity and deeper live-runtime integration.

---

## See also

- [README](../README.md) — the pitch and quick start.
- [USAGE.md](USAGE.md) — complete command reference.
- [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) — why Path B saves tokens.
- [ARCHITECTURE.md](ARCHITECTURE.md) — how the CLI, MCP adapter and Core relate.
