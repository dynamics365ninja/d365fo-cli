# Token Economics — CLI + Skills vs MCP

This document records the methodology and expected numbers for why this project
replaces a 54-tool MCP server with a CLI + Skills layer. All numbers are
reproducible with `scripts/measure-tokens.ps1` (wired in follow-up commits).

## The structural cost of MCP

An MCP client loads every connected server's tool list into the model context
on every request. The upstream `d365fo-mcp-server` exposes **54 tools**. Using
the token accounting methodology published in
[seangalliher/D365-erp-cli](https://github.com/seangalliher/D365-erp-cli#token-savings-over-a-typical-workflow)
(own measurement against Sonnet 4.5, October 2025):

- Average MCP tool schema ≈ **54 tokens**.
- 54 tools × 54 tokens ≈ **~2 900 tokens/turn of overhead**.
- Over a 20-turn workflow: ~58 000 tokens burned before any useful work.

Plus hidden cost: MCP encourages multi-step discovery (`find_type` →
`get_metadata` → `call`) — each round-trip adds conversation history that
compounds.

## The structural shape of CLI + Skills

- The agent sees **one** tool: `bash`/terminal (~100 tokens).
- At session start the harness enumerates available skills and loads **only**
  the YAML frontmatter (`name`, `description`, `applies_when`). Each skill
  costs ~30–60 tokens until it is actually triggered.
- When a skill fires, its body (Markdown instructions + CLI invocations) is
  loaded on demand. Agent discovery happens through `d365fo --help` and
  `d365fo schema --full` — again, on demand.

## Expected savings

Using the same per-turn accounting:

| Turns | MCP overhead | CLI+Skills overhead | Saving |
|---:|---:|---:|---:|
| 5  | ~14 500  | ~2 800 | ~81 % |
| 10 | ~29 000  | ~3 500 | ~88 % |
| 15 | ~44 000  | ~4 000 | ~91 % |
| 20 | ~58 000  | ~4 500 | ~92 % |

Real workflows save more, because MCP also pays for extra discovery round-trips
(often 5–15 kT per workflow) that CLI eliminates via `get table <Name>` in a
single call.

## When does the saving **not** apply?

1. **Harness without a shell tool** (plain Claude.ai chat, plain ChatGPT Web
   without Code Interpreter). Skills + CLI need a filesystem and a shell. In
   those hosts, MCP remains the only option — which is why this project keeps
   `D365FO.Mcp` alive as a thin adapter over the same `D365FO.Core`.

2. **One-off lookups** (single `get_table` call per session). Overhead of one
   CLI process start (~50–150 ms cold) may outweigh the saved tokens. MCP
   warm-connection shines here; CLI shines on multi-turn flows.

3. **Streaming back large generated XML**. If the agent demands the full
   scaffolded file back into context, token economy collapses. That is why
   `d365fo generate *` writes to `--out` and returns only a JSON summary on
   stdout. Skills instruct the agent to honour this.

4. **Write operations and runtime-resolved unit reads.** These need D365FO's
   own `IMetadataProvider` to produce XML that Visual Studio / MSBuild accept
   and to return data that reflects ISV overlays. The upstream
   `d365fo-mcp-server` routes those through a `D365MetadataBridge.exe`
   (.NET 4.8) child process; doing the same from this CLI is tracked as item
   **1.0** in [ROADMAP.md](ROADMAP.md). Until that lands the token-saving
   comparison applies to scan-style tools only (search, find, label/CoC
   analysis, security hierarchy), not to `generate`/`modify` paths.

## Benchmark recipe

`scripts/measure-tokens.ps1` (follow-up) will:

1. Spin a fixture SQLite DB with representative seed data.
2. Run identical 10/15/20-turn scripted prompts against:
   - Copilot + `d365fo-mcp-server` (legacy TS implementation).
   - Copilot + `d365fo` CLI + skills.
3. Parse `tokenUsage` from the host's JSONL conversation log.
4. Emit a CSV and an HTML report.

Release gate: **≥ 80 % overhead reduction at 15 turns**. This threshold is
published so every PR that materially adds schema surface or skill text can
re-run the benchmark and prove non-regression.

## Sources

- Anthropic — [Equipping agents for the real world with Agent Skills](https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills), October 2025.
- Simon Willison — [Claude Skills are awesome, maybe a bigger deal than MCP](https://simonwillison.net/2025/Oct/16/claude-skills/), October 2025.
- seangalliher — [D365-erp-cli, "Why CLI over MCP?"](https://github.com/seangalliher/D365-erp-cli#why-cli-over-mcp).
- dynamics365ninja — [d365fo-mcp-server, 54-tool surface](https://github.com/dynamics365ninja/d365fo-mcp-server/blob/main/docs/MCP_TOOLS.md).
