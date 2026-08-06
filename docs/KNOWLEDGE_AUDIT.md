# Knowledge & Generation Audit — D365FO / X++ competency and MetadataProvider-valid object generation

Date: 2026-08-05. Scope: complete audit of (a) the D365FO/X++ knowledge embedded in this
repository (skills, knowledge base, agent guidance), (b) the object-generation surface and its
validity guarantees against the Microsoft MetadataProvider library, and (c) the eval loop that
keeps both honest. A companion implementation plan derived from this audit lives in
[`KNOWLEDGE_AUDIT_PLAN.md`](KNOWLEDGE_AUDIT_PLAN.md).

---

## 1. Executive summary

The solution's two load-bearing competencies — deep D365FO/X++ knowledge and the ability to
generate AOT objects that are valid against `Microsoft.Dynamics.AX.Metadata` — are real but
uneven:

- **Knowledge** is single-sourced (19 topics in `skills/_source/`, CI-guarded fan-out to
  Copilot/Anthropic/CLI variants, embedded into `D365FO.Core` as the `knowledge` command corpus).
  However the *rule canon* is triplicated by hand (skill bundle, `agent-prompt`, MCP tool
  descriptions) with no drift check, the corpus is command-heavy and X++-snippet-light
  (21 `xpp` blocks total), and the emitted Anthropic skills are not wired into this repo's own
  `.claude/`.
- **Generation** covers 29 CLI subcommands and ~27 artifact families, but validity against the
  MetadataProvider is only *proven* for the subset that reaches the Windows bridge
  (`generate --install-to` / `--verify`): 16 kinds in `MetadataBootstrap.KindToCollection`.
  Reports, workflows, menu items, all security types, services and number sequences are written
  as raw `XDocument` output with no round-trip proof. Four divergent type registries exist; one
  known divergence bug ships today (workflow `AxWorkflow` vs. the real `AxWorkflowTemplate`).
- **Validation** is strongest for forms (FP001–FP010 + repairer + 37-pattern catalog) and tables
  (XML001–XML005), and absent for every other XML family. The X++ validator is regex-based BP
  linting plus index-backed reference proving — there is no parser and no in-process compiler;
  compile truth arrives only post-hoc via `xppc` log parsing.
- **Eval loop** is healthy at L0–L2 (31 cases, 31 goldens, 39 committed runs) but has no CI gate,
  no L3/L4 tier, `classification` is null on every corpus record, and the corpus schema
  contradicts reality about gitignoring.
  *Closed 2026-08-06 in Phase 4, except the L4 runtime tier: the catalog replays in CI, `eval
  verify-build` compiles every golden with `xppc` on a real installation (51/51 clean, zero
  unattributed diagnostics; the shipping defects it found — in `generate query`,
  `generate event-handler`, `generate migration-script`, `generate business-event`,
  `generate report`, `generate entity`, `generate form`, all nine form patterns,
  `generate custom-service`, `generate workflow` and `generate number-sequence` — are
  fixed, along with four mis-authored cases), replay runs now carry a triage hypothesis, and `eval/COVERAGE.md`
  reports K ∧ E ∧ T per family and per `generate` capability.*
- **The predecessor `d365fo-mcp-server`** contains battle-tested material this repo has not yet
  absorbed — see §7.

The single highest-leverage theme: **make bridge round-trip (deserialize → provider save → read
back) the definition of "valid", extend it to every generated family, and backfill offline
validators from what the round-trip teaches.**

---

## 2. Knowledge assets — findings

### 2.1 What exists (strong)

| Asset | Location | State |
|---|---|---|
| Single-source topic corpus | `skills/_source/*.md` — 19 topics, 2,639 lines | ✅ single fact, one place |
| Emission pipeline | `scripts/emit-skills.py` / `.ps1`; CI job `skills` enforces zero drift | ✅ |
| Copilot variant | `skills/copilot/*.instructions.md` (19) + `Install-D365FoCopilotSkills.ps1` | ✅ deployable |
| Anthropic variant | `skills/anthropic/*/SKILL.md` (19) | ⚠️ emitted, no installer, not wired locally |
| CLI skill bundle | `skills/d365fo-cli/SKILL.md` + `references/*.md` | ✅ umbrella canon, 200 lines |
| Embedded knowledge base | topics embedded as resources; `Knowledge/KnowledgeBase.cs` (section-aware search); `d365fo knowledge list|get|search`; MCP `get_knowledge` | ✅ |
| Agent system prompt | `Commands/Agent/AgentPromptCommand.cs` (413 lines): prepare→generate→validate loop, 13 X++ rules, select grammar, CoC rules, 8 workflow templates | ✅ rich |
| Compiler-error knowledge | `Validation/XppcFixHints.cs` — 11 weighted rules, each back-pointing to a knowledge topic | ✅ |
| Eval agents/commands | `.claude/agents/eval-{runner,improver,author}.md` + `.claude/commands/` | ✅ |

