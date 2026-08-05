# Self-improving agent eval loop — design spec

**Status:** implemented — a 31-case catalog (L0–L2) runs end-to-end. See
[eval/README.md](../eval/README.md) for day-to-day mechanics and the open
work queue.

**Adapted from:** the sibling repo `d365fo-mcp-server` runs the same pattern
against a live D365FO VM + compiler. This spec keeps that repo's vocabulary
(cases, corpus, golden oracle, triage rubric, implementer/improver split)
where it still applies, and calls out where d365fo-cli's fully-offline
`generate` → `validate` path let the design get simpler.

---

## 1. Goal

A self-improving harness that:

1. **Runs** cases — d365fo-cli use-cases of varying complexity — through
   d365fo-cli's own agent-facing tool surface (the `d365fo` CLI, which is
   also the MCP tool surface: same envelope, same commands).
2. **Verifies correctness** against **golden metadata** (primary oracle),
   plus the two offline static gates the CLI already ships:
   `validate xpp` (BP lint) and `validate references` (anti-hallucination).
3. **Feeds recurring failures back** as concrete fixes (scaffolder defects,
   validator gaps, skill-knowledge gaps), reproduced as a failing test and
   landed as a reviewable PR.

The deliverable is the same as the source repo's: **a measurable, decreasing
defect rate** across a growing case catalog — the tool provably gets better
at producing (and validating) X++/AOT XML that is structurally correct and
BP-clean.

### Non-goals

- Not a production code generator — an **eval + self-improvement harness**.
- Not auto-merge — the improver opens PRs; humans review and merge.
- Not a replacement for `GoldenQualityGateTests.cs` /
  `ScaffoldingSnapshotTests.cs` — it **feeds** them (fixes land as new tests
  there, or as new eval cases/goldens here).

---

## 2. Why this is simpler here than in the source repo

The source repo's loop needs a live D365FO VM + compiler + bridge, so it
splits into two roles running in two places (implementer on the VM,
improver in the repo) and carries real complexity to keep VM writes safe and
reversible (§4a fixtures/rollback, sandbox isolation, serialized builds).

d365fo-cli's core path is different: `generate` → `validate xpp` →
`validate references` is **fully offline**, proven by
`tests/D365FO.Core.Tests/GoldenQualityGateTests.cs`. So this loop:

- runs **entirely in-repo and in CI** — no VM, no bridge, no two-machine
  topology;
- needs **no rollback/undo machinery** — each case runs `generate` against a
  disposable temp SQLite index and writes to a temp file; the whole
  workspace is deleted after scoring, so there is nothing to roll back;
- needs **no shared-fixture problem** (source repo §4a) — the one case that
  extends an existing object (`L2-coc-extension`) reads from the
  already-checked-in `tests/Samples/MiniAot/TestModel` fixture, rebuilt
  fresh into a temp index at the start of every run.

What's missing relative to the source repo, honestly: no compiler
round-trip (`build`/`bp check` are Windows-VM-only and out of scope here) and
no runtime/SysTest oracle. The scorecard (§7) is smaller as a result — it
reports only what this loop can actually check.

---

## 3. Topology — two roles, one corpus, one repo

```
eval/cases/<id>.json  (instruction + optional canonical_args)
        │
        ├─ d365fo eval run <id>        deterministic replay (no agent): canonical_args →
        │                              generate → validate xpp/references → golden diff →
        │                              score → corpus record
        │
        └─ eval-runner agent           reads ONLY the natural-language `instruction`, drives
                                        `d365fo search/get/prepare/validate/generate` by hand
                                        via Bash, then `d365fo eval score <id> --actual <file>
                                        --source agent --write`

eval/corpus/runs/*.json  (gitignored — one record per run, either source)
        │
        ▼
d365fo eval report / eval clusters      pass rates by tier, ranked failure clusters
        │
        ▼
eval-improver agent ("the self-improver")
    reproduce the top cluster as a failing xUnit test → fix the real cause →
    `dotnet test` + `eval report` (held-out regression check) → open a PR
    citing the corpus evidence
```

