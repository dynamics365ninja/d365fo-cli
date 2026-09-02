#!/usr/bin/env python3
"""Measure what each integration puts into an agent's context, in characters.

docs/TOKEN_ECONOMICS.md used to carry estimates. This measures the same
quantities off the running code, so the document can state what was measured and
anyone can re-run it:

  MCP     the `tools/list` payload a host injects, plus the server instructions
  CLI     the skill frontmatter a host lists, and the command manifest an agent
          pulls when it wants one

Characters, not tokens: the exact token count depends on the model's tokenizer,
which is not something this repository can measure honestly. The conversion used
in the document is stated there with its divisor, and the character counts below
are what it is derived from.

Usage:
  python scripts/measure-context-cost.py            human-readable table
  python scripts/measure-context-cost.py --json     the same numbers as JSON
"""
from __future__ import annotations

import json
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent

INIT = {
    "jsonrpc": "2.0",
    "id": 1,
    "method": "initialize",
    "params": {
        "protocolVersion": "2024-11-05",
        "capabilities": {},
        "clientInfo": {"name": "measure-context-cost", "version": "1"},
    },
}
LIST = {"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}}


def invocation(project: str, exe: str) -> list[str]:
    for configuration in ("Release", "Debug"):
        for name in (f"{exe}.exe", exe):
            candidate = ROOT / "src" / project / "bin" / configuration / "net10.0" / name
            if candidate.exists():
                return [str(candidate)]
    return ["dotnet", "run", "--project", f"src/{project}", "-c", "Release", "--"]


def run(cmd: list[str], stdin: str | None = None) -> str:
    proc = subprocess.run(
        cmd, input=stdin, capture_output=True, text=True, encoding="utf-8", cwd=ROOT
    )
    if not proc.stdout.strip():
        sys.exit(f"{' '.join(cmd)} produced nothing ({proc.returncode}):\n{proc.stderr[:2000]}")
    return proc.stdout


def minified(value: object) -> int:
    return len(json.dumps(value, separators=(",", ":"), ensure_ascii=False))


def mcp() -> dict:
    stdin = "\n".join(json.dumps(m) for m in (INIT, LIST)) + "\n"
    raw = run(invocation("D365FO.Mcp", "d365fo-mcp") + ["--legacy"], stdin)
    messages = [json.loads(line) for line in raw.splitlines() if line.strip()]
    handshake = next(m for m in messages if m.get("id") == 1)["result"]
    tools = next(m for m in messages if m.get("id") == 2)["result"]["tools"]

    per_tool = sorted(((minified(t), t["name"]) for t in tools), reverse=True)
    return {
        "tools": len(tools),
        "toolSchemaChars": minified(tools),
        "instructionsChars": len(handshake.get("instructions", "")),
        "largestTool": {"name": per_tool[0][1], "chars": per_tool[0][0]},
        "smallestTool": {"name": per_tool[-1][1], "chars": per_tool[-1][0]},
        "medianToolChars": per_tool[len(per_tool) // 2][0],
    }


def cli() -> dict:
    compact = json.loads(run(invocation("D365FO.Cli", "d365fo") + ["schema"]))["data"]
    full = json.loads(run(invocation("D365FO.Cli", "d365fo") + ["schema", "--full"]))["data"]

    # What a host lists standing, per skill layout. Only name + description are
    # standing cost; the body is paged in when the skill activates.
    def frontmatter_cost(paths: list[Path]) -> tuple[int, int]:
        total = 0
        for path in paths:
            text = path.read_text(encoding="utf-8")
            match = re.match(r"---\n(.*?)\n---", text, re.S)
            if not match:
                continue
            kept = [
                line for line in match.group(1).split("\n")
                if line.startswith(("name:", "description:"))
            ]
            total += len("\n".join(kept))
        return len(paths), total

    anthropic_count, anthropic_chars = frontmatter_cost(
        sorted((ROOT / "skills" / "anthropic").glob("*/SKILL.md"))
    )
    single = ROOT / "skills" / "d365fo-cli" / "SKILL.md"
    _, single_chars = frontmatter_cost([single])
    references = sorted((ROOT / "skills" / "d365fo-cli" / "references").glob("*.md"))
    reference_sizes = [len(p.read_text(encoding="utf-8")) for p in references]

    return {
        "schemaCompact": {"commands": len(compact["commands"]), "chars": minified(compact)},
        "schemaFull": {"commands": len(full["commands"]), "chars": minified(full)},
        "skillsAnthropic": {"skills": anthropic_count, "standingChars": anthropic_chars},
        "skillSingle": {
            "standingChars": single_chars,
            "bodyChars": len(single.read_text(encoding="utf-8")),
            "references": len(references),
            "referenceChars": sum(reference_sizes),
            "medianReferenceChars": sorted(reference_sizes)[len(reference_sizes) // 2]
            if reference_sizes else 0,
        },
    }


def main() -> None:
    measured = {"mcp": mcp(), "cli": cli()}

    if "--json" in sys.argv:
        print(json.dumps(measured, indent=2))
        return

    m, c = measured["mcp"], measured["cli"]
    print("MCP adapter (per turn, injected by the host)")
    print(f"  tools                        {m['tools']}")
    print(f"  tool schemas (minified)      {m['toolSchemaChars']:>8,} chars")
    print(f"  server instructions          {m['instructionsChars']:>8,} chars  (once per session)")
    print(f"  largest / median / smallest  {m['largestTool']['chars']:,} ({m['largestTool']['name']})"
          f" / {m['medianToolChars']:,} / {m['smallestTool']['chars']:,} ({m['smallestTool']['name']})")
    print()
    print("CLI (standing cost, per turn)")
    print(f"  one-skill layout             {c['skillSingle']['standingChars']:>8,} chars"
          f"  ({c['skillSingle']['references']} topics, paged in on demand)")
    print(f"  per-topic skill layout       {c['skillsAnthropic']['standingChars']:>8,} chars"
          f"  ({c['skillsAnthropic']['skills']} skills listed)")
    print()
    print("CLI (pulled only when the agent asks)")
    print(f"  d365fo schema                {c['schemaCompact']['chars']:>8,} chars"
          f"  ({c['schemaCompact']['commands']} commands)")
    print(f"  d365fo schema --full         {c['schemaFull']['chars']:>8,} chars"
          f"  ({c['schemaFull']['commands']} commands)")
    print(f"  one reference topic (median) {c['skillSingle']['medianReferenceChars']:>8,} chars")


if __name__ == "__main__":
    main()