### 2.2 Gaps

- **K1 — Triplicated rule canon, no drift check.** `AgentPromptCommand.cs`,
  `skills/d365fo-cli/SKILL.md`, and `D365FO.Mcp/ToolCatalog.cs` descriptions each restate the
  X++/workflow rules independently. CI guards only `skills/_source` → emitted variants.
- **K2 — Anthropic skills not consumable.** No installer for `skills/anthropic/`; no
  `.claude/skills/` in this repo; a Claude session here gets eval agents but zero D365FO
  knowledge unless it manually reads `skills/_source`.
- **K3 — Corpus is command-heavy, X++-light.** 57 `sh` blocks vs 21 `xpp` blocks; only 5
  `[learn:*]` Microsoft Learn anchors. Topics teach *how to drive the CLI* more than *how to
  write correct X++/metadata* — thin exactly where generation is weakest (reports, security,
  entities, workflow).
- **K4 — No knowledge-feedback loop.** `AGENT_EVAL_LOOP.md` §12 plans MODEL_ERROR →
  `skills/_source` feedback tooling; unbuilt. Corpus `classification` is null on all 39 runs, so
  KNOWLEDGE_GAP clusters can't be ranked.
- **K5 — Frontmatter inconsistency.** `skills/_source/object-extension-authoring.md` lacks
  `appliesWhen` in-source while its emitted variant carries one.
  *Re-verified 2026-08-05 (Phase 0.4): already fixed at HEAD — the source carries `appliesWhen`,
  and re-running `emit-skills.py` produces zero drift across all three targets.*
- **K6 — Stale counts.** README claims 26 generate subcommands; `CliApp.cs` registers 29.
  *Re-verified 2026-08-05 (Phase 0.4): already fixed at HEAD — README says 29 and its command
  list matches the 29 names `CliApp.cs` registers, one for one. The remaining "26–27" figures in
  the README are MCP tool-schema counts, which are correct.*

---

## 3. Generation surface — findings

### 3.1 Coverage matrix (condensed)

Full detail per type in §3 of the exploration record; condensed status:

| Family | Generate | Bridge install/verify | MCP `generate_object` | Dedicated validator | Unit tests |
|---|---|---|---|---|---|
| Table, Class, CoC, EDT, Enum | ✅ | ✅ | ✅ | XML001–005 / COC / writer guards | ✅ strong |
| Form (9 patterns) + DS/control methods | ✅ | ✅ | ✅ | FP001–FP010 + repairer | ✅ strong |
| Query, View, Map | ✅ | ✅ | partial | writer guards only | ✅ |
| Data entity | ✅ shallow | ✅ | ❌ | ❌ | 3 tests |
| Report (AxReport+DP+Contract) | ✅ shallow | ❌ | ❌ | ❌ | **0 tests** |
| Menu item | ✅ minimal | ❌ | ❌ | ❌ | via snapshots |
| Security (priv/duty/role/policy) | ✅ asymmetric | ❌ | policy XML-only | ❌ | partial |
| Workflow | ✅ **wrong folder** | ❌ | ❌ | ❌ | **0 tests** |
| Custom service, business event, sysoperation, runbase, systest, migration, numberseq | ✅ | classes only | partial XML-only | ❌ | mixed; numberseq/migration **0** |
| Extensions (Table/Form/Edt/Enum/Duty/Role) | ✅ | partial | ❌ | ❌ | 8 tests |
| Labels | ✅ | ❌ | ✅ | key sanitizer | ✅ |

Not generatable at all: `AxMenu`, `AxTile`, `AxWorkspace` (AOT object), `AxConfigurationKey`,
composite/aggregate data entities, `AxKPI`, `AxPerspective`, `AxResource`,
`AxDataEntityViewExtension`/`AxViewExtension`/`AxQueryExtension` (known to modify layers but not
to `generate extension`), `AxLabelFile` manifest.

### 3.2 Validity against MetadataProvider — how it actually works

- No compile-time reference to `Microsoft.Dynamics.AX.*` anywhere; the net48
  `D365FO.Bridge` late-binds the assemblies from `D365FO_BIN_PATH` via reflection
  (`MetadataBootstrap.cs`), tolerating PU drift (3 factory fallbacks, 2 config ctor shapes).
