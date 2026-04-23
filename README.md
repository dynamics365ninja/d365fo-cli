# d365fo-cli

> Structured CLI + Agent Skills for Dynamics 365 Finance & Operations.
> Successor to [`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) — same metadata index, **much cheaper on tokens**, scriptable in PowerShell and CI/CD, agent-agnostic.

Inspired by the CLI layout of [`seangalliher/D365-erp-cli`](https://github.com/seangalliher/D365-erp-cli) and the Agent Skills pattern introduced by Anthropic in October 2025. MCP stays available as a thin transport on top of the shared `D365FO.Core` — no drift, no migration cliff.

## Why CLI + Skills instead of MCP?

MCP servers inject every tool definition into the model's context on every turn. For this project that used to be **54 tools ≈ 2 900 tokens/turn**. CLI+Skills changes that:

| | MCP server | CLI + Skills |
|---|---|---|
| Tool definitions in context | 54 tools every turn | 1 shell tool (~100 tok/turn) |
| Skill metadata | — | short frontmatter, lazy-loaded |
| Entity/table discovery | 2–3 MCP round-trips | one `d365fo get table X` |
| Scriptable (PowerShell, CI) | no | yes |
| Works in Claude Code / Codex / Gemini / Copilot agent | MCP-supporting hosts only | any harness with a shell |

Projected overhead saving (per D365-erp-cli methodology): **~88 % at 10 turns, ~92 % at 20 turns**. Measured in this repo via `scripts/measure-tokens.ps1` (see `docs/TOKEN_ECONOMICS.md`).

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

```sh
dotnet build d365fo-cli.slnx -c Release

# Create / ensure the index
export D365FO_INDEX_DB=$HOME/.d365fo/index.sqlite
dotnet run --project src/D365FO.Cli -- index build

# Ask the index anything
dotnet run --project src/D365FO.Cli -- search class Cust
dotnet run --project src/D365FO.Cli -- get table CustTable
dotnet run --project src/D365FO.Cli -- find coc CustTable::validateWrite

# Emit a system prompt for your agent
dotnet run --project src/D365FO.Cli -- agent-prompt --out .prompts/d365fo.md

# JSON manifest of every command
dotnet run --project src/D365FO.Cli -- schema --full
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

## Roadmap

Phase 0–3 land in this repo: solution bootstrap, Core + Index, representative command set (search/get/find/index/doctor/agent-prompt/schema), dual skills generator. Follow-up phases as described in `docs/PLAN.md`:

- Phase 1 finish — XML extract pipeline to populate the index from `PACKAGES_PATH` / `D365MetadataBridge` absorption.
- Phase 2 finish — parity for all 54 upstream MCP tools (generate, build, sync, test, bp, review).
- Phase 4 finish — MCP JSON-RPC transport on top of `ToolHandlers`.
- Phase 5 — self-contained publish (`dotnet publish -r win-x64 --self-contained`), Authenticode, winget/scoop.
- Phase 6 (optional) — `d365fo daemon` for warm SQLite.

See full plan, token-economics analysis, and migration notes in [docs/](docs/).

## License

MIT. Upstream `d365fo-mcp-server` is also MIT.
