# Setup & Usage

This guide walks you through installing `d365fo-cli`, pointing it at a D365 F&O metadata source, and using it from the command line, CI, and agents (GitHub Copilot, Claude, Codex, Gemini).

If you already know the pitch, jump straight to [Install](#install).

---

## What this tool does (and doesn't)

**Cross-platform (macOS / Linux / Windows)** — no D365 runtime required:

- Index D365 F&O AOT XML metadata (`AxTable`, `AxClass`, `AxEdt`, `AxEnum`, `AxMenuItem*`, `AxLabelFile`) into a local SQLite cache.
- Query the index: `search`, `get`, `find`.
- Scaffold new AOT objects: `generate table|class|coc|simple-list`.
- Review diffs against `git`: `review diff` (lint rules for AOT XML).
- Serve as an MCP server (`d365fo-mcp`, JSON-RPC 2.0) or a warm-cache daemon (`d365fo daemon`).

**Windows + D365FO VM required** (shells out to Microsoft tools):

- `d365fo build` → `MSBuild.exe`
- `d365fo sync` → `SyncEngine.exe`
- `d365fo test run` → `SysTestRunner.exe`
- `d365fo bp check` → `xppbp.exe`

Off-Windows these return a structured `UNSUPPORTED_PLATFORM` error envelope so agents can branch cleanly. **You do not need Windows for development, indexing, scaffolding, or agent usage** — only for `build/sync/test/bp`.

---

## Install

### Prerequisites

- .NET SDK 8 (LTS) — also builds on net10 preview while the project is pre-release.
- `git` (for `review diff`).
- Python 3.8+ **or** PowerShell 7 (for regenerating skills — optional).

### Build from source

```sh
git clone https://github.com/dynamics365ninja/d365fo-cli.git
cd d365fo-cli
dotnet build d365fo-cli.slnx -c Release
```

The binary ends up at `src/D365FO.Cli/bin/Release/net*/D365FO.Cli.dll`. Two options for ergonomic usage:

**(a) Shell alias** — quickest for local development:

```sh
# bash/zsh
alias d365fo='dotnet run --project /path/to/repo/src/D365FO.Cli --'

# PowerShell
function d365fo { dotnet run --project /path/to/repo/src/D365FO.Cli -- @args }
```

**(b) Self-contained publish** — for distribution:

```sh
dotnet publish src/D365FO.Cli -c Release -r win-x64 --self-contained
# or: -r linux-x64, -r osx-arm64
```

The output folder contains a standalone `D365FO.Cli` executable you can rename to `d365fo` and put on `$PATH`.

### Verify

```sh
d365fo version
d365fo doctor
```

`doctor` prints a checklist of the environment (SDK, env vars, index location, workspace). Fix any `ok=false` entries before continuing.

---

## Configure

All configuration is environment-variable driven so it plays well with `.env` files, CI secrets, and VS Code launch profiles.

| Variable | Required | Purpose |
|---|---|---|
| `D365FO_PACKAGES_PATH` | for `index extract` | Root of the D365 F&O `PackagesLocalDirectory`. The extractor walks `<root>/<Package>/<Model>/AxTable/*.xml` etc. |
| `D365FO_INDEX_DB` | optional | Path to the SQLite index. Default: `$LocalAppData/d365fo-cli/d365fo-index.sqlite` (macOS/Linux: `~/.local/share/d365fo-cli/…` via `Environment.SpecialFolder.LocalApplicationData`). |
| `D365FO_WORKSPACE_PATH` | optional | Root of your X++ workspace (used by `review diff` defaults). |
| `D365FO_CUSTOM_MODELS` | optional | CSV of model names to mark `IsCustom=true` in the index. |
| `D365FO_LABEL_LANGUAGES` | optional | CSV of language codes to keep during label extraction. Default: `en-us`. |

Example:

```sh
export D365FO_PACKAGES_PATH=/mnt/d365fo/PackagesLocalDirectory
export D365FO_INDEX_DB=$HOME/.d365fo/index.sqlite
export D365FO_LABEL_LANGUAGES=en-us,cs
```

---

## First run

```sh
# 1. Create / migrate the SQLite index
d365fo index build

# 2. Ingest metadata from PACKAGES_PATH
d365fo index extract
#   or scoped: d365fo index extract --model ApplicationSuite
#   or explicit path: d365fo index extract --packages /mnt/d365fo/PackagesLocalDirectory

# 3. Confirm
d365fo index status
```

`index extract` is **idempotent per model** — re-running replaces that model's rows, so it's safe to run in a watch script. Typical full extract takes seconds for a custom model, minutes for the full ApplicationSuite.

---

## Usage

All commands return a stable JSON envelope by default (piped/non-TTY) and Spectre-rendered tables in an interactive terminal. Override with `--output json|table|raw`.

```json
{ "ok": true,  "data": { … },  "warnings": [ … ] }
{ "ok": false, "error": { "code": "…", "message": "…", "hint": "…" } }
```

### Discover

```sh
d365fo search class Cust          # class names containing "Cust"
d365fo search table Sales --limit 20
d365fo search edt AccountNum
d365fo search enum NoYes
d365fo search label "Invoice"     # sanitised by default; pass --raw-text to opt out

d365fo get table CustTable
d365fo get class SalesLine
d365fo get edt CustAccount
d365fo get enum NoYes
d365fo get menu-item CustTable
d365fo get security CustTable --type Table
d365fo get label SysLabel VendorAccount --lang en-us

d365fo find coc CustTable                 # CoC extensions targeting CustTable
d365fo find relations CustTable           # in- and outbound FK relations
d365fo find usages CustPostInvoiceJob     # any index entity whose name contains the substring
```

### Scaffold

```sh
d365fo generate table FmVehicle \
  --label "@Fleet:Vehicle" \
  --field VIN:VinEdt:mandatory \
  --field Make:Name \
  --out src/MyModel/AxTable/FmVehicle.xml

d365fo generate class FmVehicleService --extends RunBase \
  --out src/MyModel/AxClass/FmVehicleService.xml

d365fo generate coc CustTable --method update --method insert \
  --out src/MyModel/AxClass/CustTable_Extension.xml

d365fo generate simple-list FmVehicleListPage --table FmVehicle \
  --out src/MyModel/AxForm/FmVehicleListPage.xml
```

Scaffolding writes atomically (`.tmp` sibling + move) and keeps a `.bak` when `--overwrite` is used.

### Review

```sh
# Diff working tree vs. HEAD and lint AOT changes
d365fo review diff --base HEAD

# Diff two revs
d365fo review diff --base main --head feature/my-branch
```

Rules currently shipped:

- `FIELD_WITHOUT_EDT` — table field without `<ExtendedDataType>`.
- `FIELD_WITHOUT_LABEL` — user-facing field without `<Label>`.
- `HARDCODED_STRING` — verbatim string literal in X++ source.
- `DYNAMIC_QUERY` — dynamic `Query` construction (flag for security review).

### Windows-only tooling (on the D365FO VM)

```powershell
d365fo build --project C:\AosService\PackagesLocalDirectory\MyModel\MyModel.rnrproj
d365fo sync --full
d365fo test run --suite MyModel.Tests
d365fo bp check --model MyModel
```

Each parses the tool output and returns a structured JSON envelope (errors, warnings, elapsed time, tail of stdout).

### Agent integration

```sh
# Emit an LLM system prompt that documents the CLI
d365fo agent-prompt --out .prompts/d365fo.md

# Emit a machine-readable catalog of every command
d365fo schema --full
```

Drop the prompt into Copilot / Claude / Codex / Gemini system instructions. Skills with lazy-loaded metadata live in [`skills/_source/`](../skills/_source/) — regenerate the two dialects with `python3 scripts/emit-skills.py`.

---

## Agent usage patterns

### GitHub Copilot (VS Code)

```sh
# One-time
cp skills/copilot/* .github/instructions/
d365fo agent-prompt --out .github/copilot-instructions.md
```

Copilot picks up `.github/instructions/*.instructions.md` via `applyTo` globs. Copilot Chat can then invoke `d365fo …` in a terminal tool.

### Claude Code / Claude Desktop

Point Claude Code at `skills/anthropic/` (drop it in the project or `~/.claude/skills/`). Each `SKILL.md` triggers via `applies_when`.

### Codex CLI / Gemini CLI

Paste the output of `d365fo agent-prompt` into the session system prompt, or reference it from `AGENTS.md`.

### MCP server (`d365fo-mcp`)

Real JSON-RPC 2.0 MCP server (protocol `2024-11-05`) published as its own executable. Wire it into Claude Desktop, Cursor, Continue, or VS Code MCP:

```jsonc
// Claude Desktop config
{
  "mcpServers": {
    "d365fo": {
      "command": "dotnet",
      "args": ["run", "--project", "/abs/path/to/src/D365FO.Mcp", "--no-build"],
      "env": { "D365FO_INDEX_DB": "/abs/path/d365fo-index.sqlite" }
    }
  }
}
```

After `dotnet publish src/D365FO.Mcp -c Release -r osx-arm64` you get a standalone `d365fo-mcp` binary you can drop on `$PATH` and reference directly.

Supported methods: `initialize`, `ping`, `tools/list`, `tools/call`. 16 tools are exposed (search/get/find/index_status — same surface as the CLI read commands).

### Daemon (warm cache)

For latency-sensitive integrations, run the CLI as a daemon so the SQLite handle and read caches stay hot:

```sh
d365fo daemon start           # foreground=false, detaches
d365fo daemon status          # running pid + endpoint
d365fo daemon stop            # sends SIGTERM, cleans pid file
```

Transport:
- **Windows:** named pipe `\\.\pipe\d365fo-cli`
- **Unix:** socket at `$XDG_RUNTIME_DIR/d365fo-cli.sock` (fallback `$TMPDIR`)

The daemon speaks the same JSON-RPC 2.0 frame as `d365fo-mcp` — one newline-terminated request per connection, one response, close.

---

## CI / automation

Every command is scriptable because:

- exit codes are reliable (`0` success, `1` controlled failure, `2` unhandled exception),
- output is JSON by default in non-TTY,
- no interactive prompts.

Example GitHub Actions step:

```yaml
- name: D365 review
  run: |
    d365fo index build
    d365fo review diff --base origin/main --head HEAD --output json \
      | jq -e '.data.violationCount == 0'
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `PACKAGES_PATH_NOT_FOUND` | Set `D365FO_PACKAGES_PATH` or pass `--packages <PATH>`. |
| Index file locked in WAL mode | Kill background `d365fo daemon`/MCP processes; the CLI uses `SqliteCacheMode.Private` but WAL leftover files (`-wal`, `-shm`) stay alongside the DB. |
| `UNSUPPORTED_PLATFORM` | `build/sync/test/bp` require Windows + D365FO dev VM. Run them there. |
| Extract missed a package | Confirm `<root>/<Package>/<Model>/AxTable/…` layout. Some packages live two levels deep; point `--packages` at the actual `PackagesLocalDirectory`. |
| Label values contain junk | By default `search label` / `get label` strip control characters. Pass `--raw-text` to see the unfiltered value. |

---

## Where to go next

- Architecture and guardrails — [docs/ARCHITECTURE.md](ARCHITECTURE.md)
- Why CLI+Skills vs. MCP — [docs/TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md)
- Coming from the original MCP server — [docs/MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md)
