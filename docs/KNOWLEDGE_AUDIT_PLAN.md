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

1.1 ✅ **Unified object-type registry** — `Core/ObjectTypes/ObjectTypeRegistry.cs`: kind → root
element, AOT subfolder, MetaModel type, provider collection, abstract-root/`i:type` policy,
generate subcommand, MCP objectType, naming kind, plus an `ExistsInStandardAot` flag. Consumed by
`MetadataBootstrap.KindToCollection/KindToTypeName` (the net48 bridge shared-compiles the file
rather than referencing net10 Core), the `GenerateInstaller` call sites (subfolder literals →
`ObjectTypeRegistry.Folders.*` constants, so a typo is a build error), `ScaffoldFileWriter`'s
xsi/abstract-root guards, `MetadataExtractor` + `index refresh` folder lists, and
`ObjectLookup.NormalizeKind`. Parity tests cover kind/root uniqueness, the folder constants, the
bridge's 16 kinds, and — when `D365FO_PACKAGES_PATH` points at an AOS — every folder name against
a live census. `ToolCatalog`'s objectType enum stays with 2.4, which widens it.
Fallout found and fixed: three phantom folders the extractor read (`AxWorkspace`,
`AxReportSsrs`, `AxQuerySimple`), and `generate query` emitting a bare abstract `<AxQuery>` root
without `i:type="AxQuerySimple"`.

1.2 ✅ **Extend the bridge to all generated families.** Every kind `IMetadataProvider` exposes
now carries its collection in the registry — read off the interface's own property list rather
than guessed, which is how the bridge's `queryextension → QueryExtensions`/`AxQueryExtension`
mapping was found to name two things that do not exist (it is `QuerySimpleExtensions` /
`AxQuerySimpleExtension`).

The bigger half is **proof**: the bridge gained a `validateArtifact` RPC (deserialize →
re-serialize → diff, no model touched) behind `d365fo validate metadata <file|dir>`, so
"valid" stops meaning "looks right to us". Two corrections to this plan's assumptions came out
of it — the on-disk format is **DataContract**, not `XmlSerializer` (hence per-type namespaces
and member order), and validation therefore needs no write access, so it works against a live
installation without touching it.

Ten unreadable families fell out immediately and are fixed: missing contract namespace on menu
items (V1), reports (V2), workflow types (V2) and form extensions (V6); `TabStyle` value
`TOCList`, which is not in the enum; `AxSecurityPolicy` putting the table name in the `NoYes`
flag `ConstrainedTable` rather than `PrimaryTable`; and `AxEdtExtension` pinned to the
nonexistent `AxEdtStringExtension` (a discriminator the write guard *required*). All 51 goldens
now deserialize.

Deferred to 1.3 and Phase 2: routing the XML-only `generate` commands through the provider on
`--install-to` (they write to disk today, which the fixes above finally make readable), and the
remaining R4 workarounds — `AxFormDataSourceRoot`, provider-side relation writes,
never-retry-writes, read-back-after-write by default.

1.3 ✅ **Serializer-order knowledge (R2)** — and generalised: instead of porting a hand-captured
`axTablePropertyOrder`, `scripts/emit-metadata-contracts.ps1` derives the whole catalog from
`Microsoft.Dynamics.AX.Metadata.dll` (564 types, 8,820 members, namespace + order + base type)
and commits it as an embedded resource, so the knowledge covers every family and CI needs no
D365FO install. `ContractOrderCanonicalizer` applies that order on every write path — the
XDocument writer, the pre-rendered form templates, and the bridge install path, which hands XML
straight to the provider.

`XML007` (a member the type does not declare, silently dropped on read) ships as an error.
**`XML006` deliberately does not.** The strict rule "any member out of contract order is
dropped" is not supported by evidence: shipped Microsoft files deviate from contract order in
places and the provider reads them back with zero loss. Ordering is therefore enforced where it
demonstrably helps — on output — rather than asserted as a defect in files we did not write.
Getting this wrong the other way would have shipped a validator that flags Microsoft's own AOT.

Measured against the live provider, the golden catalog went from 19/51 readable-and-lossless to
**43/51**. Fixed on the way: tables lost every field group (`Fields` written before
`FieldGroups`); `AxSecurityDuty` used `PrivilegeReferences` instead of `Privileges`, discarding
every privilege; menu items emitted an invented `<Image><ImageType>` block; and every generated
class carried an `<Extends>` element that AxClass has no member for (the base class lives in the
X++ declaration, which the extractor now reads).

Remaining 8, with exact causes recorded for Phase 2: report `Datasets`→`DataSets` plus the
`AxReportDesign` subtype shape (2.1), `AxDataEntityView` data sources and field mappings (2.2),
`AxSecurityEntryPointReference.AccessLevel` → the `Grant` sub-element (2.2), and four form
details (`DataSourceLinks`, `AllowDelete`, `FrameType` on a tab page).

