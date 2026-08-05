# Implementation plan — closing the knowledge & generation gaps

Derived from [`KNOWLEDGE_AUDIT.md`](KNOWLEDGE_AUDIT.md) (findings K1–K6, G1–G9, R1–R7).
Ordering principle: fix shipping defects first, then make "valid against MetadataProvider"
provable for every family, then deepen generation where the audit found it shallow, then close
the knowledge and eval loops so the level holds. Each phase is independently shippable and ends
with a verification gate.

---

## Phase 0 — Ship-blocking fixes (small, immediate)

| # | Item | Findings | Where |
|---|---|---|---|
| 0.1 | ✅ Fix workflow type: emit `<AxWorkflowTemplate>` (**not** `AxWorkflowType` — that folder exists on no AOS) with the real property set, add `AxWorkflowApproval`/`AxWorkflowTask` element generation, point the extractor at `AxWorkflowTemplate`, update eval golden `L1-workflow-basic`, add scaffolder tests | G1 | `Scaffolding/WorkflowScaffolder.cs`, `Commands/Generate/GenerateWorkflowCommand.cs`, `Extract/MetadataExtractor.cs` |
| 0.2 | ✅ `FormPatternNormalizer`: unknown or non-generatable `--pattern` → error listing generatable + catalog-only patterns (no silent `SimpleList` fallback) | G5 | `Core/Scaffolding/FormPattern.cs` normalizer + `GenerateCommands.cs` |
| 0.3 | ✅ Corpus hygiene (+ `eval run --all`, since the gate assumed it): align `eval/corpus/schema.json` prose with reality (runs are committed), re-run the stale `L2-table-extension` record against HEAD, fix the `L1-form-basic` golden caption (`@Fleet:Vehicle`) | audit §5 | `eval/corpus/`, `eval/goldens/` |
| 0.4 | ✅ Doc/frontmatter drift — both already correct at HEAD (README says 29 and matches `CliApp.cs`; `object-extension-authoring.md` carries `appliesWhen`; `emit-skills.py` re-run shows zero drift). Docs drift found elsewhere was fixed instead: workflow AOT type in ARCHITECTURE/CAPABILITIES/EXAMPLES, `eval run` replay mechanics in `eval/README.md` | K5, K6 | `README.md`, `skills/_source/`, `docs/` |

**Gate:** `dotnet test` green; `d365fo eval run --all` replay green; workflow object now visible
to `index build` over a fixture containing it.

## Phase 1 — Single type registry + provable validity (the core)

1.1 **Unified object-type registry.** One table in `D365FO.Core` (e.g.
`Core/ObjectTypes/ObjectTypeRegistry.cs`): kind → Ax root element, concrete MetaModel type name,
AOT subfolder, bridge collection name, abstract-root/i:type policy, MCP exposure flag, naming
rule id. Consume it from `MetadataBootstrap.KindToCollection/KindToTypeName`,
`GenerateInstaller` call sites (drop the ~30 subfolder literals), `ObjectLookup`,
`MetadataExtractor` folder list, `ToolCatalog` objectType enum, `ObjectNamingRules`. Add a
parity test asserting the registry covers every `generate` subcommand and every extractor
folder (prevents the next G1). Mirrors the predecessor's dispatch-parity tests (R6).

1.2 **Extend the bridge to all generated families.** Add registry-driven kinds for
`AxReport`, `AxWorkflowTemplate`, `AxMenuItem{Display,Action,Output}`, `AxSecurity{Role,Duty,
Privilege,Policy}`, `AxService`, `AxServiceGroup`, `AxDataEntityView` extensions, so
`generate --install-to/--verify` round-trips them through `IMetadataProvider`. Port bridge
workarounds from R4 as they become relevant: abstract-type mapping table
(`AxQuery→AxQuerySimple` style), `AxFormDataSourceRoot` rule, provider-side relation writes,
never-retry-writes policy, read-back after every write (`bridgeValidateAfterWrite` equivalent —
promote `BridgeGate.TryVerifyObject` from opt-in flag to default when the bridge is up).

1.3 **Serializer-order knowledge (R2).** Port `axTablePropertyOrder` + non-existent-property
catalog as `XML006` (misordered property will be silently dropped) and `XML007`
(plausible-but-nonexistent property) in `XppValidator`; apply canonical ordering in
`XppScaffolder.Table` and `TablePattern`. Extend the same treatment to other families where
goldens reveal order sensitivity.

1.4 **Deserialization self-check without Windows.** Add an eval/CI-side structural check per
family: root element + `xsi:type` policy + property-order lint driven by the registry, so
non-Windows CI approximates what `Handlers.WriteArtifact` would reject. (True provider
round-trip stays a VM-only L3 gate — Phase 4.)

**Gate:** parity test green; on a VM, `generate <each family> --install-to --verify` returns
`Readable` for every kind in the registry.

## Phase 2 — Generation depth where the audit found it shallow

2.1 **SSRS reports (G7, R3).** Port `generateSmartReport` semantics into a
`ReportStackScaffolder`: one command emits TmpTable (TempDB) + Contract + DP + Controller +
Output menu item + AxReport **with embedded RDL 2016** (page header injection, SimpleList +
GroupedWithTotals designs, grouped tablix with subtotals). Reuse the shared EDT-resolution path
(regression R3 shows resolver and emitted `i:type` must come from one computation). Unit tests
+ new eval case `L2-report-stack`.

2.2 **Menu items, security, entities, extensions.**
- Menu item: add `EnumTypeParameter`, `Parameters`, `ConfigurationKey`, `NeededPermission`,
  `LinkedPermissionObject`, `Query`.
