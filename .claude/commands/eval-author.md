---
description: Draft a new eval case (eval/cases/<id>.json), scaffolding it for golden capture.
argument-hint: <short description of the feature/instruction to cover>
allowed-tools: Agent, Bash, Read, Edit, Write, Grep, Glob
---

Launch the **eval-author** subagent (Task tool, `subagent_type:
eval-author`) to draft a new eval case.

What the case should cover: **$ARGUMENTS**

The subagent carries the full authoring contract (docs/AGENT_EVAL_LOOP.md
§8, `eval/cases/schema.json`): pick the right tier, write an unambiguous
`instruction`, add `canonical_args` when the case is deterministic, set
`golden_pending: true`, and validate against the real catalog loader. Relay
the drafted case JSON and the next step (capture the golden via
`eval-runner`/`eval capture`) back to me.
