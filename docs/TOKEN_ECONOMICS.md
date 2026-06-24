# Token Economics

> **TL;DR** — CLI + Skills costs **~100 tokens per turn**. The equivalent MCP server costs **~1,800 tokens per turn** (down from ~3,500 before the upstream tool consolidation — see below). Over a real 15-turn task that's still ~85 % off the agent's context bill, before any savings from collapsing multi-step discovery flows.

---

## Where the tokens go

```mermaid
flowchart LR
    subgraph MCP["MCP server (~1,800 tok / turn)"]
        direction TB
        M1["20–26 tool schemas<br/>injected every turn<br/>~1,400–1,800 tok"]
        M2["Multi-step discovery<br/>find_type → get_metadata → call<br/>extra round-trips"]
    end
    subgraph CLI["d365fo + Skills (~100 tok / turn)"]
        direction TB
        C1["1 shell tool description<br/>~100 tok"]
        C2["Skills frontmatter only<br/>~30–60 tok each, lazy"]
        C3["One-call aggregators<br/>prepare · search batch · get batch"]
    end
    MCP -.->|same backing index| CLI
```

Every MCP request loads all tool schemas into the model context — there is no "load on demand". The CLI exposes one shell tool; the agent discovers commands via `d365fo schema` only when it needs them. Skills add ~30–60 tokens of frontmatter per `.instructions.md` file; the full skill body is only paged in when the agent decides it is relevant.

> **Tool consolidation cut the MCP baseline too.** The upstream `d365fo-mcp-server`
> collapsed its old per-type surface (~61 tools, ~2,900 tok of schemas) into 26
> discriminator-based tools; this repo's `d365fo-mcp` adapter exposes **20**. The
> schemas are individually richer (enum discriminators, longer descriptions), so
> the per-turn overhead landed around **~1,800 tok** rather than scaling linearly
> down. The CLI's one-shell-tool cost (~100 tok) is unchanged — so the relative
> advantage holds even against the leaner MCP surface.

---

## Expected savings

| Turns | MCP overhead | CLI + Skills overhead | Saving |
|---:|---:|---:|---:|
|  5 |  ~9,000 |  ~2,800 | **~69 %** |
| 10 | ~18,000 |  ~3,500 | **~81 %** |
| 15 | ~27,000 |  ~4,000 | **~85 %** |
| 20 | ~36,000 |  ~4,500 | **~88 %** |

Figures use the post-consolidation MCP baseline (~1,800 tok/turn). Real workflows save more — MCP also pays for discovery round-trips (often 5–15 kT per workflow) that the CLI eliminates with single-call aggregators like `d365fo prepare change`, `d365fo get batch table:CustTable class:CustTableType edt:CustAccount`, or `d365fo search batch <q1> <q2> <q3>`.

---

## When CLI does **not** save

| Situation | Recommended |
|---|---|
| AI host without a shell tool (Claude.ai web, ChatGPT web) | MCP — the bundled `d365fo-mcp` adapter speaks JSON-RPC over the same index |
| Single one-off lookup per session | Either — an MCP warm connection has no per-turn startup cost |
| Agent needs the full generated XML back in context | Avoid both — `d365fo generate` always writes to `--out` and returns a JSON summary only |

> **Running CLI + MCP side by side?** Keeping MCP registered pays the full ~1,800 tok/turn schema overhead on *every* turn, even ones that only call the CLI — the per-call savings don't offset a per-turn cost. Side by side is worth it only for mixed shell-tool environments or a migration grace period; for a single agent with a working shell tool, prefer CLI only. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md#when-to-run-both-side-by-side).

---

## Sources

- Anthropic — [Equipping agents for the real world with Agent Skills](https://www.anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills), October 2025.
- Simon Willison — [Claude Skills are awesome, maybe a bigger deal than MCP](https://simonwillison.net/2025/Oct/16/claude-skills/), October 2025.
- seangalliher — [D365-erp-cli, "Why CLI over MCP?"](https://github.com/seangalliher/D365-erp-cli#why-cli-over-mcp).

---

## See also

- [EXAMPLES.md](EXAMPLES.md#agent-integration) — wiring Skills and the CLI into each AI agent.
- [ARCHITECTURE.md](ARCHITECTURE.md) — the Metadata Bridge and where `d365fo-mcp` plugs in.
- [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md) — decision tree for MCP users.
