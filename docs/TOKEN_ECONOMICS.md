# Token Economics — CLI + Skills vs. MCP

> **Audience:** anyone deciding between CLI+Skills and MCP for their AI workflow, or evaluating the per-turn cost of AI tooling.
> **TL;DR:** CLI + Skills saves **~80–92% of tool-plumbing tokens** compared to MCP over a typical 5–20 turn workflow. MCP still wins in two niches — see [When the saving does *not* apply](#when-the-saving-does-not-apply).

## Contents

1. [The structural cost of MCP](#the-structural-cost-of-mcp)
2. [The structural shape of CLI + Skills](#the-structural-shape-of-cli--skills)
3. [Expected savings](#expected-savings)
4. [When the saving does *not* apply](#when-the-saving-does-not-apply)
5. [Benchmark recipe](#benchmark-recipe)
6. [Sources](#sources)

---

## The structural cost of MCP

An MCP client loads every connected server's tool list into the model's context **on every request**. The upstream `d365fo-mcp-server` exposes **54 tools**. Using the token-accounting methodology published in [seangalliher/D365-erp-cli](https://github.com/seangalliher/D365-erp-cli#token-savings-over-a-typical-workflow) (own measurement against Sonnet 4.5, October 2025):

- Average MCP tool schema ≈ **54 tokens**.
- 54 tools × 54 tokens ≈ **~2,900 tokens/turn of overhead**.
- Over a 20-turn workflow: ~58,000 tokens burned before any useful work.

There's also a hidden cost: MCP encourages multi-step discovery (`find_type` → `get_metadata` → `call`) — each round-trip adds conversation history that compounds.

## The structural shape of CLI + Skills

- The agent sees **one** tool: the shell / `bash` tool (~100 tokens).
- At session start the harness enumerates available skills and loads **only** their YAML frontmatter (`name`, `description`, `applies_when`). Each skill costs ~30–60 tokens until it is actually triggered.
- When a skill fires, its body (Markdown instructions + CLI invocations) is loaded on demand.
- Agent discovery happens through `d365fo --help` and `d365fo schema --full` — again, on demand.

## Expected savings

Using the same per-turn accounting:

| Turns | MCP overhead | CLI + Skills overhead | Saving |
|---:|---:|---:|---:|
| 5  | ~14,500 | ~2,800 | ~81% |
| 10 | ~29,000 | ~3,500 | ~88% |
| 15 | ~44,000 | ~4,000 | ~91% |
| 20 | ~58,000 | ~4,500 | ~92% |

Real workflows save more, because MCP also pays for extra discovery round-trips (often 5–15 kT per workflow) that the CLI eliminates via `d365fo get table <Name>` in a single call.

## When the saving does *not* apply

### 1. AI host without a shell tool

Plain Claude.ai chat or plain ChatGPT Web (no Code Interpreter) cannot run CLI commands — Skills + CLI need a filesystem and a shell. In those hosts **MCP remains the only option**, which is why this project keeps `D365FO.Mcp` alive as a thin adapter over the same `D365FO.Core`.

### 2. One-off lookups

A single `get_table` call per session. Overhead of one CLI process start (~50–150 ms cold) may outweigh the saved tokens. MCP's warm connection shines here; CLI shines on multi-turn flows.

### 3. Streaming back large generated XML

If the agent demands the full scaffolded file back into context, token economy collapses. That is why `d365fo generate *` writes to `--out` and returns only a JSON summary on stdout. Skills instruct the agent to honour this.

### 4. Write operations and runtime-resolved unit reads

These need D365FO's own `IMetadataProvider` to produce XML that Visual Studio / MSBuild accept and to reflect ISV overlays. They route through the **Metadata Bridge** ([`D365FO.Bridge`](ARCHITECTURE.md#metadata-bridge-live-d365fo-reads-and-writes)). Until those paths are fully instrumented, the token-saving numbers above apply to scan-style tools only (search, find, label / CoC analysis, security hierarchy) — not to `generate` / `modify` flows.

## Benchmark recipe

`scripts/measure-tokens.ps1` (follow-up):

1. Spin a fixture SQLite DB with representative seed data.
2. Run identical 10/15/20-turn scripted prompts against:
   - Copilot + `d365fo-mcp-server` (legacy TypeScript implementation).
   - Copilot + `d365fo` CLI + Skills.
3. Parse `tokenUsage` from the host's JSONL conversation log.
4. Emit CSV + HTML report.

**Release gate:** ≥ 80% overhead reduction at 15 turns. This threshold is published so every PR that materially adds schema surface or skill text can re-run the benchmark and prove non-regression.

## Sources

- Anthropic — [Equipping agents for the real world with Agent Skills](https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills), October 2025.
- Simon Willison — [Claude Skills are awesome, maybe a bigger deal than MCP](https://simonwillison.net/2025/Oct/16/claude-skills/), October 2025.
- seangalliher — [D365-erp-cli, "Why CLI over MCP?"](https://github.com/seangalliher/D365-erp-cli#why-cli-over-mcp).
- dynamics365ninja — [d365fo-mcp-server, 54-tool surface](https://github.com/dynamics365ninja/d365fo-mcp-server/blob/main/docs/MCP_TOOLS.md).

---

## See also

- [USAGE.md](USAGE.md) — how to wire Skills and the CLI into each AI agent.
- [ARCHITECTURE.md](ARCHITECTURE.md) — the Metadata Bridge (case 4 above).
- [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md) — decision tree for MCP users.
