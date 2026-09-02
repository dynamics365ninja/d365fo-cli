# Authoring knowledge

The knowledge corpus is what an agent reads *before* it calls anything: 43 topics on how D365FO
actually works, served by `d365fo knowledge get` and by the `get_knowledge` MCP tool, and emitted
as skills for Copilot, Claude and this repository's own layout.

**One source, three outputs.** Every topic lives in `skills/_source/<id>.md`. Nothing else is
edited by hand:

```
skills/_source/<id>.md
   ├── skills/copilot/<id>.instructions.md      (GitHub Copilot)
   ├── skills/anthropic/<id>/SKILL.md           (Claude — one skill per topic)
   ├── skills/d365fo-cli/references/<id>.md     (one skill, lazy references)
   └── embedded into D365FO.Core                (`d365fo knowledge`, the MCP tool)
```

Editing an emitted file is a change that survives until the next `emit-skills.py` run and no
longer. CI fails on the drift.

---

## Adding a topic

### 1. Write it

`skills/_source/my-topic.md`, frontmatter first:

```markdown
---
id: my-topic
description: One line naming the situations this applies to, including the phrases a user would actually say ("create a form", "extend a table"). This is the trigger — an agent decides from this alone whether to page the topic in.
covers: Short phrase for the reference table
applyTo:
  - "**/AxMyThing/**"
appliesWhen: Plain-text trigger description for the Anthropic layout.
---

# What this is about

...
```

`id`, `description` and `covers` are required. `applyTo` (Copilot globs) and `appliesWhen`
(Anthropic) are per-ecosystem and optional.

### 2. Ground every claim

This is the part that matters, and it is not a style preference. The corpus is what an agent
believes without checking; a plausible-sounding wrong sentence in it costs more than no sentence
at all. Before writing that an API takes three arguments, ask the installation:

```sh
d365fo get class DocumentManagement --output json     # then read the signature
d365fo search any AssetBarCodeDP
d365fo find refs SomeMethod
```

Two of this corpus's own topics were wrong in exactly the plausible way until they were checked:
`DocumentManagement::attachFile` takes nine arguments, so the natural eight-argument call compiles
and files the note under the wrong parameter; and of the 375 static methods `Global` declares only
34 are also predefined functions, so "it is on Global" does not mean "you can call it unqualified".

### 3. Emit and audit

```sh
python scripts/emit-skills.py
d365fo knowledge audit --verify
```

The audit resolves every identifier the corpus names against the index and fails on one that does
not exist. It runs in CI in snapshot mode — against `eval/knowledge-audit.snapshot.json`, captured
on a machine with a full installation, because CI has no D365FO. When you add symbols on a machine
that *does* have one:

```sh
d365fo knowledge audit --capture     # re-captures the snapshot from the live index
d365fo knowledge audit --verify      # confirm the committed snapshot now covers them
```

A name that legitimately resolves nowhere — a class from a module this installation lacks, an
illustrative placeholder — goes in `eval/knowledge-audit.allow.json` with a reason, not into the
snapshot.

### 4. Check it is reachable

```sh
d365fo knowledge list
d365fo knowledge search "the words a user would use"
d365fo eval coverage             # does the topic close a leaf?
```

`eval coverage` scores every AOT family and every `generate` subcommand on **K ∧ E ∧ T** —
taught by the corpus, proven by an eval case, built by a command. A topic that names
`generate <command>` or an `AxSomething` root element is what turns the K on for that leaf; the
report is generated from the corpus and the registries, never from a list, so it cannot flatter
itself.

---

## Rule canon blocks

A rule that must be identical everywhere — in the CLI's agent prompt, in the MCP server
instructions, and in the skill — is written once, inside a canon block:

```markdown
<!-- canon:never-handwrite-xml -->
> ⛔ **NEVER write X++ AOT XML files directly** …
<!-- /canon -->
```

`D365FO.Core.Knowledge.RuleCanon` reads those blocks at runtime to compose the agent prompt and
the server instructions; `emit-skills.py` writes them into `skills/d365fo-cli/SKILL.md`. The rule
therefore has exactly one home, and the three consumers cannot drift apart.

---

## What belongs in the corpus

| Belongs | Does not |
|---|---|
| Platform behaviour an agent will otherwise guess wrong ("attachFile takes nine arguments") | Anything `--help` already says |
| The shape of a correct artefact, and what silently breaks it | A tutorial for X++ as a language |
| Which command to reach for, and the order to reach for them in | Command reference — that is [CAPABILITIES.md](CAPABILITIES.md) |
| A named failure and why it happens ("`geAssetBarCodeTmp` — the platform ships the typo") | Speculation about what the platform "probably" does |

The test is falsifiability: if you cannot say how you would check the sentence against an
installation, it is not knowledge, it is a guess wearing knowledge's clothes.

---

## See also

- [AGENT_EVAL_LOOP.md](AGENT_EVAL_LOOP.md) — how a topic's claims become a scored eval case.
- [NEW_TOOL_CHECKLIST.md](NEW_TOOL_CHECKLIST.md) — a new command needs a topic, not just tests.
- [TESTING.md](TESTING.md) — where the knowledge audit sits among the gates.