- The only true "MetadataProvider validity" check is `Handlers.WriteArtifact`:
  `XmlSerializer(Ax*Type).Deserialize(xml)` + provider `Create/Update`, plus
  `BridgeGate.TryVerifyObject` read-back after `generate --verify`.
- The bridge has **no `validate*` RPC** — Microsoft's own validation surface is never invoked.
  *Closed 2026-08-05 in Phase 1.2: the bridge gained `validateArtifact` (deserialize +
  re-serialize + diff, no model touched) behind `d365fo validate metadata`. It also corrected a
  wrong assumption in this audit — the on-disk format is **DataContract**, not `XmlSerializer`:
  each type declares its own namespace (`AxTable` none, `AxForm` V6, `AxMenuItem*` V1,
  `AxWorkflow*`/`AxReport` V2) and its own member order, and the serializer silently drops
  elements that are unknown or out of order.*
- Off-bridge (macOS/Linux/CI), "validity" degrades to `ScaffoldFileWriter` guards
  (abstract-root, `xmlns:i`, CLR-bool) + per-family offline validators where they exist.

### 3.3 Defects and structural risks

- **G1 — Workflow folder/type bug.** `GenerateWorkflowCommand.cs:77` installed to `AxWorkflow`
  and the scaffolder emitted `<AxWorkflow>`; `MetadataExtractor.cs:231` read `AxWorkflowType`.
  Ground truth (a real AOS: 0 `AxWorkflowType` folders, 211 `AxWorkflowTemplate` folders across
  packages) says **both** names are wrong — the workflow type is `AxWorkflowTemplate`, with
  `AxWorkflowApproval` / `AxWorkflowTask` as separate element objects. The property set was
  invented too (`DocumentTableName`, `DocumentMenuItemType`, `WorkflowDocumentClass` do not
  exist; the real names are `Category`, `Document`, `DocumentMenuItem`,
  `SubmitToWorkflowMenuItem`, `SupportedElements`). Fixed 2026-08-05 in Phase 0.1, with the
  emitted shape locked by `WorkflowScaffolderTests`.
- **G2 — Four divergent type registries.** Bridge `KindToCollection`/`KindToTypeName` (16),
  ~30 hard-coded subfolder literals in `Commands/Generate/*`, `ObjectLookup` (15 read kinds),
  `MetadataExtractor` (30 folders). No single source of truth; G1 is the first visible casualty.
  Fixed 2026-08-05 in Phase 1.1: `Core/ObjectTypes/ObjectTypeRegistry.cs` is the single table,
  shared-compiled into the net48 bridge. Consolidating it surfaced three more phantom folders
  (`AxWorkspace`, `AxReportSsrs`, `AxQuerySimple` — read by the extractor, present on no AOS)
  and one shipping defect: `generate query` emitted a bare `<AxQuery>`, but `AxQuery` is an
  abstract MetaModel base and every shipped file is `<AxQuery i:type="AxQuerySimple">`, so the
  metadata reader would reject every generated query.
