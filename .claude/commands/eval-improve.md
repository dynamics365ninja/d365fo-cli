---
description: Work the eval-loop corpus — rank failure clusters, fix the top actionable defect, validate against the full suite, open a PR.
argument-hint: [cluster/area or blank for top-priority]
allowed-tools: Agent, Bash, Read, Edit, Write, Grep, Glob
---

Launch the **eval-improver** subagent (Task tool, `subagent_type:
eval-improver`) to run the improver ("self-improver") role of the d365fo-cli
agent eval loop.

Focus for this run: **$ARGUMENTS**
(If blank, take the top-priority actionable cluster from `d365fo eval
clusters`.)

The subagent already carries the full protocol (docs/AGENT_EVAL_LOOP.md §9–10:
survey corpus → confirm rubric class → reproduce as a failing xUnit test →
fix the scaffolder/validator/skill content → validate the full suite → open
a PR citing corpus evidence). Do not re-explain it. Relay the subagent's
verdict and the resulting diff/PR back to me. Do not commit or push unless I
ask.
