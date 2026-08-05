---
description: Run an eval case end-to-end (drive its instruction through the plain d365fo CLI, or replay canonical_args) and score it against the golden.
argument-hint: <case-id>
allowed-tools: Agent, Bash, Read, Grep, Glob
---

Launch the **eval-runner** subagent (Task tool, `subagent_type: eval-runner`)
to run this eval case:

Case id: **$ARGUMENTS**

First read `eval/cases/$ARGUMENTS.json`. The subagent carries the full
protocol (docs/AGENT_EVAL_LOOP.md §3–4): prefer the deterministic `d365fo
eval run` replay when the case has `canonical_args`; otherwise drive the
case's natural-language `instruction` by hand through the plain `d365fo`
CLI, then score with `d365fo eval score`. No VM or live D365FO connection
needed — this runs entirely offline in the repo. Relay the corpus scorecard
and, on failure, the subagent's hypothesis back to me.
