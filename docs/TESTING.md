# Testing

Four layers, each answering a question the one below it cannot.

| Layer | Question | Cost | Where |
|---|---|---|---|
| **Unit / surface tests** | Does the code do what its author meant? | seconds | `dotnet test` |
| **Eval replay** | Does the tool still produce the artefact a reviewer approved? | ~1 min | `d365fo eval run --all` |
| **Build oracle (L3)** | Does that artefact compile? | ~30 s, needs a VM | `d365fo eval verify-build` |
| **Oracles** | Do our *rules* agree with what Microsoft ships? | ~50 min, needs a VM | `d365fo oracle sweep` |

The first two run in CI on every push. The last two need a D365FO installation, so they are run
by hand on a development VM and their results are committed
(`eval/golden-build-verification.json`, and the counts recorded in `SweepFalsePositiveTests`).

---

## Before you open a PR

```sh
dotnet test d365fo-cli.slnx -c Release                       # 1 813 tests
./scripts/check-build-warnings.ps1                           # warning ratchet
dotnet run --project src/D365FO.Cli -c Release -- eval run --all
dotnet run --project src/D365FO.Cli -c Release -- eval coverage --check
dotnet run --project src/D365FO.Cli -c Release -- knowledge audit --verify
python scripts/emit-skills.py && git status --porcelain skills/
python scripts/emit-mcp-tools.py --check
```

That is exactly what CI runs, in the same order. If you touched a scaffolder, add the VM layers:

```sh
dotnet run --project src/D365FO.Cli -c Release -- eval verify-build     # goldens must compile
dotnet run --project src/D365FO.Cli -c Release -- oracle sweep --dry    # rules vs the fixtures
```

---

## The suites

`tests/D365FO.Core.Tests` (1 193) covers the index, the extractor, the scaffolders, the
validator and the metadata contracts. `tests/D365FO.Cli.Tests` (620) covers the command surface:
that every command's settings type can be constructed, that the manifest and the app agree, that
the MCP adapter and the CLI cover the same ground.

A few carry more weight than their size suggests:

| Test | What it prevents |
|---|---|
| `CommandSurfaceTests` | A command that compiles, registers, appears in `--help` and dies on call with "Could not resolve type". All seven `remove-*` commands shipped that way once. |
| `CliMcpParityTests` | A command reachable from one surface and not the other, without a written reason. |
| `ObjectTypeRegistryAotTests` | A registry row that disagrees with the AOT on this machine — folder name, root element, namespace. |
| `MetadataContractsAotTests` | A contract claim the shipped files contradict — it samples up to 12 documents per AOT folder across the installation, and it is what withdrew the member-order rule. |
| `SweepFalsePositiveTests` | A validator rule that fires on shipped X++, each pinned with the count it once accounted for. |
| `SchemaIndexCoverageTests` | An index column that queries filter on and no index covers. |

---

## The eval loop

`eval/cases/*.json` are 60 reviewed cases. Each replays its `canonical_args` through a real
`d365fo` child process against a disposable index, and diffs the result against a golden captured
on a VM — plus the companions the command writes beside it.

```sh
d365fo eval run --all              # every case; non-zero exit on any mismatch
d365fo eval run L1-table-basic     # one case
d365fo eval coverage               # the K ∧ E ∧ T taxonomy: knowledge, eval, tool
```

Goldens are **captured, never hand-authored** — `d365fo eval capture <case> --actual <file>` after
a human has read the XML. A golden somebody typed is a test of what we imagined, and the whole
point of the corpus is to be a test of what the tool does. See
[AGENT_EVAL_LOOP.md](AGENT_EVAL_LOOP.md) for the full contract, and `eval/README.md` for the
day-to-day commands.

---

## The oracles

The suite proves the code does what its author expected. The oracles ask whether what the author
expected matches the platform:

```sh
d365fo oracle sweep --dry        # every rule over the checked-in fixtures — runs in CI
d365fo oracle sweep              # every rule over a whole PackagesLocalDirectory
d365fo oracle probe <file.xml>   # compile one artefact with the real xppc
d365fo oracle census AxTable     # what shipped XML actually carries, member by member
d365fo oracle runtime            # is the SysTest runner wired to a database, and does it discriminate?
```

**The bar for `sweep` is zero errors on Microsoft's own X++.** Not "few". A rule that fires on
shipped code teaches its reader to ignore findings, which costs more than the rule earns. The
first full run reported 4 674 errors on 242 858 files and every one was ours; the bar has held
since. See [CAPABILITIES.md](CAPABILITIES.md#oracles-oracle--measuring-the-tool-against-the-platform).

`oracle runtime --negative-control` is the one worth internalising: "all tests passed" is also
what a runner prints when it ran nothing, so a suite that has never reported a deliberate failure
is not evidence of anything.

---

## CI

| Job | What fails it |
|---|---|
| `build-test` (ubuntu · windows · macos) | Any test on any platform. |
| `warning-baseline` | Warning count rising above the ratchet — **or dropping below it** without lowering the baseline. |
| `published-cli` (win-x64 · linux-x64) | The published, trimmed, single-file shape behaving differently from an ordinary build. It has caught what `dotnet test` could not (#182). |
| `eval` | A golden mismatch, or a stale `eval/COVERAGE.md`. |
| `knowledge-audit` | A knowledge claim naming a symbol no index resolves. |
| `skills` | An emitted skill drifting from `skills/_source`, or frontmatter no YAML parser can read. |
| `mcp-tool-map` | `docs/MCP_TOOLS.md` stale, or the two surfaces disagreeing about which tools exist. |

Nothing here is advisory. A gate that can be ignored is a gate that will be.

---

## Writing a test in this repository

- **Name the defect, not the method.** `A_local_function_shadows_the_predefined_name` says what
  broke; `TestCheckBuiltinArity3` says nothing.
- **Assert the positive half too.** Every false-positive fix in `SweepFalsePositiveTests` sits
  beside a test that the rule still fires on the code it exists for, so "fixing" a rule by
  deleting it fails.
- **Ground it in the installation when you can.** `ObjectTypeRegistryAotTests` and
  `MetadataContractsAotTests` read the real package tree (17 003 `Ax*` folders on the machine this
  was written on) and skip cleanly when there is none. A fact checked against what Microsoft ships
  beats one checked against a fixture somebody wrote.
- **A fixture is reference material, not scenery.** `tests/Samples/MiniAot` is XML this tool would
  accept; `oracle sweep --dry` holds it to the same bar as the platform.

---

## See also

- [AGENT_EVAL_LOOP.md](AGENT_EVAL_LOOP.md) — the eval corpus, the golden contract, the L3 oracle.
- [NEW_TOOL_CHECKLIST.md](NEW_TOOL_CHECKLIST.md) — what a new command has to satisfy.
- [ARCHITECTURE.md](ARCHITECTURE.md) — what the projects are and how they fit.
