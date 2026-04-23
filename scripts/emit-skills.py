#!/usr/bin/env python3
"""Emit Copilot and Anthropic Agent-Skills variants from skills/_source/*.md.

Equivalent of scripts/emit-skills.ps1 for environments without PowerShell.
Single source of truth: skills/_source/<id>.md with YAML frontmatter.

Frontmatter keys:
  id:           (required) stable identifier, used as file/folder name
  description:  (required) one-line trigger description
  applyTo:      (Copilot)  list of globs
  appliesWhen:  (Anthropic) plain-text trigger description

Outputs:
  skills/copilot/<id>.instructions.md
  skills/anthropic/<id>/SKILL.md
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


def main() -> int:
    copilot_out = OUT_ROOT / "copilot"
    anthropic_out = OUT_ROOT / "anthropic"
    for p in (copilot_out, anthropic_out):
        if p.exists():
            shutil.rmtree(p)

    files = sorted(SOURCE.glob("*.md"))
    if not files:
        print("no source skills found", file=sys.stderr)
        return 0

    for f in files:
        print(f"» {f.name}")
        text = f.read_text(encoding="utf-8")
        fm_text, body = split_frontmatter(text)
        meta = parse_yaml(fm_text)
        for required in ("id", "description"):
            if required not in meta:
                raise SystemExit(f"{f.name}: missing '{required}'")
        emit_copilot(meta, body, copilot_out)
        emit_anthropic(meta, body, anthropic_out)

    print(f"\nDone. {len(files)} skill(s) emitted.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
