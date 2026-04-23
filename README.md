# d365fo-cli

> **CLI + Agent Skills + MCP server for Dynamics 365 Finance & Operations X++ development.**
> Lets AI assistants (GitHub Copilot, Claude, Cursor, Codex, Gemini) understand your AOT metadata, scaffold AxTable / AxClass / Chain-of-Command extensions, review X++ diffs, and drive MSBuild / SyncEngine / SysTestRunner / xppbp on a D365FO developer VM.
> Successor to [`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) — same metadata index, **much cheaper on tokens**, scriptable in PowerShell and CI/CD, agent-agnostic.

Purpose-built for X++ / AOT work: indexes `AxTable` (fields, relations, indexes, methods, delete actions), `AxClass` (methods, attributes), `AxEdt`, `AxEnum`, `AxForm` (+extensions), `AxMenuItem*`, `AxLabelFile` (multi-language), `AxQuery` / `AxQuerySimple`, `AxView`, `AxDataEntityView` (OData entity/collection names), `AxReport` / `AxReportSsrs`, `AxService` / `AxServiceGroup`, `AxWorkflowType`, `AxSecurity{Role,Duty,Privilege}` (+flattened SecurityMap), event subscribers, Chain-of-Command extensions, and per-model descriptor metadata (publisher, layer, module references) into a local SQLite cache so agents resolve types, relations, CoC targets, and labels without round-tripping a live VM.

`*FormAdaptor` companion packages are skipped during extraction (same as the upstream `d365fo-mcp-server`).

Inspired by the Agent Skills pattern introduced by Anthropic in October 2025. MCP stays available as a first-class JSON-RPC 2.0 transport (`d365fo-mcp`) on top of the shared `D365FO.Core` — no drift, no migration cliff.

## Why CLI + Skills instead of MCP?

MCP servers inject every tool definition into the model's context on every turn. For this project that used to be **54 tools ≈ 2 900 tokens/turn**. CLI+Skills changes that:

| | MCP server | CLI + Skills |
|---|---|---|
| Tool definitions in context | 54 tools every turn | 1 shell tool (~100 tok/turn) |
| Skill metadata | — | short frontmatter, lazy-loaded |
| Entity/table discovery | 2–3 MCP round-trips | one `d365fo get table X` |
| Scriptable (PowerShell, CI) | no | yes |
| Works in Claude Code / Codex / Gemini / Copilot agent | MCP-supporting hosts only | any harness with a shell |

**Still need MCP?** Keep it. `D365FO.Mcp` is a thin adapter over the same `D365FO.Core` used by the CLI — one source of truth.

## Layout

```
src/
  D365FO.Core/   Shared library (index, repository, guardrails, models)
  D365FO.Cli/    Spectre.Console.Cli hosting → `d365fo` binary
  D365FO.Mcp/    Stdio dispatcher skeleton (MCP coexistence)
skills/
  _source/       Single source of truth (Markdown + YAML frontmatter)
  copilot/       Emitted: .github/instructions/*.instructions.md style
  anthropic/     Emitted: <skill-id>/SKILL.md (Anthropic Agent Skills)
scripts/
  emit-skills.py / emit-skills.ps1   Dual generator
tests/           xUnit (Core + CLI)
```

## Quick start

> 📖 **Full setup & usage guide: [docs/USAGE.md](docs/USAGE.md)**

```sh
dotnet build d365fo-cli.slnx -c Release

# Create / ensure the index
export D365FO_INDEX_DB=$HOME/.d365fo/index.sqlite
dotnet run --project src/D365FO.Cli -- index build

# Ingest AOT metadata (optionally limit to one model)
dotnet run --project src/D365FO.Cli -- index extract [--model ApplicationSuite]

# Ask the index anything
dotnet run --project src/D365FO.Cli -- search class Cust
dotnet run --project src/D365FO.Cli -- search entity Customers       # OData entity/collection match
dotnet run --project src/D365FO.Cli -- get table CustTable            # fields + relations + indexes + methods + delete actions
dotnet run --project src/D365FO.Cli -- get role SystemAdministrator   # duties + privileges + entry points
dotnet run --project src/D365FO.Cli -- find coc CustTable::validateWrite
dotnet run --project src/D365FO.Cli -- find handlers CustTable
dotnet run --project src/D365FO.Cli -- resolve label @SYS12345 --lang cs,en-US
dotnet run --project src/D365FO.Cli -- read class CustTable_Extension --method validateWriteExt
dotnet run --project src/D365FO.Cli -- models deps ApplicationSuite

# Emit a system prompt for your agent
dotnet run --project src/D365FO.Cli -- agent-prompt --out .prompts/d365fo.md
```

Every command returns a stable envelope:

```json
{ "ok": true,  "data": { ... } }
{ "ok": false, "error": { "code": "...", "message": "...", "hint": "..." } }
```

## Skills

Source skills live in [skills/_source/](skills/_source). The generator emits two formats from one source:

```sh
python3 scripts/emit-skills.py
# or:
pwsh scripts/emit-skills.ps1
```

- **GitHub Copilot** → `skills/copilot/<id>.instructions.md` with `applyTo` glob.
- **Anthropic Claude** → `skills/anthropic/<id>/SKILL.md` with `applies_when` trigger.

To use them:

- **VS Code / Visual Studio Copilot**: copy `skills/copilot/*` into your solution's `.github/instructions/`.
- **Claude Code / Claude Desktop**: point at `skills/anthropic/`.
- **Codex CLI / Gemini CLI**: reference the relevant `SKILL.md` in your session prompt.

Seed skills ship covering: [X++ class authoring](skills/_source/x++-class-authoring.md), [table scaffolding](skills/_source/table-scaffolding.md), [CoC extension authoring](skills/_source/coc-extension-authoring.md), [security hierarchy tracing](skills/_source/security-hierarchy-trace.md), [label translation](skills/_source/label-translation.md).

See full plan, token-economics analysis, and migration notes in [docs/](docs/) — including the [roadmap of planned and deferred items](docs/ROADMAP.md).

## License

MIT. Upstream `d365fo-mcp-server` is also MIT.
