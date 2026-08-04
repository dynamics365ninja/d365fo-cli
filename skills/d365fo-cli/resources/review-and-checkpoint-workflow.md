> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

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

For AOT XML, the diff must be additive or narrowly targeted. If unrelated XML
nodes disappear, the edit is wrong and must be reverted before continuing.
Treat removals of `<DataSourceModifications>`, `<DataSourceReferences>`,
`<DataSources>`, `<Controls>`, methods, pattern metadata, or extension
properties as high-risk unless the user explicitly requested that removal.

**Hand-written X++ never reaches a file unvalidated:**

```sh
d365fo validate references --file <f> --output json   # every identifier proven against the index; exit 2 = hallucinated symbols
d365fo validate xpp --file <f> --output json          # offline BP rules (today(), CoC defaults, labels, …); exit 2 = errors
```

Fix all errors, re-run, only then write. Both gates run in <200 ms with no VM.

**Changed AOT XML has its own gate:**

```sh
# Parse with an XML validator first. Then:
d365fo validate xpp --file <f> --code-type xml-any --output json
d365fo index refresh --model <Model>
d365fo get form <Form> --output json      # for AxForm/AxFormExtension changes
d365fo get table <Table> --output json    # for AxTable/AxTableExtension changes
```

For new forms based on an example, compare the pattern metadata and required
controls/datasources from the example. Missing ActionPane/Body/Tab/FastTab/grid
or QuickFilter elements are not acceptable just because the XML parses.

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