- Security: privilege entry-point shapes are OK; deepen duty/role beyond reference lists,
  give `AxSecurityPolicy` its `PolicyGroup`/`Constrained*` collections, make
  `Security{Duty,Role}Extension` emit real `PropertyModifications` payloads.
- Data entity: keys, `EntityCategory`, `PrimaryCompanyContext`, relations, computed columns,
  optional staging-table emission; support `AxDataEntityViewExtension`.
- `generate extension`: add `view`/`query`/`dataEntityView` kinds already known to
  `ObjectModifyEngine.ExtensionKindFor`.

2.3 **Guardrails uniformly applied (G4).** Move `GroundingGate` invocation into the shared
`GenerateInstaller` path so all 29 commands honor `D365FO_GROUNDING_ENFORCE`; port
property-honesty reconciliation (R6) into `ScaffoldFileWriter`/bridge writes (requested vs.
written diff in the result envelope).

2.4 **MCP parity (G6).** Widen `generate_object` from 11 to the full registry (registry-driven
enum), add `validate` tool exposing `xpp` + `references` + `form-pattern` modes over MCP.

2.5 **prepare type-awareness (G8).** Drive `StrategiesFor` and advertised kind lists from the
registry; add bespoke strategies for report/entity/security (the new depth areas).

**Gate:** new unit tests per family (kill the zero-test list: report, workflow,
number-sequence, migration-script, `entity --all-fields`); eval cases added for each deepened
family; MCP schema snapshot test.

## Phase 3 — Knowledge: absorb, single-source, audit

3.1 **Port the 63-entry knowledge base (R1).** Convert `xppKnowledge.ts` entries into
`skills/_source/` topics (grouped: ~10–14 new topic files, e.g. `ssrs-report-authoring`,
`security-modeling`, `posting-and-financials`, `integration-dmf-dualwrite`,
`performance-and-caching`, `runtime-frameworks`), preserving the
`{summary, migration, rules, examples}` structure. Extend `emit-skills.py` if new frontmatter
fields are needed. Port `d365foErrorHelp.ts` patterns into `XppcFixHints` rules + a knowledge
topic each.

3.2 **Knowledge audit harness (R1).** Add xUnit equivalents of `apiSymbols` +
`exampleValidation`: every API symbol named in `skills/_source` must exist in the fixture/std
index or an allowlist; every ```xpp example must pass `XppValidator` + `ReferenceResolver`.
CI-gate it. (The predecessor explicitly flags this repo as un-audited.)

3.3 **Single-source the rule canon (K1).** Generate the rule sections of
`AgentPromptCommand` output and the long MCP tool descriptions from `skills/_source` (new
emit target in `emit-skills.py`, embedded as resources like the knowledge corpus). CI drift
check covers all three consumers.

3.4 **Wire skills for consumption (K2).** Add an installer for the `skills/anthropic/`
variant (mirror `Install-D365FoCopilotSkills.ps1`), and reference the emitted skills from this
repo's own `.claude/` so sessions here load the D365FO knowledge.

**Gate:** `d365fo knowledge search` hits the new domains; knowledge-audit CI job green; drift
job covers agent-prompt + MCP descriptions.

## Phase 4 — Eval loop to predecessor parity (R7)

4.1 **CI gate:** add `eval` job running replay cases (`requires_fixture_index` subset) on every
PR; fail on golden mismatch or validator regressions.

4.2 **L3 build oracle:** VM-side `verify-goldens-build` equivalent — recompile every golden via
`xppc` one case at a time, persist `eval/golden-build-verification.json`; wire
`XppcDiagnostics` results back into corpus records.

4.3 **L4 runtime oracle:** SysTest-backed cases via existing `test run` shell-out, fixtures
provisioned per run in a throwaway model.

4.4 **Classification & clusters:** populate `classification`
(TOOL_DEFECT/VALIDATOR_GAP/KNOWLEDGE_GAP/MODEL_ERROR) on new runs, make `eval clusters` rank
them, and implement the MODEL_ERROR → `skills/_source` feedback path (K4) so knowledge gaps
found in evals become topic edits with provenance.

4.5 **Coverage taxonomy:** port the K ∧ E ∧ T model (knowledge teaches ∧ eval proves ∧ tool
builds) into a generated `eval/COVERAGE.md` with a `--check` CI mode; grow the fixture AOT
beyond one table + one class so reference resolution in evals is realistic.

**Gate:** CI red on any eval regression; coverage report shows per-family K/E/T status;
clusters command returns ranked, classified clusters.

---

## Sequencing & dependencies

- Phase 0 is independent; land first.
- 1.1 (registry) unblocks 1.2, 2.2, 2.4, 2.5 — do it before any Phase 2 item.
- 3.1/3.2 are independent of Phases 1–2 and can run in parallel.
- 4.1 should land as soon as Phase 0 stabilizes goldens; 4.2/4.3 need VM access; 4.4/4.5 close
  the loop last.

## Verification (end-to-end)

1. `dotnet build && dotnet test` — all suites incl. new parity, knowledge-audit, per-family
   scaffolder tests.
2. `d365fo eval run --all` replay green locally and in the new CI job.
3. On a Windows VM with `D365FO_BRIDGE_ENABLED`: `generate <family> --install-to <model>
   --verify` returns `Readable` for every registry kind; `build --msbuild` on a model containing
   one generated object of each family compiles clean; `bp check` reports no new BP errors.
4. MCP smoke: `generate_object` accepts every registry kind; `validate` tool returns the same
   verdicts as the CLI.
