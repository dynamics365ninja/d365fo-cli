<sub>[README](../README.md) › [Docs](README.md) › MCP configuration</sub>

# 🔌 MCP configuration

Pointing an MCP client at the bundled `d365fo-cli-mcp` adapter. It serves the same SQLite index and
the same handlers the CLI uses — [MCP_TOOLS.md](MCP_TOOLS.md) is the generated map of which tool
reaches which command.

> **If your agent has a shell, use the CLI instead.** The adapter exists for hosts that cannot run
> a command: Claude Desktop, ChatGPT, an editor MCP panel. Registering it costs 49 076 characters
> of tool schemas in the model's context on *every* turn against the CLI skill's 600 — measured,
> not estimated, in [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md).

---

`npm install -g d365fo-cli` installs the adapter beside the CLI, as the `d365fo-cli-mcp` command
([SETUP.md](SETUP.md#install-with-npm)). On Windows that command is an npm shim; a host that starts
processes without a shell needs `"command": "cmd"`, `"args": ["/c", "d365fo-cli-mcp"]`.

---

## `d365fo-cli-mcp` or `d365fo-mcp`?

Two projects serve D365FO metadata over MCP, and their tool surfaces differ:

| | This repository | [`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) |
|---|---|---|
| Command | `d365fo-cli-mcp` | `d365fo-mcp` (npm package `d365fo-mcp`) |
| MCP `serverInfo.name` | `d365fo-cli-mcp` | `d365fo-mcp-server` (instances add a suffix) |
| `service` in `GET /health` | `d365fo-cli-mcp` | `d365fo-mcp-server` |

`d365fo connect` warns when the URL you give it answers as `d365fo-mcp-server`.

This adapter was called `d365fo-mcp` before. An editor config with `"command": "d365fo-mcp"` that
means this adapter has to say `d365fo-cli-mcp`.

---

## The two transports

```sh
d365fo-cli-mcp                      # stdio — one process per developer, spawned by the client
d365fo-cli-mcp --http --port 8080   # HTTP  — one shared instance for a team
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
      "command": "d365fo-cli-mcp",
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
      "command": "d365fo-cli-mcp",
      "env": { "D365FO_PACKAGES_PATH": "K:\\AosService\\PackagesLocalDirectory" }
    }
  }
}
```

### Cursor, Continue, and other MCP hosts

Same shape — an executable plus environment. If the host has no `env` block, set the variables
where it is launched from, or put them in `%LOCALAPPDATA%\d365fo-cli\settings.json`, which the adapter reads too
(see [CONFIGURATION.md](CONFIGURATION.md#json-config-file)).

### Pointing at a deployed HTTP instance

```sh
d365fo connect https://d365fo-cli-mcp.example.com --editor vscode --api-key <key>
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

And four the adapter alone has:

| Variable | Values | Effect |
|---|---|---|
| `MCP_SERVER_MODE` | `full` (default) · `read-only` · `write-only` | Gates which tools are advertised **and** callable. The gate is re-checked at call time, so a client holding a stale tool list cannot get past it. |
| `MCP_TOOLSETS` | `all` (default), or a comma-separated list of toolsets and tool names | Advertises only those tools, to cut the schema a host injects every turn. Same as `d365fo-cli-mcp --toolsets`. Intersected with `MCP_SERVER_MODE`, gated at call time the same way, and an unknown name stops the server at startup. See [Toolsets](#toolsets). |
| `API_KEY` | any string | Required value of the `X-Api-Key` header on `POST /mcp`. Unset means no auth, with a startup warning. HTTP only. |
| `MCP_HTTP_PORT` | port number | Listen port when `--port` is not passed (default `3000`). |

### Toolsets

A host puts every advertised tool's schema into every turn: about 49k characters for all 32 tools.
A client that only looks things up can drop the rest:

```json
{ "mcpServers": { "d365fo": { "command": "d365fo-cli-mcp", "args": ["--toolsets", "read"] } } }
```

| Toolset | Tools | Schema chars |
|---|---|---:|
| `read` | `search`, `batch_get_info`, `get_object_info`, `get_method`, `find_references`, `find_tables_by_field`, `extension_info`, `security_info`, `labels`, `models` | 11 159 |
| `knowledge` | `get_knowledge`, `object_patterns`, `explain_build_error`, `suggest_edt`, `validate_object_naming` | 5 688 |
| `author` | `prepare`, `generate_object`, `modify_method`, `modify_object`, `delete_object`, `undo_last_modification`, `journal_list` | 19 805 |
| `check` | `validate`, `lint`, `analyze`, `verify_project` | 5 286 |
| `index` | `stats`, `index_status`, `index_sync`, `index_history`, `get_workspace_info` | 3 938 |
| `sdlc` | `sdlc` | 2 698 |

Entries combine, and a single tool name works too: `read,knowledge,prepare`. The `initialize`
instructions then name the tools the server does not offer, so the model does not reach for one.
A toolset is a size control, not a safety one: `read` keeps `labels`, whose `create`/`delete`
actions write label files. For a server that must not write, use `MCP_SERVER_MODE=read-only`
(it drops `labels` and `get_method` too, which read the local package tree).

---

## Checking it works

```sh
# 1. Does the adapter start and see an index?
d365fo-cli-mcp --http --port 8080 &
curl http://localhost:8080/health
# {"status":"ok","mode":"full","indexReachable":true}

# 2. What does a client actually receive?
printf '%s\n%s\n' \
  '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}}' \
  '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}' \
  | d365fo-cli-mcp --legacy
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