1.4 **Deserialization self-check without Windows.** Add an eval/CI-side structural check per
family: root element + `xsi:type` policy + property-order lint driven by the registry, so
non-Windows CI approximates what `Handlers.WriteArtifact` would reject. (True provider
round-trip stays a VM-only L3 gate — Phase 4.)

**Gate:** parity test green; on a VM, `generate <each family> --install-to --verify` returns
`Readable` for every kind in the registry.

## Phase 2 — Generation depth where the audit found it shallow

**Before any of it: the catalog was keyed wrong.** Eighteen MetaModel types serialize under a
DataContract name different from their CLR name, and the 1.3 catalog was keyed by the latter.
`AxFormDataSourceRoot` contracts to `<AxFormDataSource>` — the same name as a real *abstract*
CLR type — so every form data source resolved to five members instead of thirty.
`AxMethodPropertyCollection` writes as `<Method>`, `AxFormControlPropertyCollection` as
`<Control>`. Keying by contract name removed the subtype-substitution heuristic 1.3 had
reverse-engineered from the symptom, and made "a member this type does not declare" mean what it
says. Two gaps closed with it: member *value* types are now recorded (so a walker can descend
into `<Grant>` and `<Design>`, elements named after a member rather than a type), and the Core
assembly is scanned, where `AccessGrant` lives.

2.1 ✅ **SSRS reports (G7, R3).** `AxReport` was largely fiction: `<Datasets>` (the member is
`DataSets`), `<ReportParameters>` (parameters live in `DefaultParameterGroup`), and a hand-rolled
`AxReportTablix`/`TablixBody` tree matching no AOT type — all discarded on read, so the report
loaded with no datasets and no design. Rebuilt as `AxReportAutoDesign` with table data regions.
`generate report` now emits the whole stack: TempDB table per dataset, DP, contract,
`SrsReportRunController`, and an output menu item. Six files, all six verified lossless against
the live provider.

**RDL is deliberately not generated.** It lives in `AxReportPrecisionDesign.Text` as an escaped
document, and although precision designs outnumber auto designs in shipped code, a wrong RDL is
far harder to detect than a wrong contract — the file loads and the report renders wrongly.
Auto-design is describable in AOT terms and checkable; RDL wants its own evidence base first.

2.2 ✅ (mostly) **Menu items, security, entities, extensions.**
- Menu item: `EnumTypeParameter`/`EnumParameter`, `Parameters`, `Query`, `ConfigurationKey`,
  `LinkedPermissionObject`/`LinkedPermissionType`. **`NeededPermission` is not a menu-item
  member** — it belongs to form controls. A menu item has five independent `*Permissions` flags,
  so an access level is expanded into those, cumulatively.
- Security: `<AccessLevel>` exists nowhere in the security model. Access is six Allow/Deny
  permissions inside `<Grant>`, so every generated privilege granted nothing — and the extractor
  read the same non-member, indexing every shipped privilege with a blank access level too.
- Data entity: `<DataSources>` is not a member (they belong to the embedded `ViewMetadata`
  query); fields needed `i:type="AxDataEntityViewMappedField"` or every `DataField`/`DataSource`
  mapping was dropped; `<IsMandatory>` is `Mandatory`. Keys and `EntityCategory` added.
- `generate extension`: `view`, `query`, `dataEntityView` added. The type for a query extension
  is **`AxQuerySimpleExtension`** — there is no `AxQueryExtension` type or folder on any AOS.

Still open: duty/role depth beyond reference lists, `AxSecurityPolicy`'s
`PolicyGroup`/`Constrained*` collections, `Security{Duty,Role}Extension` property-modification
payloads, and entity relations / computed columns / staging-table emission.

2.3 ◐ **Guardrails uniformly applied (G4).** The contract rules now run on *every* write:
`ScaffoldFileWriter` refuses a document the reader would mangle, so an unknown member or an
out-of-range enum fails at the point of the mistake rather than on the AOS. It found one
immediately — `generate number-sequence` wrote `<NumberSequenceModule>` onto an EDT that has no
such member, which its golden could not catch because the case captures the extension class and
the EDT was a sibling artifact nobody validated. Still open: moving `GroundingGate` into the
shared `GenerateInstaller` path, and property-honesty reconciliation (R6).

2.4 ✅ **MCP parity (G6).** The real gap was fidelity, not coverage: the XML-only handlers
returned the raw scaffold, skipping the namespace, the member order and the shape rules the file
path applies — the same request produced a correct file through the CLI and a document no AOS
would read through MCP. Both surfaces now render through one emitter. `generate_object` gained
menu-item, privilege, duty, role, entity and extension; a `validate` tool exposes `xpp`,
`references`, `form-pattern` and `metadata-shape` — the last being the offline half of
`validate metadata`, needing no bridge.

