# d365fo — AI-native CLI for D365 F&O X++

<div align="center">

**One binary that knows every X++ class, table, form, and EDT in your D365FO codebase**

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET](https://img.shields.io/badge/.NET-10-purple.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey.svg)]()
[![Tests](https://img.shields.io/badge/tests-310%2B-brightgreen.svg)]()
[![Successor to d365fo-mcp-server](https://img.shields.io/badge/successor%20to-d365fo--mcp--server-orange.svg)](https://github.com/dynamics365ninja/d365fo-mcp-server)

*Grounded AI development for Dynamics 365 Finance & Operations — works with GitHub Copilot, Claude Code, Codex, Gemini CLI, and any agent with a shell*

</div>

---

## Why

AI assistants excel at C#, Python, and JavaScript. X++ is different: your D365FO codebase is private, deeply customized, and invisible to every model — so AI confidently generates code that doesn't compile.

This CLI pre-indexes your entire D365FO installation (hundreds of thousands of symbols across standard, ISV, and custom models) into a local SQLite index and exposes it as one `d365fo` binary. Every signature, every CoC wrapper, every label, every form pattern — verified against your real metadata **before** the AI writes a single line. And because it is a shell command instead of an MCP tool list, it costs ~100 tokens per turn instead of ~1,800.

![Solution Architecture](docs/img/solution-architecture-diagram.svg)

| Task | Without `d365fo` | With `d365fo` |
|------|------------------|---------------|
| Method signatures | Guessed → compile errors | `d365fo get class` — exact, from your codebase |
| Existing CoC wrappers | Manual AOT search | `d365fo find coc Class::method` in < 50 ms |
| New forms | Hand-written XML, broken patterns | Pattern-validated scaffolds; structural violations **block the write** |
| Labels | Hardcoded strings | Right `@SYS`/`@MODULE` key found instantly |
| Security chains | Hours of manual tracing | Role → Duty → Privilege → Entry Point in one call |
| Generated code | Hallucinated fields and types | Every reference proven against the index, gated before write |
| Agent context cost | 24–26 MCP tool schemas every turn | 1 shell tool + lazy-loaded Skills (~85 % fewer tokens) |

---

## Capabilities

| Feature | Description |
|---|---|
| 🔍 **Full-codebase intelligence** | Tables, classes, EDTs, enums, forms, queries, views, entities, reports, services, workflows, security artifacts, labels (FTS5) — results in milliseconds, no VM round-trip |
| 🛡️ **Grounded generation** | Fail-closed gates: `prepare change`/`prepare create` issue grounding tokens, `validate references` proves every identifier, `validate xpp` enforces BP rules — hallucinated code never reaches disk |
| 🧩 **Form pattern engine** | Catalog of Microsoft form patterns and container sub-patterns: `get form-pattern` serves the required structure, `generate form` self-tests against it (FP001–FP010), `validate form-pattern` re-checks any hand edit |
| ✍️ **Pattern-correct scaffolding** | 26 `generate` commands — tables, classes, CoC, forms (9 patterns), form datasource/control override methods, entities, security, SysOperation, workflows, business events, number sequences, XDS policies |
| 🏗️ **SDLC integration** | MSBuild compilation with structured `xppcDiagnostics`, DB sync, xppbp best practices, SysTestRunner — on Windows D365FO VMs |
| 📐 **X++ knowledge base** | 19 lazy-loaded Skills: select grammar, CoC authoring, FormRun lifecycle, BP rule canon — loaded only when relevant, for Copilot and Claude alike |
| ⚡ **Agent-first ergonomics** | Stable `{ ok, data, warnings }` JSON envelope, `search batch` / `get batch` / `prepare` single-round aggregators, `agent-prompt` + `schema` manifests |
| 🔌 **Daemon & MCP adapter** | Warm-cache named-pipe daemon with file-system watcher; `d365fo-mcp` speaks JSON-RPC 2.0 over the same index for MCP-only hosts, over stdio or `--http` for a shared team deployment (`API_KEY`, `MCP_SERVER_MODE`) |

### Pattern-grounded form development

Forms are the hardest artifact to generate correctly — each pattern dictates required containers, ordering, and allowed sub-patterns. The form pattern engine makes it a guided pipeline:

```mermaid
flowchart LR
    A["find form-patterns<br/>(what do peers use?)"] --> B["get form-pattern<br/>structure + reference forms"]
    B --> C["generate form<br/>--pattern &lt;P&gt;"]
    C --> D["pattern self-test<br/>FP001–FP010"]
    D -->|clean| E["write + JSON summary"]
    D -->|errors| B
    F["hand-edited form XML"] --> G["validate form-pattern<br/>exit 2 on errors"]
```

Structural violations (wrong order, missing container, disallowed control, misapplied sub-pattern) **block the write** while `D365FO_FORM_PATTERN_ENFORCE=true` (the default) — recommendations only warn.

---

## Quick Start

### Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (pinned in `global.json`)
- Access to a D365 F&O `PackagesLocalDirectory` (local clone, Azure Files share, or Windows VM path)

### Install

One line in PowerShell checks for the .NET SDK, clones the repo, publishes a self-contained `d365fo` binary onto `PATH`, and hands off to `d365fo init` — an interactive wizard that detects your `PackagesLocalDirectory` and asks the rest. Safe to re-run — an existing checkout is updated in place.

```powershell
irm https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main/install.ps1 | iex
```

macOS / Linux (read · search · scaffold only — `build`/`sync`/`test`/`bp` need a Windows D365FO VM):

```sh
curl -fsSL https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main/install.sh | bash
```

<details>
<summary>Prefer to run the steps yourself</summary>

```sh
git clone https://github.com/dynamics365ninja/d365fo-cli.git
cd d365fo-cli
dotnet build d365fo-cli.slnx -c Release
d365fo init --persist-profile
```

**PowerShell alias (fastest for dev, no publish step):**

```powershell
function d365fo { dotnet run --project C:\path\to\d365fo-cli\src\D365FO.Cli -- @args }
```

**Self-contained binary (what the installer does):**

```sh
dotnet publish src/D365FO.Cli -c Release -r win-x64 --self-contained
# also: linux-x64, osx-x64, osx-arm64
```

</details>

### First run

The installer above already ran `d365fo init` for you. What's left is populating the index:

```sh
# Point at your packages folder (skip if 'd365fo init' found it already)
$env:D365FO_PACKAGES_PATH = "K:\AosService\PackagesLocalDirectory"

# Build + populate the index
d365fo index build
d365fo index extract

# Verify
d365fo doctor --output json
d365fo index status --output json

# Search
d365fo search table Cust --output json
d365fo get table CustTable --output json
d365fo get batch table:CustTable class:CustTableType edt:CustAccount --output json
d365fo find coc SalesTable::insert --output json
d365fo labels resolve @SYS12345 --lang en-us,cs
```

Ready to scaffold your first table, form, and CoC extension? Full walkthrough with every command: **[docs/SETUP.md — Step 6](docs/SETUP.md#step-6--scaffold-your-first-object)**.

---

## AI Agent Integration

### GitHub Copilot (VS Code / Visual Studio)

The preferred method is the **one-command skill installer** — it deploys the bundled `d365fo-cli` Copilot skill (SKILL.md + 19 lazily-loaded topic references) into your X++ project's `.github/skills/d365fo-cli/` folder. Copilot auto-discovers skills in `.github/skills/` with no extra configuration.

```powershell
# From the d365fo-cli repo's scripts folder:
.\Install-D365FoCopilotSkills.ps1 -XppRepo "K:\D365FO\MyProject"
```

The installer:
1. Regenerates `skills/d365fo-cli/references/` if needed, using whichever host is available (`pwsh`, Windows PowerShell, or `python`).
2. Copies `skills/d365fo-cli/SKILL.md` and all `references/*.md` to `<XppRepo>/.github/skills/d365fo-cli/`.
3. Removes reference files in the target that no longer exist upstream, so retired topics don't linger.
4. Prints a migration note if the legacy `copilot-instructions.md` / `instructions/` files still exist.

**Skill layout installed into your X++ repo:**

```
.github/
└── skills/
    └── d365fo-cli/
        ├── SKILL.md              # core rule canon + tool mapping (loaded when the skill activates)
        └── references/            # 19 X++ topic files, loaded per topic on demand
            ├── coc-extension-authoring.md
            ├── xpp-database-queries.md
            ├── x++-class-authoring.md
            ├── xpp-class-and-method-rules.md
            ├── xpp-statement-and-type-rules.md
            ├── xpp-best-practice-rules.md
            ├── form-pattern-scaffolding.md
            ├── table-scaffolding.md
            ├── data-entity-scaffolding.md
            ├── event-handler-authoring.md
            ├── object-extension-authoring.md
            ├── security-hierarchy-trace.md
            ├── sysoperation-batch-patterns.md
            ├── business-events-authoring.md
            ├── custom-service-authoring.md
            ├── integration-patterns.md
            ├── label-translation.md
            ├── model-dependency-and-coupling.md
            └── review-and-checkpoint-workflow.md
```

**Legacy path (pre-skill format):** If you previously used `copilot-instructions.md` + `instructions/*.instructions.md`, you can migrate by running the installer and then removing the old files:

```powershell
Remove-Item "<XppRepo>\.github\copilot-instructions.md" -ErrorAction SilentlyContinue
Remove-Item "<XppRepo>\.github\instructions" -Recurse -ErrorAction SilentlyContinue
```

The legacy `skills/copilot/*.instructions.md` output is still emitted by `emit-skills.ps1` / `emit-skills.py` for environments that cannot use the `.github/skills/` format (e.g. GitHub Copilot versions that predate skill auto-discovery).

### Claude Code / Claude Desktop

```sh
python3 scripts/emit-skills.py                                   # emit Anthropic SKILL.md files
cp -r skills/anthropic/ /your-repo/.claude/skills/
```

### Codex CLI / Gemini CLI / Cursor

Reference the `SKILL.md` files from `skills/anthropic/` in your session prompt or `AGENTS.md`.

### MCP (Claude Desktop, Continue, VS Code MCP)

The bundled `d365fo-mcp` adapter speaks JSON-RPC 2.0 over the same index. Its tool surface is **consolidated** into 24 discriminator-based tools (e.g. `search`, `get_object_info`, `get_method`, `labels`, `security_info`, `extension_info`, `object_patterns`, `generate_object`, `modify_method`, `analyze`, `models`) — see [docs/MIGRATION_FROM_MCP.md](docs/MIGRATION_FROM_MCP.md):

```json
{
  "mcpServers": {
    "d365fo": {
      "command": "d365fo-mcp",
      "args": [],
      "env": { "D365FO_PACKAGES_PATH": "K:\\AosService\\PackagesLocalDirectory" }
    }
  }
}
```

Sharing one instance across a team instead of a local stdio process per developer? Run `d365fo-mcp --http` — see [docs/MIGRATION_FROM_MCP.md](docs/MIGRATION_FROM_MCP.md#http-transport--shared-deployment-azure-app-service) for the `API_KEY` / `MCP_SERVER_MODE` (`read-only` shared instance + `write-only` local companion) deployment pattern.

### Verify

Open the AI chat and ask:

```
What tables contain "CustAccount" field?
```

A `d365fo search` shell call returning results from your codebase = you're connected.

---

## Why CLI instead of MCP?

MCP servers inject every tool definition into the model's context on every single turn. Tool consolidation trimmed that surface — the upstream MCP server went from ~61 per-type tools (≈3,500 tok/turn) to 26 discriminator-based tools, and this repo's adapter to 24 — but it is still ~1,800 tokens per turn versus one shell tool.

| | MCP server | CLI + Skills |
|---|---|---|
| Tool definitions per turn | 24–26 tools (~1,800 tokens) | 1 shell tool (~100 tokens) |
| Discovery round-trips | 2–3 per task | often 1 (`d365fo prepare change`) |
| Scriptable (shell, CI/CD) | No | Yes |
| Works in any AI harness | No — MCP hosts only | Yes — Copilot, Claude, Codex, Gemini, … |
| Token cost over 15-turn workflow | baseline | **~85 % reduction** |

See [docs/TOKEN_ECONOMICS.md](docs/TOKEN_ECONOMICS.md) for the full analysis and the cases where MCP still wins. Migrating from `d365fo-mcp-server`? Start with **[docs/MIGRATION_FROM_MCP.md](docs/MIGRATION_FROM_MCP.md)**.

---

## Commands at a Glance

| Group | Commands |
|---|---|
| **Prepare** | `prepare change`, `prepare create` — single-round context aggregators returning a grounding token |
| **Validate** | `validate name`, `validate xpp` (offline BP rules), `validate references` (anti-hallucination gate) |
| **Index** | `index build`, `index extract`, `index refresh`, `index status` (incl. `stale-index` detection), `index export`, `index import`, `index optimize`, `index history` |
| **Discover** | `search any`, `search batch`, `search class\|table\|edt\|enum\|form\|query\|view\|entity\|report\|service\|workflow\|label\|business-event\|security-policy\|configuration-key\|tile\|workspace` |
| **Get** | `get object`, `get batch` (up to 10 objects per call), `get table\|class\|edt\|enum\|form\|menu-item\|query\|view\|entity\|report\|service\|business-event` |
| **Security** | `security role\|duty\|privilege` (artifacts), `security coverage` (Role → Duty → Privilege reach) — mirrors the MCP `security_info` tool |
| **Form patterns** | `form-pattern analyze` (advisor), `form-pattern spec` (catalog), `form-pattern validate` (FP001–FP010) — mirrors the MCP `object_patterns` tool (`domain=form`) |
| **Find** | `find related`, `find coc`, `find relations`, `find usages`, `find extensions`, `find event-handlers`, `find references`, `find form-patterns`, `find batch-jobs` (the extensibility ones mirror the MCP `extension_info` tool) |
| **Read** | `read class`, `read table`, `read form` (= MCP `get_method`) |
| **Generate** | `generate table\|class\|coc\|form\|datasource-method\|control-method\|simple-list\|entity\|extension\|event-handler\|privilege\|duty\|role\|report\|sysoperation\|number-sequence\|workflow\|menu-item\|edt\|enum\|query\|view\|map\|business-event\|custom-service\|migration-script\|runbase\|security-policy\|systest` |
| **Labels** | `labels search\|resolve\|info\|create\|rename\|delete` — search/resolve plus in-place `*.label.txt` edits, multi-language via `--lang` (mirrors the MCP `labels` tool) |
| **Journal / undo** | `undo [--steps N] [--dry-run]`, `journal list`, `delete` (kind/name, bridge or on-disk) — deterministic single-command rollback for every write path (mirrors the MCP `undo_last_modification` tool) |
| **Analyze** | `analyze completeness`, `analyze integration`, `analyze impact`, `lint`, `suggest edt`, `suggest extension`, `report-integrations` |
| **Review** | `review diff` |
| **Models** | `models list`, `models deps`, `models coupling` |
| **Agent** | `agent-prompt`, `schema` |
| **Daemon** | `daemon start\|status\|stop\|warmup` |
| **Ops (Windows VM)** | `build`, `sync`, `test run`, `bp check` |

One worked example per command: **[docs/EXAMPLES.md](docs/EXAMPLES.md)**

### When to use built-in editor tools vs. `d365fo`

**One-line rule:** if the file ends in `.xml` and is an AOT object → always `d365fo`. Everything else (config, scripts, docs) → standard editor tools.

> ⛔ **When `d365fo` returns `ok: false`** — report the error to the user and stop. Metadata read from open XML files does **not** substitute for the CLI. Never fall back to PowerShell / Python scripts to write AOT XML.

The full scenario-by-scenario decision table lives in **[docs/CAPABILITIES.md](docs/CAPABILITIES.md)**.

---

## Documentation

| Getting started | Reference | Operations |
|-----------------|-----------|------------|
| [Setup](docs/SETUP.md) — install, configure, verify | [Examples](docs/EXAMPLES.md) — one per command | [Troubleshooting](docs/TROUBLESHOOTING.md) |
| [Migration from MCP](docs/MIGRATION_FROM_MCP.md) | [Architecture](docs/ARCHITECTURE.md) — index schema, AOT coverage, lint rules, daemon | [Token economics](docs/TOKEN_ECONOMICS.md) |
| [Capabilities](docs/CAPABILITIES.md) — tool decision table | [Configuration](docs/CONFIGURATION.md) — env vars and profiles | |

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| `PACKAGES_PATH_NOT_FOUND` | Set `D365FO_PACKAGES_PATH` or pass `--packages <PATH>` |
| `UNSUPPORTED_PLATFORM` | `build` / `sync` / `test` / `bp` require Windows + a D365FO dev VM |
| `NO_INDEX` | Run `d365fo index build` then `d365fo index extract` |
| `FORM_PATTERN_VIOLATION` | The generated/edited form breaks its pattern — `d365fo form-pattern spec <P>` shows the required structure |
| Index appears stale after editing XML | Run `d365fo index refresh --model <Model>` |
| Index file locked | Stop any running `d365fo daemon` or `d365fo-mcp` process; WAL sidecar files (`-wal`, `-shm`) are normal |

More in [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) and [docs/SETUP.md](docs/SETUP.md#troubleshooting).

---

## License

MIT. The sibling [`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) is also MIT.

## Disclaimer

This project is an independent research effort and is not affiliated with, endorsed by, or associated with Microsoft or any other organization. It is provided as-is for educational and development purposes.
