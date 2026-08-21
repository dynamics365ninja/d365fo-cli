#!/usr/bin/env python3
"""Fail if any shipped skill file has frontmatter a YAML parser cannot read.

The drift job next to this one compares generated output against skills/_source.
It catches a stale file; it does not catch a *malformed* one — issue #172 shipped
frontmatter that no YAML parser could load and CI stayed green, because the bytes
matched what the emitter produced. Parsing is the missing half of that check.

Covers the generated variants and the hand-written files alike: skills/d365fo-cli/
SKILL.md keeps its own frontmatter (emit-skills.py only refreshes the marker-
delimited regions below it), so an emitter fix alone would never protect it.

Usage: python scripts/check-skill-frontmatter.py [root]
"""
from __future__ import annotations

import sys
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover - environment problem, not a content problem
    print("error: PyYAML is required (pip install pyyaml)", file=sys.stderr)
    raise SystemExit(2)

PATTERNS = (
    "skills/**/SKILL.md",
    "skills/copilot/*.instructions.md",
    ".claude/skills/**/SKILL.md",
    ".github/skills/**/SKILL.md",
)

# Keys every consumer reads. A file may carry more; it may not lose these.
REQUIRED = {
    "SKILL.md": ("name", "description"),
    ".instructions.md": ("description",),
}


def required_keys(path: Path) -> tuple[str, ...]:
    for suffix, keys in REQUIRED.items():
        if path.name.endswith(suffix):
            return keys
    return ()


def frontmatter(text: str) -> str | None:
    """The block between the opening '---' fence and the next one, or None."""
    if not text.startswith("---"):
        return None
    rest = text[3:].lstrip("\r\n")
    end = rest.find("\n---")
    if end == -1:
        return None
    return rest[:end]


def check(path: Path) -> list[str]:
    text = path.read_text(encoding="utf-8")
    block = frontmatter(text)
    if block is None:
        return ["no '---' frontmatter fence at the top of the file"]

    try:
        data = yaml.safe_load(block)
    except yaml.YAMLError as exc:
        detail = str(exc).replace("\n", " ")
        return [f"frontmatter is not valid YAML: {detail}"]

    if not isinstance(data, dict):
        return [f"frontmatter parsed as {type(data).__name__}, expected a mapping"]

    problems = []
    for key in required_keys(path):
        value = data.get(key)
        if value is None:
            problems.append(f"missing required key '{key}'")
        elif not isinstance(value, str):
            # The #172 shape: 'description: a: b' parses as a nested mapping
            # rather than failing outright, so a clean parse is not enough.
            problems.append(
                f"'{key}' parsed as {type(value).__name__}, expected a string "
                "— quote the value if it contains a colon"
            )
        elif not value.strip():
            problems.append(f"'{key}' is empty")
    return problems


def main(argv: list[str]) -> int:
    root = Path(argv[1]).resolve() if len(argv) > 1 else Path(__file__).resolve().parent.parent

    seen: set[Path] = set()
    for pattern in PATTERNS:
        seen.update(p for p in root.glob(pattern) if p.is_file())

    if not seen:
        print(f"error: no skill files found under {root}", file=sys.stderr)
        return 2

    failures = 0
    for path in sorted(seen):
        rel = path.relative_to(root).as_posix()
        for problem in check(path):
            print(f"::error file={rel}::{problem}")
            failures += 1

    if failures:
        print(f"\n{failures} frontmatter problem(s) across {len(seen)} skill file(s).", file=sys.stderr)
        return 1

    print(f"OK — frontmatter parses in all {len(seen)} skill file(s).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