**Why two roles still matters even without a VM split:** `eval run` is a
fast, deterministic, agent-free CI gate — it proves the scaffolders and
validators still agree with the goldens. It does **not** prove an agent can
correctly use the CLI from a plain-English ask, which is the loop's actual
purpose (d365fo-cli's own README: "wires up AI agents via a stable JSON
envelope and MCP"). That's what the **eval-runner** agent, working only from
a case's `instruction` field, is for. Both paths score through the same
`D365FO.Core.Eval.EvalScorer`, so a golden mismatch means the same thing
regardless of who drove the CLI.

The two roles communicate only through the corpus (`eval/corpus/runs/`) —
no shared in-memory state, and either can run on its own cadence.

---

## 4. The loop

For a replay run (`eval run <id>`):

1. **Load** the case from `eval/cases/<id>.json`.
2. **Provision** a disposable temp SQLite index — from
   `tests/Samples/MiniAot/TestModel` when `requires_fixture_index: true`,
   otherwise empty-but-schema-ensured.
3. **Replay** — spawn `dotnet <d365fo.dll> <canonical_args> --out <temp>
   --overwrite --output json` as a genuine **child process** against that
   index (`D365FO_INDEX_DB` passed via the child's environment). Not
   in-process: an earlier in-process design (build a `CliApp` and call
   `RunAsync` directly) was found to permanently corrupt Spectre.Console's
   process-wide `AnsiConsole` singleton for the rest of the host process —
   observed as flaky, silently-empty Table-mode output from every command
   that ran afterward in the same process. A real child process makes that
   entire class of shared-static-state hazard moot, and is arguably more
   honest anyway: byte-for-byte the same invocation a human or agent would
   run.
4. **Score** — `D365FO.Core.Eval.EvalScorer.Score(...)` calls
   `XppValidator.Validate` and `ReferenceResolver.Resolve` directly against
   the produced XML (same Core APIs `validate xpp`/`validate references`
   call — no subprocess needed for this part), then diffs the XML against
   the golden via `D365FO.Core.Eval.XmlGolden`.
5. **Record** (`--write`) — append an `EvalCorpusRecord` to
   `eval/corpus/runs/`.
6. **Clean up** — delete the temp workspace. Nothing to roll back; nothing
   was written outside it.

For an agent-driven run, the **eval-runner** agent does steps 1–3 by hand
(reading only `instruction`, never `canonical_args`) via Bash-driven
`d365fo` calls, saves the produced artifact, then calls `d365fo eval score
<id> --actual <file> --source agent [--hypothesis <CLASS>] --write` to do
steps 4–5.

---

## 5. Corpus record schema

One JSON file per run under `eval/corpus/runs/` (gitignored — documented in
[eval/corpus/schema.json](../eval/corpus/schema.json)):

```jsonc
{
  "runId": "20260805T083728551Z__L0-edt-basic__51980b5f41004f67973fd22b5702848d",
  "caseId": "L0-edt-basic",
  "tier": 0,
  "timestampUtc": "2026-08-05T08:37:28.551Z",
  "source": "replay",                    // "replay" | "agent"
  "score": {
    "xppClean": true, "xppErrors": 0,
    "referencesClean": true, "referenceErrors": 0,
    "goldenMatch": true,
    "goldenDiff": { "missing": [], "extra": [], "changed": [] }
  },
  "classification": null,                // set by the runner as a hypothesis; see §9
  "note": null
}
```

Self-contained: the improver must be able to act on it without re-running
anything.

---

## 6. Golden-metadata oracle

### 6.1 What a golden is

For each case, a captured, human-reviewed AOT-XML artifact under
`eval/goldens/<case_id>/*.xml` — exactly one file per case today.

### 6.2 Normalization (`D365FO.Core.Eval.XmlGolden`)

No golden-diff utility existed anywhere in this repo before this loop — the
existing "golden" tests (`GoldenQualityGateTests`, `ScaffoldingSnapshotTests`)
assert structurally against a live `XElement` tree rather than diffing
serialized XML. `XmlGolden` fills that gap:

- flattens an `XElement` tree into an order-independent `path → value` map;
- repeated sibling elements (e.g. multiple `<AxTableField>` under
  `<Fields>`) are keyed by their own `<Name>` child or `DataField`
  child/attribute, so collection reordering never registers as a diff;
- per-case `ignore` glob patterns (`*` / `**`) strip legitimately variable
  nodes;
- the diff is `missing[] / extra[] / changed[]`, mirroring the corpus
  record shape.

### 6.3 Authoring goldens (bootstrapping)

Never hand-authored. Run the case for real (`d365fo generate ... --out
<file>`), inspect the output, then `d365fo eval capture <id> --actual
<file>` to land it as the golden — same discipline as the source repo's
§6.4, and consistent with this repo's own golden-test precedent.

---

## 7. Scorecard

| Signal | Meaning |
|---|---|
| `xppClean` | `validate xpp` (BP lint, `D365FO.Core.Validation.XppValidator`) found zero errors |
| `referencesClean` | `validate references` (anti-hallucination gate, `ReferenceResolver`) found zero errors |
| `goldenMatch` | normalized actual XML == normalized golden XML — **primary correctness signal** |

Smaller than the source repo's scorecard by design: there is no
`build`/`bp_clean` dimension (nothing to compile offline) and no `systest`
dimension (no runtime harness here) — only what the offline grounding chain
can actually check. `d365fo eval report` aggregates pass rates per tier plus
a classification-count breakdown across the whole corpus.

---

## 8. Case catalog

`eval/cases/<id>.json`, shape documented in
[eval/cases/schema.json](../eval/cases/schema.json) and enforced in code by
`D365FO.Core.Eval.EvalCaseCatalog` (hand-rolled validation — no JSON-Schema
library is used anywhere else in this repo):

```jsonc
{
  "id": "L2-coc-extension",
  "title": "Chain-of-Command wrapper on FmVehicleService.run",
  "tier": 2,
  "instruction": "Add a Chain-of-Command extension class wrapping the run() method on ...",
  "canonical_args": ["generate", "coc", "FmVehicleService", "--method", "run"],
  "target_artifact_types": ["AxClass"],
  "golden_path": "L2-coc-extension",
  "tags": ["coc", "extension", "class", "anti-hallucination"],
  "requires_fixture_index": true,
  "golden_pending": false
}
```

`canonical_args` is optional — cases meant only for the eval-runner agent
(not yet reproducible deterministically, or deliberately testing whether an
agent arrives at a *different but still valid* structure) can omit it and
rely on `eval score` alone.

Tiers (unchanged from the source repo's convention):

| Tier | Example |
|---|---|
| L0 — trivial | new EDT, new enum |
| L1 — single object | table, class |
| L2 — extension | CoC wrapper, event handler, table/form extension |
| L3 — composite | relation + generated form + datasource rebind |
| L4 — feature slice | data entity + security chain, batch job, SSRS report |

Current catalog: 31 cases, L0–L2 (`generate edt`, `enum`, `table`, `class`,
`coc`, `form`, `query`, `map`, `report`, `sysoperation`, `runbase`,
`business-event`, `custom-service`, `workflow`, `systest`,
`extension table/edt/enum/form`, `event-handler`, `number-sequence`, `entity`,
`security-policy`, `privilege`, `duty`, `role`, `menu-item`, `view`,
`migration-script`, `datasource-method`, `control-method`) — every `generate`
subcommand except the bridge-only `modify`/`test`/`bp` branches. See
[eval/README.md](../eval/README.md) for the standing "grow the catalog" queue.

---

## 9. Triage / attribution rubric

Unchanged from the source repo — the distinction (genuine tool defect vs.
the agent's own mistake) is exactly as load-bearing here:

| Class | Definition | Becomes a fix? | Fix area (this repo) |
|---|---|---|---|
| `TOOL_DEFECT` | a scaffolder produced wrong/missing/over-eager output | **Yes** | `src/D365FO.Core/Scaffolding/` |
| `VALIDATOR_GAP` | `validate xpp`/`validate references` should have blocked (or shouldn't have blocked) but didn't (did) | **Yes** | `src/D365FO.Core/Validation/`, `src/D365FO.Core/FormPatterns/` |
| `KNOWLEDGE_GAP` | the agent-facing skill docs gave wrong/missing guidance that led the eval-runner astray | **Yes** | `skills/_source/**` (rerun `scripts/emit-skills.py` after editing) |
| `MODEL_ERROR` | tool output was correct; the agent misused it | **No** — at most a prompt tweak | — |
| `ENV_FLAKE` | transient/infra | **No** — retry or ignore | — |

The runner (replay or agent) records a **hypothesis** (`classification` +
`note` on the corpus record); the improver **confirms** it before acting —
it must reproduce `TOOL_DEFECT`/`VALIDATOR_GAP` deterministically as a new
repo test, with no external dependency, before touching any source.

**Triage bias, carried over from the source repo's hard-won lesson:** an
honest failure beats a confident lie. The most damaging class of defect is a
tool *asserting something false* rather than failing — reporting a clean
validation from evidence that was never actually collected, claiming an
object doesn't exist when the index is just stale. Making a failure honest
is a real fix, not a placeholder for one.

---

## 10. Improver workflow

1. **Survey** — `d365fo eval report` (scoreboard) and `d365fo eval clusters`
   (failing runs grouped by `(classification, case_id)`, ranked by
   frequency).
2. **Pick** the top actionable cluster (`TOOL_DEFECT | VALIDATOR_GAP |
   KNOWLEDGE_GAP`). `MODEL_ERROR`/`ENV_FLAKE` are not fixes.
3. **Confirm** the classification by reproducing it as a new, currently
   failing xUnit test (style: `GoldenQualityGateTests.cs`) under
   `tests/D365FO.Core.Tests/`.
4. **Fix** the real cause — one scaffolder, one validator rule, or one
   skill-knowledge entry.
5. **Validate** — `dotnet test` (full suite must stay green) and `d365fo
   eval report` / re-running the case catalog (held-out regression check —
   never validate only against the failing case).
6. **Open a PR** citing the corpus `runId`(s), the new repro test, and
   before/after scorecards. Never auto-merge.

---

## 11. Isolation & safety

- Every replay run targets a **disposable temp SQLite index** and a
  **disposable temp output directory**, both deleted after scoring —
  nothing is written outside the OS temp directory (enforced independently
  by `D365FO.Core.Guardrails.PathGuard`, which blocks writes outside the
  packages root / workspace root / temp directory regardless).
- The fixture index (`tests/Samples/MiniAot/TestModel`) is read-only input,
  rebuilt fresh into the temp index at the start of every run that needs it
  — never mutated, never shared across runs.
- Replay never touches the real `D365FO_INDEX_DB` the caller has configured
  — it's a genuine child process with its own environment.

---

## 12. Open questions / risks

- **Golden authoring cost** grows with the catalog — same mitigation as the
  source repo: start with deterministic tiers, capture-then-review.
- **No runtime oracle.** Cases whose correctness is behavioral, not
  structural (method bodies beyond a wrapper), are under-covered by a golden
  diff alone — this loop has no SysTest-equivalent layer yet.
- **CI wiring is not yet built.** `eval run` for the full catalog is not a
  gate in `.github/workflows/ci.yml` today — see
  [eval/README.md](../eval/README.md) for what's explicitly deferred.
