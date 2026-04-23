# Migration from d365fo-mcp-server

You do **not** have to migrate. The CLI is additive. Pick a path:

## Path A — keep MCP, add CLI (recommended)

Your existing `.mcp.json` and `%USERPROFILE%\.github\copilot-instructions.md`
stay. Layer the CLI on top:

1. Build the CLI: `dotnet build d365fo-cli.slnx -c Release`.
2. Publish a self-contained binary: `dotnet publish
   src/D365FO.Cli -r win-x64 -c Release --self-contained -p:PublishSingleFile=true`.
3. Drop `d365fo.exe` on `PATH`.
4. Copy `skills/copilot/*.instructions.md` into your solution's
   `.github/instructions/` folder. Copilot will load frontmatter only; bodies
   load when a glob matches.
5. Delete or keep MCP tools from your `.mcp.json` as you see fit. Skills and
   MCP can coexist; the LLM will prefer whichever is cheaper for the task.

## Path B — CLI-only (full token saving)

1. Steps 1–4 above.
2. Remove `d365fo-*` entries from `.mcp.json`.
3. Remove the old TS server from disk (it is versioned in the upstream repo
   under the tag `legacy-typescript`).

## Path C — MCP still required (no shell in harness)

Keep `d365fo-mcp-server` as-is, or use `D365FO.Mcp` (JSON-RPC 2.0 over
stdio; 16 read tools today). Either way the SQLite index schema is
shared, so switching is a configuration change, not a re-index.

## Index compatibility

The CLI's SQLite schema lives in `src/D365FO.Core/Index/Schema.sql` and is
currently at **v5** (tracked via `PRAGMA user_version`). It is a superset of
the upstream MCP server's layout — pointing the CLI at an existing
`d365fo-mcp-server` database just works:

```sh
export D365FO_INDEX_DB=C:/path/to/existing/d365fo.sqlite
d365fo index status
```

The CLI auto-applies `EnsureSchema` on first connection, so older databases
are migrated forward transparently. No destructive migrations run without
explicit confirmation, and `ApplyExtract` is idempotent per-model (re-extract
replaces that model's rows).

## Mapping (abbreviated)

Headline mappings between the upstream MCP tools and CLI commands:

| MCP tool | CLI command |
|---|---|
| `search_classes` | `d365fo search class <q>` |
| `get_table_details` | `d365fo get table <name>` (now also includes indexes, methods, delete actions) |
| `get_edt_details` | `d365fo get edt <name>` |
| `find_coc_extensions` | `d365fo find coc <Class>[::<method>]` |
| `get_security_coverage_for_object` | `d365fo get security <obj> --type <kind>` |
| `search_labels` | `d365fo search label <q> --lang en-us,cs` |
| `get_menu_item_details` | `d365fo get menu-item <name>` |
| `get_table_relations` | `d365fo find relations <table>` |

CLI-only surface that has no upstream MCP equivalent (yet):

| Command | Purpose |
|---|---|
| `d365fo search query\|view\|entity\|report\|service\|workflow` | Index queries, views, data entities (by name **or** OData `PublicEntityName`/`PublicCollectionName`), SSRS/RDL reports, SOAP services, workflow types. |
| `d365fo get form\|role\|duty\|privilege\|query\|view\|entity\|report\|service\|service-group` | Full details for each object type. |
| `d365fo find extensions <Target>` | Enumerate Table/Form/Edt/Enum extensions targeting an object. |
| `d365fo find handlers <Source>` | Event subscribers bound to a form/table/delegate. |
| `d365fo resolve label @SYS12345 [--lang …]` | Resolve an `@File+Key` token to its text across indexed languages. |
| `d365fo read class\|table\|form <Name> [--method X] [--declaration]` | Read embedded X++ source from the AOT XML. |
| `d365fo models list` / `d365fo models deps <Name>` | List indexed models or show their Descriptor-declared dependency graph (`depends-on` + `depended-by`). |
| `d365fo index extract --model <Name>` | Incremental per-model re-extract. |

Generator, build, sync, test, BP-check commands are wired under the
`generate`, `build`, `sync`, `test`, `bp`, `review` branches. The longer-term
roadmap (X++ reverse references, FTS5, live OData, daemon/MCP parity, etc.)
lives in [docs/ROADMAP.md](ROADMAP.md).