2.5 ✅ **prepare type-awareness (G8).** `StrategiesFor` gains bespoke strategies for report,
entity and security, and derives the rest from the registry: a kind with an extension object gets
it named, and a kind without one is told so instead of being sent looking.

**Gate:** all 51 goldens readable-and-lossless against the live provider (was 43); offline lint
clean; MCP parity tests green.

## Phase 3 — Knowledge: absorb, single-source, audit

3.1 ✅ **Ported the 63-entry knowledge base (R1)** — 16 new topics (19 → 35), grouped by
domain rather than one per entry: `posting-and-financials`, `ssrs-report-authoring`,
`security-modeling`, `integration-dmf-dualwrite`, `runtime-frameworks`,
`inventory-and-warehouse`, `xpp-data-access-apis`, `forms-and-navigation`,
`transactions-and-concurrency`, `performance-and-caching`, `xpp-runtime-types`,
`number-sequence-patterns`, `workflow-authoring`, `testing-and-quality`, `analytics-and-er`,
`build-error-triage`; plus retryable/async batch on `sysoperation-batch-patterns` and table
inheritance on `table-scaffolding`. `emit-skills.py` gained a `covers:` frontmatter field
(see 3.3). `d365foErrorHelp.ts` became 11 new `XppcFixHints` rules plus the
`build-error-triage` topic, each rule back-pointing at a topic that now exists (a test
asserts that).

Every topic was audited against the live index before landing, which is why the corpus says
`SrsReportParameterAttribute`, `<DefaultAggregate>`, `SysGlobalTelemetry` and
`AxMenuElementSubMenu`/`<SubMenu>` — two casing defects in the new text were caught by the
gate rather than by review.

3.2 ✅ **Knowledge audit harness (R1)** — `KnowledgeRefExtractor` pulls every named AOT
element out of `skills/_source` (static call, extends, new, attribute, intrinsic,
declaration; markdown links, `<Slot>` placeholders, container literals and XML fences
excluded), `KnowledgeAudit` resolves them through a new `IKnowledgeSymbolLookup` that
`MetadataRepository` answers over 22 named AOT collections, and `KnowledgeExamples` routes
every example through the offline BP validator. `d365fo knowledge audit [--capture|--verify]`
runs both halves: live against a full standard index, otherwise against the committed
`eval/knowledge-audit.snapshot.json`, so CI (which has no index) still refuses an un-audited
edit. Exceptions are reviewed data in `eval/knowledge-audit.allow.json`.

The first run found and this phase fixed a real tool defect: `MigrationScriptScaffolder`
emitted `extends SysRunnable`, a type that exists in no AOT — the same class of defect the
predecessor's audit was built to catch.

3.3 ✅ **Single-sourced the rule canon (K1)** — rule blocks are fenced in the topic that
explains them (`<!-- canon:<id> -->`), and `RuleCanon` reads them from the corpus already
embedded in the assembly. *Deviation from the plan, and an improvement on it:* no generated
side-artifact is needed for the two runtime consumers (`agent-prompt`, the new MCP
`initialize` `instructions` field), so drift there cannot be represented rather than merely
being detected. The one on-disk consumer, `skills/d365fo-cli/SKILL.md`, has generated regions
written by `emit-skills.py` and is covered by CI's drift job. Its reference table was listing
19 of 35 topics; it is now generated from the `covers:` frontmatter.

The MCP *tool* descriptions turned out not to carry the X++ canon — they are tool-usage text
— so the third consumer became the server `instructions`, which is where clients want the
rules anyway: paid once per session instead of restated per tool.

3.4 ✅ **Wired skills for consumption (K2)** — `scripts/Install-D365FoClaudeSkills.ps1`
mirrors the Copilot installer for the `skills/anthropic/` variant (regenerates when empty,
prunes retired topics, leaves unrelated skills in `.claude/skills/` alone). This repo's own
`.claude/skills/d365fo-knowledge/` routes a session to the corpus, the audit workflow and the
canon rather than duplicating 35 files into the repo twice.

**Gate:** ✅ `d365fo knowledge search` lands on the new domains (posting, feature management,
dual-write change tracking, SYS10028, on-hand, aggregate measurements); ✅ `knowledge-audit`
CI job runs the snapshot half; ✅ the drift job covers agent-prompt and the MCP instructions
through `RuleCanonTests` + `AgentPromptCanonTests`, and `skills/d365fo-cli/SKILL.md` through
the widened `skills` job.

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
