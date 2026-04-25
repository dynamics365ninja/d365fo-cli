# d365fo-cli

> **A command-line toolkit for Dynamics 365 Finance & Operations X++ development — designed for AI agents, usable by humans.**

`d365fo-cli` indexes your D365 F&O AOT metadata into a local database and gives you one friendly command — `d365fo` — to search it, scaffold new objects, review changes, and drive the D365FO developer tools. It is the successor to [`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) and works with every major AI assistant: GitHub Copilot, Claude (Code / Desktop), Cursor, Codex, and Gemini.

---

## What you get

- 🔎 **Instant AOT lookup** — find tables, classes, EDTs, enums, forms, queries, views, reports, services, workflows, security roles, and labels in milliseconds, without touching a D365 VM.
- 🏗️ **Scaffold X++ objects** — generate ready-to-use XML for tables, classes, Chain-of-Command extensions, and AxForms (all nine D365FO patterns: `SimpleList`, `SimpleListDetails`, `DetailsMaster`, `DetailsTransaction`, `Dialog`, `TableOfContents`, `Lookup`, `ListPage`, `Workspace`); optionally drop them straight into a model folder.
- 🕵️ **Understand the code** — trace CoC targets, relations, event handlers, label translations, and reverse references across your workspace.
- 🤖 **AI-ready** — every command returns a stable JSON envelope, so agents can parse results reliably. A pre-built system prompt and lazy-loaded Skills keep token usage low.
- 🧪 **Scriptable** — runs in PowerShell, bash/zsh, CI/CD pipelines, and cron jobs. No host application required.
- 🪟 **Optional VM integration** — when run on a D365FO developer VM, it also drives `MSBuild`, `SyncEngine`, `SysTestRunner`, and `xppbp`.
- 🧩 **MCP still supported** — a thin `d365fo-mcp` adapter ships alongside, sharing the same index.

Cross-platform: **Windows, macOS, Linux**. You only need Windows for build/sync/test/bp commands on a real D365FO VM — all indexing, searching, and scaffolding works anywhere.

---

## Installation

### Prerequisites

- .NET SDK 10 (the repo's `global.json` pins the exact version).
- Optional: `git` (for `review diff`), Python 3.8+ or PowerShell 7 (to regenerate Skills).

### Build from source

```sh
git clone https://github.com/dynamics365ninja/d365fo-cli.git
cd d365fo-cli
dotnet build d365fo-cli.slnx -c Release
```

### Add `d365fo` to your PATH

**Option A — shell alias (fastest):**

```sh
# bash / zsh
alias d365fo='dotnet run --project /path/to/d365fo-cli/src/D365FO.Cli --'

# PowerShell
function d365fo { dotnet run --project C:\path\to\d365fo-cli\src\D365FO.Cli -- @args }
```

**Option B — standalone binary (distribution):**

```sh
dotnet publish src/D365FO.Cli -c Release -r win-x64 --self-contained
# or: -r linux-x64, -r osx-arm64
```

Rename the output executable to `d365fo` (or `d365fo.exe`) and place it on your `PATH`.

### Verify

```sh
d365fo version
d365fo doctor     # environment checklist (SDK, env vars, index, workspace)
```

---

## Quick Start

```sh
# 1. Tell the CLI where your PackagesLocalDirectory is
export D365FO_PACKAGES_PATH=/mnt/d365fo/PackagesLocalDirectory
export D365FO_INDEX_DB=$HOME/.d365fo/index.sqlite

# 2. Create the local index
d365fo index build

# 3. Ingest AOT metadata (full, or scoped to a model)
d365fo index extract
d365fo index extract --model ApplicationSuite

# 4. Ask the index anything
d365fo search class Cust
d365fo get table CustTable
d365fo find coc CustTable::validateWrite
d365fo resolve label @SYS12345 --lang en-US,cs

# 5. Scaffold a new table
d365fo generate table FmVehicle \
  --label "@Fleet:Vehicle" \
  --field VIN:VinEdt:mandatory \
  --field Make:Name \
  --out src/MyModel/AxTable/FmVehicle.xml

# 6. Emit a system prompt for your AI agent
d365fo agent-prompt --out .prompts/d365fo.md
```

Every command returns a predictable JSON envelope:

```json
{ "ok": true,  "data": { /* … */ }, "warnings": [] }
{ "ok": false, "error": { "code": "…", "message": "…", "hint": "…" } }
```

In an interactive terminal you get nicely rendered tables. In scripts and pipes you get JSON automatically. Force either with `--output json|table|raw`.

---

## Configuration

Configuration is environment-variable based, so it plays well with `.env` files, CI secrets, and launch profiles.

| Variable | Purpose |
|---|---|
| `D365FO_PACKAGES_PATH` | Root of D365 F&O `PackagesLocalDirectory` (needed for `index extract`). |
| `D365FO_INDEX_DB` | Path to the local SQLite index. Defaults to your local app-data folder. |
| `D365FO_WORKSPACE_PATH` | Root of your X++ workspace (used by `review diff`). |
| `D365FO_CUSTOM_MODELS` | Comma-separated patterns marking your own models (wildcards `*`, `?`, negation `!`). |
| `D365FO_LABEL_LANGUAGES` | Comma-separated languages to keep when extracting labels (default `en-us`). |
| `D365FO_BRIDGE_ENABLED` | `1`/`true` to route reads through the live D365FO Metadata Bridge (Windows + VM only). |

See [`docs/SETUP.md`](docs/SETUP.md#configure) for the full list.

---

## Commands

| Group | Commands | What it does |
|---|---|---|
| **Index** | `index build`, `index extract`, `index status` | Build, populate, and inspect the local metadata cache. |
| **Search** | `search class\|table\|edt\|enum\|form\|query\|view\|entity\|report\|service\|workflow\|label` | Fuzzy-find AOT objects by name. |
| **Get** | `get table\|class\|edt\|enum\|form\|menu-item\|security\|label\|role\|duty\|privilege\|query\|view\|entity\|report\|service\|service-group` | Fetch full details (fields, methods, relations, indexes, …). |
| **Find** | `find coc`, `find relations`, `find usages`, `find extensions`, `find handlers`, `find refs` | Trace Chain-of-Command, references, handlers, relationships. |
| **Read** | `read class`, `read table`, `read form` | Pull source snippets for a method, declaration, or range. |
| **Resolve** | `resolve label` | Look up multi-language label text by token. |
| **Generate** | `generate table\|class\|coc\|form\|entity\|extension\|event-handler\|privilege\|duty\|role` | Scaffold AOT XML for new objects (forms support 9 D365FO patterns). |
| **Review** | `review diff` | Lint AOT changes between two git revs. |
| **Models** | `models list`, `models deps` | List models and trace dependencies. |
| **Agent** | `agent-prompt`, `schema` | Emit system prompts and machine-readable catalogs for AI agents. |
| **Daemon** | `daemon start\|status\|stop` | Warm-cache daemon for latency-sensitive integrations. |
| **Ops (Windows + VM)** | `build`, `sync`, `test run`, `bp check` | Drive `MSBuild.exe`, `SyncEngine.exe`, `SysTestRunner.exe`, `xppbp.exe`. |

See [`docs/EXAMPLES.md`](docs/EXAMPLES.md) for one worked example per command.

---

## AI agent integration

`d365fo-cli` is built to be driven by AI agents. Two pieces make that easy:

1. **A system prompt** that tells the agent what commands exist and the full X++/CoC/BP rule canon (including MS Learn citations):
   ```sh
   d365fo agent-prompt --out .prompts/d365fo.md
   ```
   The same canon is also available as [`/.github/copilot-instructions.md`](.github/copilot-instructions.md) for GitHub Copilot — drop it in the consuming repo's `.github/` folder.
2. **Skills** (15 topics) — short, lazy-loaded recipes covering the X++ rule canon ported from `d365fo-mcp-server`. Generation workflows: table scaffolding (with pattern presets), form patterns (9 templates), CoC extensions, object extensions (Table/Form/Edt/Enum), data entities, event handlers, label CRUD, security hierarchy. Language rules: X++ database queries (`select` / `crossCompany` / `in` / set-based ops), class & method rules (modifier order, fields-protected), statement & type rules (switch, ternary, no-DB-null sentinels, `as`/`is`), and best-practice rules (`BPUpgradeCodeToday`, `BPErrorLabelIsText`, alt-key, doc comments). Analyst skills: model dependency / coupling, Git-checkpoint review workflow. Source skills live in [`skills/_source/`](skills/_source/) and are emitted in two formats:

   ```sh
   python3 scripts/emit-skills.py     # or: pwsh scripts/emit-skills.ps1
   ```

   | Agent | Where to put the skills |
   |---|---|
   | **GitHub Copilot** (VS Code / Visual Studio) | Copy `skills/copilot/*.instructions.md` into `.github/instructions/`. |
   | **Claude Code / Claude Desktop** | Point at `skills/anthropic/` (drop in the project or `~/.claude/skills/`). |
   | **Codex CLI / Gemini CLI** | Reference the relevant `SKILL.md` in your session prompt or `AGENTS.md`. |

Need MCP? The `d365fo-mcp` binary speaks JSON-RPC 2.0 (protocol `2024-11-05`) over stdio and reuses the same index — wire it into Claude Desktop, Cursor, Continue, or VS Code MCP. See [`docs/EXAMPLES.md#mcp-server-d365fo-mcp`](docs/EXAMPLES.md#mcp-server-d365fo-mcp) for a config sample.

---

## Why a CLI instead of MCP?

MCP servers inject every tool definition into the model's context on every single turn. For this project that used to be **54 tools ≈ 2,900 tokens every turn**. A CLI + Skills approach flips that:

| | MCP server | CLI + Skills |
|---|---|---|
| Tool definitions per turn | 54 tools (~2,900 tokens) | 1 shell tool (~100 tokens) |
| Discovery round-trips | 2–3 per task | often 1 (`d365fo get table X`) |
| Scriptable (shell, CI) | No | Yes |
| Works in any AI harness with a shell | No — MCP-supporting hosts only | Yes — Copilot, Claude Code, Codex, Gemini, … |

Over a 15-turn workflow that typically means **~90% fewer tokens spent on tool plumbing**. See [`docs/TOKEN_ECONOMICS.md`](docs/TOKEN_ECONOMICS.md) for the full math and the cases where MCP still wins.

---

## Project layout

```
src/
  D365FO.Core/      Shared library (index, repository, guardrails, scaffolding)
  D365FO.Cli/       The `d365fo` command-line binary (Spectre.Console.Cli)
  D365FO.Mcp/       Optional MCP server (shares D365FO.Core)
  D365FO.Bridge/    Optional net48 metadata bridge (Windows + D365FO VM)
skills/
  _source/          Source Skills (Markdown + YAML)
  copilot/          Emitted GitHub Copilot instructions
  anthropic/        Emitted Anthropic SKILL.md
scripts/            emit-skills.py / emit-skills.ps1
tests/              xUnit test suites
docs/               Deeper docs (see below)
```

---

## Documentation

| Doc | What's inside |
|---|---|
| [`docs/SETUP.md`](docs/SETUP.md) | Install, configure, verify — two scenarios (dev alias vs. self-contained distribution). |
| [`docs/EXAMPLES.md`](docs/EXAMPLES.md) | One worked example per command (discover, scaffold, review, ops, agents, daemon, CI). |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | How the pieces fit together — index schema, guardrails, bridge. |
| [`docs/TOKEN_ECONOMICS.md`](docs/TOKEN_ECONOMICS.md) | Why CLI+Skills is cheaper per turn, with numbers. |
| [`docs/MIGRATION_FROM_MCP.md`](docs/MIGRATION_FROM_MCP.md) | Coming from `d365fo-mcp-server`? Read this first. |
| [`docs/ROADMAP.md`](docs/ROADMAP.md) | Planned and deferred items. |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `PACKAGES_PATH_NOT_FOUND` | Set `D365FO_PACKAGES_PATH` or pass `--packages <PATH>`. |
| `UNSUPPORTED_PLATFORM` | `build` / `sync` / `test` / `bp` require Windows + a D365FO dev VM. |
| Index file appears locked | Stop any running `d365fo daemon` or `d365fo-mcp` process; WAL sidecar files (`-wal`, `-shm`) are normal. |
| Extract missed a package | Confirm the `<root>/<Package>/<Model>/AxTable/…` layout and point `--packages` at the real `PackagesLocalDirectory`. |

More in [`docs/SETUP.md#troubleshooting`](docs/SETUP.md#troubleshooting).

---

## License

MIT. The upstream [`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) is also MIT.

---

## Disclaimer

This project is an independent research effort and is not affiliated with, endorsed by, or associated with Microsoft or any other organization. It is provided as-is for educational and development purposes.
