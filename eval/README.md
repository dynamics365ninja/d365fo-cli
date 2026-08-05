# Agent eval loop

Self-improving agent eval loop for d365fo-cli's own tool surface. Full design
in [docs/AGENT_EVAL_LOOP.md](../docs/AGENT_EVAL_LOOP.md). This file covers
day-to-day mechanics.

Adapted from a sibling repo, `d365fo-mcp-server`, which runs the same pattern
against a live D365FO VM + compiler. d365fo-cli's `generate` → `validate xpp`
→ `validate references` path is fully offline (confirmed by
`tests/D365FO.Core.Tests/GoldenQualityGateTests.cs`), so this loop needs no
VM, no bridge, and no fixture-rollback machinery — every case runs against a
disposable temp SQLite index + temp output dir.

## Layout

```
eval/
├── README.md              ← this file
├── cases/
│   ├── schema.json         ← documented case shape (informational; EvalCaseCatalog enforces it in code)
│   └── <case-id>.json      ← one file per case
├── goldens/
│   └── <case-id>/*.xml     ← committed, human-reviewed golden output (exactly one file per v1 case)
└── corpus/
    ├── schema.json          ← documented run-record shape
    └── runs/                ← one *.json run record per run (gitignored — local/CI evidence)
```

## Running a case

Deterministic replay (no agent, no LLM cost — the CI-safe path). Requires
the case to carry `canonical_args`:

```
dotnet run --project src/D365FO.Cli -- eval run L0-edt-basic --write
```

This builds a throwaway temp SQLite index (plus fixture data from
`tests/Samples/MiniAot/TestModel` when the case sets
`requires_fixture_index: true`), replays `canonical_args` through the exact
same `CommandApp` the real CLI runs (`D365FO.Cli.CliApp.Build()`), then
scores the produced XML against `eval/goldens/<id>/` — `validate xpp` and
`validate references` are called directly against the Core APIs
(`D365FO.Core.Validation.XppValidator` / `ReferenceResolver`), not via a
subprocess. `--write` appends a record to `eval/corpus/runs/`.

Agent-driven run (what actually tests "can an agent use this tool
correctly" — the loop's real purpose): the **eval-runner** agent
(`.claude/agents/eval-runner.md`, or `/eval-run <case-id>`) reads the case's
`instruction` only, drives `d365fo search/get/prepare/validate/generate` by
hand via Bash, then scores with:

```
dotnet run --project src/D365FO.Cli -- eval score L0-edt-basic \
  --actual /path/to/produced.xml --source agent --write
```

Both paths score through the same `D365FO.Core.Eval.EvalScorer`, so a golden
mismatch means the same thing regardless of who drove the CLI.

## Improver toolchain

```
dotnet run --project src/D365FO.Cli -- eval report     # pass-rates by tier + classification counts
dotnet run --project src/D365FO.Cli -- eval clusters   # failing runs grouped by (classification, case), ranked by frequency
```

The **eval-improver** agent (`.claude/agents/eval-improver.md`, or
`/eval-improve [cluster]`) reads these, picks the top actionable cluster,
reproduces it as a new failing xUnit test (style: `GoldenQualityGateTests.cs`),
fixes the real cause, and opens a PR. See docs/AGENT_EVAL_LOOP.md for the
triage rubric.

## Authoring a new case

The **eval-author** agent (`.claude/agents/eval-author.md`, or
`/eval-author <feature>`) drafts `eval/cases/<id>.json` with
`golden_pending: true`. Land the golden for real, never hand-authored:

```
dotnet run --project src/D365FO.Cli -- eval run <id>          # inspect the actual.xml it prints the path to on failure, or re-run with --actual capture
dotnet run --project src/D365FO.Cli -- eval capture <id> --actual <path-to-reviewed.xml>
```

Then review the captured XML for real and flip `golden_pending` to `false`
in the case JSON in the same PR.

## Triage bias: an honest failure beats a confident lie

Carried over from the sibling repo's hard-won lesson (its `eval/README.md`):
the most damaging defects are the tool **asserting a falsehood** rather than
failing — claiming an object doesn't exist when the index is just stale,
scoring a dimension clean from evidence that was never actually collected,
recommending something the compiler would reject. Making a failure honest
(fail loudly, with the real cause, instead of silently reporting success) is
a real fix, not a placeholder for one — prefer it over leaving a confident
lie in place while a deeper fix waits.

## Explicitly out of scope (for now)

- **CI wiring.** `eval run` for all 5 cases is not yet a gate in
  `.github/workflows/ci.yml`. Natural follow-up once the catalog has grown
  past the initial 5.
- **No runtime/SysTest oracle.** No `SysTestRunner` integration exists in
  this offline loop — only the golden-diff and static-validator dimensions.
- **`MODEL_ERROR` → knowledge-base feedback tooling.** The sibling repo has
  an automated "cluster MODEL_ERROR runs into `skills/_source` proposals"
  step (`knowledgeFeedback.ts`); not built here yet. The rubric and agent
  roles already support adding it.
- **Catalog breadth.** 5 cases across L0–L2 today (`generate edt`, `enum`,
  `table`, `class`, `coc`). Wider `generate` coverage (forms, extensions,
  security, entities, …) is the natural next slice — use `eval-author`.
