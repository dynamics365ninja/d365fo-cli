# Migration from d365fo-mcp-server

You do **not** have to migrate. The CLI is additive. Pick a path:

## Path A — keep MCP, add CLI (recommended)

Your existing `.mcp.json` and `%USERPROFILE%\.github\copilot-instructions.md`
stay. Layer the CLI on top:

1. Build the CLI: `dotnet build d365fo-cli.slnx -c Release`.
2. Publish a self-contained binary (Phase 5): `dotnet publish
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

Keep `d365fo-mcp-server` as-is, or migrate to `D365FO.Mcp` when its
JSON-RPC transport lands (Phase 4). Either way the SQLite index schema is
shared, so switching is a configuration change, not a re-index.

## Index compatibility

The CLI's SQLite schema (`src/D365FO.Core/Index/Schema.sql`, v1) mirrors the
upstream MCP server's layout. An existing `d365fo-mcp-server` database can be
pointed at by the CLI:

```sh
export D365FO_INDEX_DB=C:/path/to/existing/d365fo.sqlite
d365fo index status
```

If the schema drifts between the two, the CLI will auto-`EnsureSchema` on
first run. No destructive migrations run without explicit confirmation.

## Mapping (abbreviated)

Full mapping of all 54 upstream MCP tools to CLI commands lives in
`docs/TOOL_MAPPING.md` (populated alongside Phase 2 parity work). Highlights:

| MCP tool | CLI command |
|---|---|
| `search_classes` | `d365fo search class <q>` |
| `get_table_details` | `d365fo get table <name>` |
| `get_edt_details` | `d365fo get edt <name>` |
| `find_coc_extensions` | `d365fo find coc <Class>::<method>` |
| `get_security_coverage_for_object` | `d365fo get security <obj> --type <kind>` |
| `search_labels` | `d365fo search label <q> --lang en-us,cs` |
| `get_menu_item_details` | `d365fo get menu-item <name>` |
| `get_table_relations` | `d365fo find relations <table>` |

Generator, build, sync, test, BP-check commands are wired in subsequent
commits under the `generate`, `build`, `sync`, `test`, `bp`, `review` command
branches.