- **G3 — Bridge kind set ⊂ generate set.** Report, workflow, menu item, security*, service,
  label manifest never reach the provider, so their XML is never proven deserializable.
  Fixed 2026-08-05 in Phase 1.2: every family the provider exposes now has its collection in
  the registry (read off `IMetadataProvider`'s own property list), and `validate metadata`
  proves deserializability for all of them. Proving it immediately found **ten unreadable
  families**: menu items, reports and workflow types written without their contract namespace;
  `AxFormExtension` likewise; a `TableOfContents` form carrying `TabStyle` value `TOCList`,
  which is not in the enum; `AxSecurityPolicy` putting a table name into the `NoYes` flag
  `ConstrainedTable` instead of `PrimaryTable`; and `AxEdtExtension` pinned to
  `i:type="AxEdtStringExtension"` — a type that exists in no D365FO build, which the write
  guard *required*. All fixed; all 51 goldens now read back.
- **G4 — Grounding gate applied to only 3 of 29 generate commands** (coc, extension,
  event-handler). The anti-hallucination token from `prepare` is optional everywhere else.
- **G5 — Form pattern silent fallback.** `FormPatternNormalizer.Normalize` mapped any unknown
  `--pattern` (incl. catalog-known but non-generatable ones like `Wizard`, `FormPart*`) to
  `SimpleList` instead of erroring. Fixed 2026-08-05 in Phase 0.2: `TryNormalize` resolves
  through `FormPatternCatalog` and rejects catalog-only names (naming the generatable variant
  parent where one exists); CLI and MCP both return `BAD_INPUT`.
- **G6 — MCP surface lags CLI.** `generate_object` accepts 11 of ~27 types; no
  `validate_xpp`/`resolve_references` tool over MCP; view/map/entity/report/security/menu-item
  not exposed.
- **G7 — SSRS depth.** No RDL/precision design, tablix-only, no chart/matrix, no bridge, no
  tests. AxReport XML shape is plausible but unproven.
- **G8 — prepare is type-blind.** `StrategiesFor()` has bespoke advice only for
  table/class/form/map; advertised kind lists differ between `prepare change` (10) and
  `prepare create` (13) and are unenforced.
- **G9 — XML BP rules are AxTable-only** (XML001–005). No structural rules for form (beyond FP),
  EDT, enum, entity, report, query, view, map, security.

---

## 4. Validation & compiler integration — findings

- Offline: `XppValidator` (SEL/COC/BP/XML rules, regex-based), `ReferenceResolver`
  (index-backed identifier proving, 7 violation kinds), `FormPatternValidator`+`Repairer`,
  `ObjectNamingRules`, `lint` (16 index-wide categories, SARIF).
- Data-driven rules (XML002–005) mine `PropertyStats` from the customer's own AOT with a 0.8
  threshold — a good pattern worth extending to more properties/families.
- Compiler: no in-process parser/compiler; `XppcDiagnostics` parses `xppc` logs post-hoc,
  `XppcFixHints` maps messages → hint + knowledge topic; `build`/`bp check`/`test run`/`sync`
  shell out on the VM.
- Enforcement: `FormPatternGate` on by default; `GroundingGate` opt-in via env and only on 3
  commands; eval `EvalScorer` re-runs XppValidator+ReferenceResolver against goldens.

## 5. Eval loop — findings

- 31 cases (L0: 2, L1: 18, L2: 11), 31 reviewed goldens, schema-checked by `EvalCaseCatalog`;
  39 committed corpus runs, 37 replay / 2 agent.
- Open items: no `eval` job in CI; `classification` null everywhere → `eval clusters` idle;
  corpus prose said runs are gitignored while they are tracked (fixed 2026-08-05 in Phase 0.3,
  along with adding `eval run --all`); one stale corpus record predated
  the XML001 table-extension fix at HEAD; no L3 (build/xppc oracle) or L4 (runtime/SysTest) tier;
  fixture AOT (`tests/Samples/MiniAot`) contains only one table + one class, limiting
  reference-resolution realism.

---

## 6. Test coverage — findings

~500 test attributes across 55 files; strong on scaffolding snapshots (84), table patterns (71),
form patterns (42+25+25), XppValidator (30), ReferenceResolver (28). **Zero dedicated tests** for
report, workflow, number-sequence, migration-script generators and `entity --all-fields` — the
exact families that also lack bridge proof and validators (compounding risk: three nets missing
at once).

---

## 7. Reuse from `d365fo-mcp-server` (the debugged predecessor)

Audited at `dynamics365ninja/d365fo-mcp-server` v1.8.0 (TypeScript MCP server, 26 unified tools,
195 Vitest files, net48 C# bridge over `IMetadataProvider`, 80-case L0–L4 eval harness). It is
substantially *ahead* of this repo in several of the exact areas §3 flags as weak. Absorbable
assets, in value order:

- **R1 — Curated X++ knowledge base (63 entries).** `src/tools/xppKnowledge.ts` (3,400+ lines):
  structured `{id, keywords, summary, ax2012→d365fo migration, rules, examples, related}`
  entries covering ~44 domains this repo's 19 topics do not (posting engine, financial
  dimensions, SSRS reports, print management, dual-write, DMF, warehouse, ER, SysDa, OCC,
  caching, datetime/timezones, .NET interop, table inheritance, …). Plus
  `d365foErrorHelp.ts` — a pattern-matched compiler/runtime error catalog with fix code.
  Critically, the predecessor *audits* its knowledge: `apiSymbols.test.ts` +
  `exampleValidation.test.ts` prove that symbols named in entries exist and examples pass the BP
  validator — and its own `eval/README.md:179` explicitly flags that this repo's skill files
  have never had that treatment.
- **R2 — AOT XML serializer-quirk knowledge.** *Absorbed 2026-08-05 in Phase 1.3, and
  generalised: rather than porting the predecessor's hand-captured `axTablePropertyOrder`, the
  whole contract catalog is derived from `Microsoft.Dynamics.AX.Metadata.dll` (564 types,
  namespace + member order + base type) and committed as an embedded resource, so it covers
  every family and needs no D365FO install to use. `XML007` (member the type does not declare)
  ships; a strict order rule does not — shipped Microsoft files deviate from contract order and
  the provider still reads them losslessly, so order is enforced on output instead of asserted
  as a defect elsewhere.* `src/utils/axTablePropertyOrder.ts`: the AxTable
  deserializer **silently drops misordered property elements** (file looks right, validators
  pass, xppbp later fails with `BPErrorTableTitleField1NotDeclared`). Canonical property order
  captured from a VM-built golden, plus `AX_TABLE_NON_EXISTENT_PROPERTIES` (plausible-but-fake
  property names with corrective messages), enforced as rules **XML006/XML007** — both absent
  from this repo's `XppValidator`.
- **R3 — SSRS report stack.** `generateSmartReport.ts` (64 KB): one call emits TmpTable
  (TempDB) + Contract + DP + Controller + Output menu item + AxReport **including CDATA-embedded
  RDL 2016** with page-header injection and grouped-tablix/subtotals support; carries two
  regression-locked EDT-resolution bugs (`:1337`, `:1387`). This is exactly what §3.3-G7 lacks.
- **R4 — Bridge/MetadataProvider workarounds.** `bridge/D365MetadataBridge/` (9.8 kLOC):
  `Main()` JIT-loading trap (assembly resolve must be installed before any D365FO type is
  referenced); relations must be written *through the provider* to get serializer element order
  (`MetadataWriteService.cs:2262`); top-level form datasource must be `AxFormDataSourceRoot`
  (`:1138`); `AxQuery`→`AxQuerySimple` abstract-type mapping; table extensions have two relation
  collections; `ResolveModelSaveInfo` for real model Id/SequenceId; **writes never retried**
  (may already have applied), reads retried after health-checked respawn;
  `bridgeValidateAfterWrite` re-read after every write. Also the documented split: 13 types go
  through `IMetadataProvider` (`BRIDGE_CREATE_TYPES`), the rest through purpose-built XML
  writers because the provider's property channel can't express them — a rationale this repo's
  bridge/scaffolder split should mirror deliberately instead of accidentally.
- **R5 — Form engine extras.** Catalog-as-data with `xmlAliases`, `referenceForms`,
  `lifecycleGuidance`, per-pattern method stubs; `formCloner.ts` (clone a Microsoft reference
  form and re-bind datasources, string-level by design — XML round-trips corrupt AOT files);
  `fieldControlTypes.ts` (control type from the field's real `i:type`, not EDT-name
  heuristics); mined-usage cross-check (`crossCheck.ts`) that reports catalog gaps after every
  index build; the `<DataGroup>` ⇒ sibling `<DataSource>` trap (full build fails, incremental
  passes).
- **R6 — Guardrail mechanics.** HMAC-signed grounding tokens (stateless verification for a
  write-only companion); property-honesty reconciliation (`createTablePropertyHonesty.ts` —
  requested vs. actually-written diff, catching the silent-drop defect class); per-`.rnrproj`
  mutex; agentic-loop detection; dispatch-parity tests between duplicated dispatchers.
- **R7 — Eval maturity.** 80 cases L0–L4 (vs. 31 L0–L2 here), runtime SysTest oracle,
  golden re-compilation via xppc one-case-at-a-time (`verify-goldens-build.ts`, 57/65 green),
  coverage taxonomy (78 leaves) defining done as **K ∧ E ∧ T** (knowledge teaches ∧ eval proves
  ∧ tool builds), CI-gated; improver toolchain (`eval:clusters/report/knowledge/flakes/mine`).
- **Caveat:** ~25 source comments cite `docs/eval-sweep-findings-2026-07-21.md` (findings
  #1–#38) which is absent from the repo; recovering it would unlock the reasoning behind the
  regression locks.

---

## 8. Risk ranking (what bites first)

1. **G1** workflow bug — ships wrong artifacts today.
2. **G2/G3** registry divergence + unproven families — silent invalid XML for
   reports/security/workflow/menu items, the exact types the user calls out (SSRS aj.).
3. **Eval not in CI** — regressions in either competency land unnoticed.
4. **K1/K4** — knowledge drift and no feedback loop erode the "deep knowledge" claim over time.
5. **G5/G4** — silent fallbacks defeat the guardrail design.
