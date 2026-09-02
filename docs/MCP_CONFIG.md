# MCP configuration

Pointing an MCP client at the bundled `d365fo-mcp` adapter. It serves the same SQLite index and
the same handlers the CLI uses — [MCP_TOOLS.md](MCP_TOOLS.md) is the generated map of which tool
reaches which command.

> **If your agent has a shell, use the CLI instead.** The adapter exists for hosts that cannot run
> a command: Claude Desktop, ChatGPT, an editor MCP panel. Registering it costs 40 132 characters
> of tool schemas in the model's context on *every* turn against the CLI skill's 366 — measured,
> not estimated, in [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md).

---

## The two transports

```sh
d365fo-mcp                      # stdio — one process per developer, spawned by the client
d365fo-mcp --http --port 8080   # HTTP  — one shared instance for a team
```

stdio is the default and needs no ports, no auth and no deployment: the client starts the process
and speaks JSON-RPC over its pipes. HTTP is for the shared case and is covered in
[SETUP_AZURE.md](SETUP_AZURE.md).

There is a third: `--legacy` runs the built-in dispatcher instead of the official SDK transport.
One JSON object per line, no framing, which makes it scriptable — `scripts/emit-mcp-tools.py` and
`scripts/measure-context-cost.py` both drive it. Same `ToolCatalog`, same handlers.

---

## Client configuration

Every client wants the same three things: the executable, any arguments, and the environment that
tells it where the index and the packages are.

### Claude Code / Claude Desktop

Claude Code reads `.mcp.json` in the project — the file `d365fo connect --editor claude` writes.
Claude Desktop reads its own `claude_desktop_config.json`. Same shape either way:

```json
{
  "mcpServers": {
    "d365fo": {
      "command": "d365fo-mcp",
      "env": {
        "D365FO_PACKAGES_PATH": "K:\\AosService\\PackagesLocalDirectory",
        "D365FO_INDEX_DB": "C:\\Users\\me\\AppData\\Local\\d365fo-cli\\d365fo-index.sqlite"
      }
    }
  }
}
```

### VS Code

`.vscode/mcp.json`:

```json
{
  "servers": {
    "d365fo": {
      "type": "stdio",
      "command": "d365fo-mcp",
      "env": { "D365FO_PACKAGES_PATH": "K:\\AosService\\PackagesLocalDirectory" }
    }
  }
}
```

### Cursor, Continue, and other MCP hosts

Same shape — an executable plus environment. If the host has no `env` block, set the variables
where it is launched from, or put them in `~/.d365fo/config.json`, which the adapter reads too
(see [CONFIGURATION.md](CONFIGURATION.md#json-config-file)).

### Pointing at a deployed HTTP instance

```sh
d365fo connect https://d365fo-mcp.example.com --editor vscode --api-key <key>
```

`connect` probes `GET /health` first, then **merges** the entry into the client's config rather
than rewriting the file — an existing server of another name is left alone. `--force` writes even
when the probe fails; `--no-probe` skips it.

---

## Environment

The adapter reads the same variables the CLI does ([CONFIGURATION.md](CONFIGURATION.md)); these
are the ones that decide whether it works at all:

| Variable | Why the adapter cares |
|---|---|
| `D365FO_INDEX_DB` | Which index to serve. Defaults to the CLI's own, so a locally built index is picked up with no configuration. |
| `D365FO_PACKAGES_PATH` | Needed by everything that reads X++ source or writes AOT XML — `get_method`, `generate_object`, `labels`. Read-only lookups against the index do not need it. |
| `D365FO_BRIDGE_ENABLED` | `modify_*` and `generate_object --install-to` go through the local bridge process; without it they refuse rather than half-work. |

And three the adapter alone has:

| Variable | Values | Effect |
|---|---|---|
| `MCP_SERVER_MODE` | `full` (default) · `read-only` · `write-only` | Gates which tools are advertised **and** callable. The gate is re-checked at call time, so a client holding a stale tool list cannot get past it. |
| `API_KEY` | any string | Required value of the `X-Api-Key` header on `POST /mcp`. Unset means no auth, with a startup warning. HTTP only. |
| `MCP_HTTP_PORT` | port number | Listen port when `--port` is not passed (default `3000`). |

---

## Checking it works

```sh
# 1. Does the adapter start and see an index?
d365fo-mcp --http --port 8080 &
curl http://localhost:8080/health
# {"status":"ok","mode":"full","indexReachable":true}

# 2. What does a client actually receive?
printf '%s\n%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | d365fo-mcp --legacy
```

`indexReachable: false` means the adapter started but cannot open the database — usually
`D365FO_INDEX_DB` pointing somewhere the client's environment does not have, which is the single
most common MCP-only failure. Run `d365fo index status` in the same environment the client
launches from to see the path it would resolve.

---

## See also

- [MCP_TOOLS.md](MCP_TOOLS.md) — the 32 tools and the commands behind them (generated).
- [SETUP_AZURE.md](SETUP_AZURE.md) — running one shared instance for a team.
- [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md) — coming from `d365fo-mcp-server`.
- [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) — what registering the adapter costs per turn.
