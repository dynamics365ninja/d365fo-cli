---
name: review-and-checkpoint-workflow
description: Use Git as the review layer for AI-driven D365FO edits, and use `d365fo review diff` to get an AOT-semantic summary of XML changes. Invoke whenever the user is about to start a non-trivial change, before "accepting" AI edits, or wants a structural diff (added classes, modified table fields, new CoC wrappers).
applies_when: User intent mentions reviewing changes, accepting / rejecting AI edits, "what changed", AOT diff, structural diff, or VS 2022's missing accept/undo UI.
---
# Git-checkpoint review workflow

> Visual Studio 2022 has no inline accept/reject UI for AI edits. Use Git as
> the review layer; pair with `d365fo review diff` for an AOT-semantic
> summary on top of the raw byte diff.

## 1. Before starting any non-trivial task

```sh
# Either: clean tree + a fresh branch
git switch -c d365fo/<short-task>

# Or: at minimum, a checkpoint commit on the current branch
git commit -am "checkpoint before <task>"
```

Do NOT create branches autonomously without telling the user — propose,
wait, then execute.

## 2. During the task

Every `d365fo generate … --overwrite` writes a `.bak` next to the original
so you can recover the previous version if Git history isn't enough.

After each scaffold or edit, run a quick `git diff` to confirm the change is
contained.

## 3. After the task — AOT-semantic review

```sh
# Raw byte diff (as usual)
git diff --stat
git diff <ref> -- AxClass/ AxTable/ AxForm/

# AOT-semantic diff — added classes, modified table fields, new CoC wrappers …
d365fo review diff --base <ref> --output json
d365fo review diff --base HEAD~1 --output json | jq '.data.added,.data.modified'
```

`review diff` is **complementary** to `git diff`, not a replacement:

| Tool | Shows | Best for |
|---|---|---|
| `git diff` | Raw bytes per file | Spotting whitespace / unintended edits |
| `d365fo review diff` | Structural deltas (added field, new wrapper, index change) | Reviewer summary, PR descriptions |

## 4. Accept / reject

- **Accept** — `git add -A && git commit && git switch main && git merge <branch>`.
- **Reject** — `git restore` (working-tree changes) or `git branch -D` (whole branch).
  The `.bak` files remain to recover individual files.

## Hard rules

- Never bypass safety checks — no `git push --force`, no `--no-verify`, no
  `git reset --hard` on shared branches. Discard with `git restore` or branch deletion.
- Never run `d365fo build` / `bp check` automatically as part of the review
  flow — they block the user. Say *"Diff summarised. Run `d365fo build`
  when you're ready."*
- Always include the `.bak` files in `.gitignore` for the user's repo so
  scaffold-overwrite backups don't pollute commits.
- Always show `d365fo review diff` output BEFORE asking the user to accept
  — they need the structural summary to make a decision.
