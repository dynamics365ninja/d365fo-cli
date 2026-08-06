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
    └── runs/                ← one *.json run record per run (committed, matching d365fo-mcp-server's eval/corpus/runs/)
```

## Running a case

Deterministic replay (no agent, no LLM cost — the CI-safe path). Requires
the case to carry `canonical_args`:

```
dotnet run --project src/D365FO.Cli -- eval run L0-edt-basic --write
dotnet run --project src/D365FO.Cli -- eval run --all            # whole catalog, non-zero exit on any golden mismatch
```

This builds a throwaway temp SQLite index (plus fixture data from
`tests/Samples/MiniAot/TestModel` when the case sets
`requires_fixture_index: true`), replays `canonical_args` through a real
`d365fo` child process (never in-process — see the note in
`EvalRunCommand.RunReplay`), then
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

## The L3 build oracle (Windows + a D365FO installation)

Replay proves a golden is structurally what was reviewed. It cannot prove the X++
inside it compiles — `validate xpp` is a regex BP linter, not a compiler. That is
what `eval verify-build` is for:

```
dotnet run --project src/D365FO.Cli -- eval verify-build --write      # compile every golden, persist verdicts
dotnet run --project src/D365FO.Cli -- eval verify-build --case L1-query-basic
dotnet run --project src/D365FO.Cli -- eval verify-build --provision-only --work-dir ./out   # any OS, no compiler
```

It provisions every reviewed golden — plus the mini-AOT fixture, into the *same*
module — as a throwaway model under a temp directory, runs `xppc.exe` against it,
attributes each diagnostic to the case whose golden produced the named object, and
writes `eval/golden-build-verification.json`. A case scores one artifact but its
command usually ships several; the siblings go in a `_companions` subfolder of the
golden directory, where the scorer does not see them and the compiler does. `--write` also appends corpus records
carrying only the build dimension; the offline dimensions stay null there rather
than being copied from an earlier, possibly stale replay.

Nothing here degrades quietly: off Windows it returns `UNSUPPORTED_PLATFORM`, with
no installation `XPPC_NOT_FOUND`, and if the compiler rejects the argument list it
returns `EVAL_BUILD_INVOCATION` and writes **no** verdicts — an argument mistake
must never be recorded as a broken golden. `EvalScoreCard.BuildClean` is `null`
wherever no compiler ran; it is never `false` by default.

Current baseline: **51 of 51 goldens compile clean**, with **zero unattributed
diagnostics** — every complaint the compiler makes is blamed on the case that
produced the object it names.

The number has moved both ways, and only one direction is about the tool: it drops
whenever the parser learns a severity prefix it was silently discarding
(`Metadata Error`, `FormPatternValidation Error`, …), and rises when a scaffolder is
fixed. Treat a *rise in unattributed diagnostics*, not a fall in the clean count, as
the sign something is wrong with the harness.

That number went *down* from 48 as the oracle got more honest, not as the tool got
worse. The metadata validator reports in its own shape
(`Metadata Error: AxForm/<object>/Design/Controls/…/DataGroup: …`), which the
parser did not recognise, and the attribution key was the golden's file stem —
which truncates `NoYes.Extension` at the dot. Both meant real findings were being
dropped on the floor while the scoreboard looked good.

Cases tagged `known-reference-gap` are recorded but never classified as
`TOOL_DEFECT`: their X++ names a sibling artifact the case's single golden does not
include, or a standard object the fixture cannot contain, and the compiler rejects
those for exactly the reason `validate references` does.

Defects this oracle has found and that are now fixed — none of them visible to a
golden diff or to any offline validator, which is the whole argument for the tier:

| Scaffolder | What the compiler said |
|---|---|
| `generate query` | the metadata reader could not read a generated query at all (`KeyNotFoundException`) |
| `generate event-handler` | "The method name in the source code … does not match the name in the XML file, ''" |
| `generate migration-script` | "'count' is an invalid name for a variable because it is an X++ keyword" |
| `generate business-event` | "Cannot implicitly convert from type 'str' to type 'Extensible Enumeration(ModuleAxapta)'", plus a `parmId()` that exists on no business event |
| `generate report` | five missing mandatory `AX_*` framework parameters, no default parameter group, "Invalid page size", and an extra dataset with no fields |
| `generate entity` | "There must be a key defined for a public data entity" — `IsPublic` was hard-coded `Yes` while `Keys` stayed optional |
| `generate form` | `<DataGroup>` emitted without its sibling `<DataSource>` and before `<Controls>` instead of after, so "Field group 'Overview' does not exist" even once the table declared it; and an `AxFormActionPaneTabControl` placed directly under a tab page, which the reader forbids |


## AOT form-pattern compliance — done

All nine form patterns now name a pattern the AOS has, and every form eval case
compiles clean against it.

`scripts/emit-form-patterns.ps1` derives the whole registry — 162 definitions across
top-level patterns *and* container sub-patterns: versions, required parts with
cardinality, choice slots, and the property values each part must carry — from
`Microsoft.Dynamics.AX.Metadata.Patterns.dll` into
`src/D365FO.Core/FormPatterns/form-patterns.json`, the same way
`emit-metadata-contracts.ps1` derives the DataContract catalog. `FormPatternRegistry`
serves it with no installation present.

**`FormPatternCatalog` takes the structural half of a pattern from that registry** —
versions, design properties, the required control tree, and what may sit at the design
root. Editorial content (purpose, when to use it, reference forms, lifecycle guidance)
stays hand-written, because the registry does not know it.
`FormTemplatePatternRegistryTests` pins every template's design pattern against the
registry, so naming a pattern that does not exist is an offline test failure rather
than a VM-only surprise. Its `KnownWrong` list is empty.

What the migration corrected, beyond five wrong version numbers:

| Was | Is |
|---|---|
| `Lookup 1.2` | `LookupGridOnly 1.1` — there is no pattern named `Lookup` at all |
| `Workspace 1.0` | `WorkspaceOperational 1.1` — `Workspace` exists only as an inactive `2.0` |
| `ListPage 1.1` | `ListPage` with its single version, the string `UX7 1.0` |
| `SidePanel` declared as a sub-pattern | a `Style`, not a pattern |
| `Workspace_Tiles`, `Workspace_Links`, `ToolbarAndList`, … | `SectionTiles`, `SectionRelatedLinks`, `ToolbarList`, … — the catalog's own note said these names were "to be confirmed by mining"; the registry confirmed the alias was the real one |

Five things only the compiler could have told us, each now in a template:

- a Details Master title group requires a `HeaderTitle`, and `MainGrid.DefaultAction`
  cannot be empty;
- a form may not repeat a control name, but the *extension's* name is what identifies
  a control's type — so two quick filters differ in the outer name only;
- a Table of Contents page needs a title group *and* a content group, even an empty
  one, and must not declare `FieldsFieldGroups` (the TOC pattern already governs it,
  and that sub-pattern forbids the title the TOC pattern requires);
- an operational workspace's lists are **not inline**: a tabbed-list page must hold a
  `FormPartControl` pointing at a separate `FormPartSectionList` form. `generate form
  --pattern Workspace` now refuses `--field`/`--section` and says so, rather than
  emitting a dangling FormPart or a form the AOS rejects;
- the section sub-patterns' `1.0` versions are inactive — `1.1` is current.

Two derivation corrections came out of doing it, both in `RegistrySpecFactory`: a
part's declared children are the ones that must be present, not the only ones that
may be (the closed set is the design root); and versions come in two lineages — plain
numbers and an older series whose version string is literally `UX7 1.0`, which sorts
above `1.4` unless the lineages are ranked.

## Improver toolchain

```
dotnet run --project src/D365FO.Cli -- eval report                  # pass-rates by tier + classification counts
dotnet run --project src/D365FO.Cli -- eval clusters --actionable   # ranked TOOL_DEFECT/VALIDATOR_GAP/KNOWLEDGE_GAP clusters with run ids
dotnet run --project src/D365FO.Cli -- eval knowledge --out brief.md   # KNOWLEDGE_GAP/MODEL_ERROR -> skills/_source proposals
dotnet run --project src/D365FO.Cli -- eval coverage --write        # regenerate eval/COVERAGE.md (K and E and T)
```

Replay runs record a **classification hypothesis** automatically: replay is
agent-free, so a golden mismatch can only be the scaffolder changing, and a
validator rejecting output that matches a reviewed golden can only be the
validator. Agent runs stay unclassified unless the runner passes `--hypothesis` —
there the same scorecard is equally consistent with a tool defect and with the
agent's own mistake, and guessing would send the improver at innocent code.

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

- **No runtime/SysTest oracle** (Phase 4.3). `test run` shells out to
  `SysTestRunner.exe` and returns a raw output tail — there is no result parser to
  build a scorecard dimension from, and no per-run fixture provisioning inside a
  live model. The build oracle above is the L3 tier; L4 is still missing.
  A handful of cases (tagged `known-reference-gap`) generate a class or
  extension whose body legitimately references a *sibling* artifact or a
  standard system object that a minimal offline index can't contain (e.g.
  the SysOperation controller's own Service class, a CoC extension over a
  standard `NumberSeqApplicationModule_*` class, or a computed view's
  `SysComputedColumn` call) — `referencesClean: false` there is the expected,
  honest result of scoring one artifact in isolation, not a defect.
- **Catalog breadth.** 51 cases across L0–L2. Every `generate` subcommand
  except the bridge-only `modify`/`test`/`bp` branches has a case, and so does
  every *option axis that selects a different code path* inside one: all nine
  `form --pattern` templates, `extension SecurityDuty`/`SecurityRole`,
  `edt --extends`, `enum --non-extensible`, `table --table-type TempDB`,
  `query --join`, `view --computed`, `report --parameter`/`--extra-dataset`,
  `privilege --data-entity`, `menu-item --kind Action`/`Output`, and
  `map --map-to` over two tables. Option axes that only vary a literal
  (a different `--label`, another `--field`) are deliberately *not* separate
  cases — they exercise no new branch.
  L4 cases (batch, workflow *runtime* submission, posting, DMF, ER, SSRS
  rendering, …) still need the runtime oracle above. See `d365fo-mcp-server`'s
  `eval/cases/` for the fuller catalog shape to port once one exists here.

## Coverage — K ∧ E ∧ T

[`COVERAGE.md`](COVERAGE.md) is generated, never hand-maintained: a leaf counts as
done only when the knowledge corpus **teaches** it, an eval case with a reviewed
golden **proves** it, and a `generate` subcommand **builds** it. Families come from
`ObjectTypeRegistry`, capabilities from `GenerateSurface`, so the report cannot
claim coverage that no longer exists. Regenerate with `eval coverage --write`; CI
runs `--check`.
