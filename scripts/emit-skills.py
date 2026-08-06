#!/usr/bin/env python3
"""Emit Copilot, Anthropic, and d365fo-cli skill resource variants from skills/_source/*.md.

Equivalent of scripts/emit-skills.ps1 for environments without PowerShell.
Single source of truth: skills/_source/<id>.md with YAML frontmatter.

Frontmatter keys:
  id:           (required) stable identifier, used as file/folder name
  description:  (required) one-line trigger description
  covers:       (required) short phrase for the SKILL.md reference table
  applyTo:      (Copilot)  list of globs
  appliesWhen:  (Anthropic) plain-text trigger description

Body markers:
  <!-- canon:<id> --> … <!-- /canon -->   a rule-canon block. The same blocks are
  read at runtime by D365FO.Core.Knowledge.RuleCanon, which composes the CLI's
  agent prompt and the MCP server instructions from them — so the rule canon has
  exactly one home. This script writes them into skills/d365fo-cli/SKILL.md, the
  one consumer that is a file on disk rather than a runtime composition; CI fails
  if that file drifts from the source topics.

Outputs:
  skills/copilot/<id>.instructions.md
  skills/anthropic/<id>/SKILL.md
  skills/d365fo-cli/references/<id>.md
  skills/d365fo-cli/SKILL.md            (generated regions only)
"""
from __future__ import annotations

import re
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "skills" / "_source"
OUT_ROOT = ROOT / "skills"
FENCE = re.compile(r"^---\s*$", re.MULTILINE)


def split_frontmatter(text: str) -> tuple[str, str]:
    if not text.startswith("---"):
        raise ValueError("missing frontmatter fence '---' at top of file")
    parts = FENCE.split(text, maxsplit=2)
    if len(parts) < 3:
        raise ValueError("malformed frontmatter")
    return parts[1].strip(), parts[2].lstrip("\r\n")


def parse_yaml(text: str) -> dict[str, object]:
    data: dict[str, object] = {}
    current_list_key: str | None = None
    for raw in text.splitlines():
        if not raw.strip():
            continue
        if current_list_key and re.match(r"^\s*-\s+", raw):
            val = raw.strip()[2:].strip().strip('"').strip("'")
            data[current_list_key].append(val)  # type: ignore[attr-defined]
            continue
        m = re.match(r"^([A-Za-z0-9_-]+)\s*:\s*(.*)$", raw)
        if not m:
            continue
        key, val = m.group(1), m.group(2).strip()
        if not val:
            data[key] = []
            current_list_key = key
        else:
            data[key] = val.strip('"').strip("'")
            current_list_key = None
    return data


def emit_copilot(meta: dict, body: str, out_dir: Path) -> Path:
    sid = meta["id"]
    apply_to = meta.get("applyTo") or []
    if isinstance(apply_to, str):
        apply_to = [apply_to]
    glob = ",".join(apply_to) if apply_to else "**/*"
    fm = f"---\ndescription: {meta['description']}\napplyTo: '{glob}'\n---\n"
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"{sid}.instructions.md"
    path.write_text(fm + body, encoding="utf-8")
    return path


def emit_anthropic(meta: dict, body: str, out_dir: Path) -> Path:
    sid = meta["id"]
    lines = ["---", f"name: {sid}", f"description: {meta['description']}"]
    if "appliesWhen" in meta:
        lines.append(f"applies_when: {meta['appliesWhen']}")
    lines.append("---\n")
    fm = "\n".join(lines)
    dir_ = out_dir / sid
    dir_.mkdir(parents=True, exist_ok=True)
    path = dir_ / "SKILL.md"
    path.write_text(fm + body, encoding="utf-8")
    return path


def emit_copilot_skill(meta: dict, body: str, out_dir: Path) -> Path:
    """Emit body-only (no frontmatter) to skills/d365fo-cli/references/<id>.md."""
    sid = meta["id"]
    out_dir.mkdir(parents=True, exist_ok=True)
    path = out_dir / f"{sid}.md"
    path.write_text(body, encoding="utf-8")
    return path


CANON_RX = re.compile(r"<!--\s*canon:([a-z0-9-]+)\s*-->\n(.*?)\n<!--\s*/canon\s*-->", re.DOTALL)

# Canon blocks written into SKILL.md, in reading order, with the heading each gets.
SKILL_CANON = [
    ("never-auto", "Never-auto"),
    ("core", "Non-negotiable X++ rules"),
    ("coc", "Chain of Command"),
    ("aot-xml-safety", "AOT XML safety"),
    ("bp", "Best practice — must pass `d365fo bp check`"),
]


def replace_region(text: str, name: str, body: str) -> str:
    """Replace the content between <!-- BEGIN <name> --> and <!-- END <name> -->."""
    begin, end = f"<!-- BEGIN {name} -->", f"<!-- END {name} -->"
    i, j = text.find(begin), text.find(end)
    if i < 0 or j < 0:
        raise SystemExit(f"SKILL.md is missing the '{name}' generated region markers")
    return text[: i + len(begin)] + "\n" + body.rstrip("\n") + "\n" + text[j:]


def emit_skill_md(topics: list[dict], canon: dict[str, str], path: Path) -> None:
    """Refresh the generated regions of the hand-written d365fo-cli SKILL.md."""
    if not path.exists():
        raise SystemExit(f"{path} not found")

    rows = ["| Resource file | Covers |", "|---|---|"]
    rows += [f"| `{t['id']}` | {t['covers']} |" for t in topics]

    blocks = []
    for canon_id, heading in SKILL_CANON:
        if canon_id not in canon:
            raise SystemExit(f"no canon block '{canon_id}' in skills/_source")
        blocks.append(f"### {heading}\n\n{canon[canon_id]}")

    text = path.read_text(encoding="utf-8")
    text = replace_region(text, "references", "\n".join(rows))
    text = replace_region(text, "canon", "\n\n".join(blocks))
    path.write_text(text, encoding="utf-8")


def main() -> int:
    copilot_out      = OUT_ROOT / "copilot"
    anthropic_out    = OUT_ROOT / "anthropic"
    copilot_skill_out = OUT_ROOT / "d365fo-cli" / "references"
    for p in (copilot_out, anthropic_out):
        if p.exists():
            shutil.rmtree(p)
    # Only remove the references dir so SKILL.md is preserved
    if copilot_skill_out.exists():
        shutil.rmtree(copilot_skill_out)

    files = sorted(SOURCE.glob("*.md"))
    if not files:
        print("no source skills found", file=sys.stderr)
        return 0

    topics: list[dict] = []
    canon: dict[str, str] = {}

    for f in files:
        print(f"» {f.name}")
        text = f.read_text(encoding="utf-8")
        fm_text, body = split_frontmatter(text)
        meta = parse_yaml(fm_text)
        for required in ("id", "description", "covers"):
            if required not in meta:
                raise SystemExit(f"{f.name}: missing '{required}'")
        emit_copilot(meta, body, copilot_out)
        emit_anthropic(meta, body, anthropic_out)
        emit_copilot_skill(meta, body, copilot_skill_out)
        topics.append(meta)

        for canon_id, block in CANON_RX.findall(body.replace("\r\n", "\n")):
            if canon_id in canon:
                raise SystemExit(f"canon id '{canon_id}' is declared by more than one topic")
            canon[canon_id] = block.strip()

    emit_skill_md(topics, canon, OUT_ROOT / "d365fo-cli" / "SKILL.md")

    print(f"\nDone. {len(files)} skill(s) emitted to all three targets (copilot, anthropic, d365fo-cli); "
          f"{len(canon)} canon block(s) written into skills/d365fo-cli/SKILL.md.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
