#!/usr/bin/env python3
"""Round-trip tests for the frontmatter quoting in scripts/emit-skills.py.

Run: python scripts/test-emit-skills.py

Plain asserts rather than a test framework — the skills job installs PyYAML and
nothing else, and this has to stay runnable on a bare `actions/setup-python`.
Keep scripts/emit-skills.ps1's ConvertTo-YamlScalar in lock-step with anything
changed here; the two emitters must produce byte-identical frontmatter.
"""
from __future__ import annotations

import sys
from pathlib import Path

import yaml

sys.path.insert(0, str(Path(__file__).resolve().parent))

from importlib import import_module

emit = import_module("emit-skills")


def roundtrip(key: str, value: str) -> object:
    """Emit `key: value` the way the emitter would, then parse it back."""
    return yaml.safe_load(f"{key}: {emit.yaml_scalar(value)}")[key]


def check(value: str, why: str) -> None:
    got = roundtrip("description", value)
    assert got == value, f"{why}: {value!r} round-tripped to {got!r}"


# The #172 shape: a colon-space makes YAML read a nested mapping key and the
# whole frontmatter block stops parsing.
check(
    "D365 F&O X++ skill. Use when working in a D365 F&O X++ project: writing "
    "classes, tables, forms, or any AOT artifact.",
    "colon-space must survive",
)

# A space-hash starts a comment in a plain scalar and silently truncates the
# value — no parse error, just a shorter string.
check(
    "User intent mentions macros and #define.",
    "space-hash must survive",
)

# Values that are fine bare must stay bare, so the fix causes no regeneration
# churn across the 70+ committed skill files.
for bare in (
    "Author a Chain-of-Command extension in D365FO without duplicating wrappers.",
    'Use when the user asks to "wrap a method" or "add a CoC".',
    "Scaffold an AxForm using one of the nine canonical patterns (SimpleList, Dialog).",
    "Move data in and out of D365FO — DMF, dual-write, virtual entities.",
):
    assert emit.yaml_scalar(bare) == bare, f"unnecessarily quoted: {bare!r}"
    check(bare, "bare value must survive")

# Shapes YAML would read as a non-string, or lose entirely.
for tricky in (
    "yes",
    "null",
    "~",
    "  leading and trailing  ",
    "- looks like a sequence item",
    "*anchor-ish",
    "trailing colon:",
    "{ looks like a flow mapping }",
    "@reserved",
    "%directive",
    '"already quoted"',
    "back\\slash and \"quotes\"",
):
    got = roundtrip("description", tricky)
    assert got == tricky, f"tricky value {tricky!r} round-tripped to {got!r}"
    assert isinstance(got, str), f"{tricky!r} parsed as {type(got).__name__}"

# The emitted frontmatter block as a whole must parse, not just the one line.
description = "Covers X++ interop: CLRInterop and #define."
applies_when = "User mentions interop: .NET calls, or #macros."
block = "\n".join(
    [
        "name: some-topic",
        f"description: {emit.yaml_scalar(description)}",
        f"applies_when: {emit.yaml_scalar(applies_when)}",
    ]
)
parsed = yaml.safe_load(block)
assert parsed["name"] == "some-topic"
assert parsed["description"] == description
assert parsed["applies_when"] == applies_when

print("OK — emit-skills frontmatter quoting round-trips.")
