# Changelog

All notable changes to `d365fo-cli` are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Versioning

This repository has no release tags yet, and the assembly carries no explicit version, so
entries below are grouped by the date the work landed on `main` rather than by a version
number that does not exist. `d365fo version` reports the assembly version. Once the first
tag is cut, this file switches to [Semantic Versioning](https://semver.org/spec/v2.0.0.html)
and the dated sections become the pre-1.0 history.

Upstream references of the form "upstream 1.15.0" point at the sibling
[`d365fo-mcp-server`](https://github.com/dynamics365ninja/d365fo-mcp-server) release the work
was ported from.

---

## [Unreleased]

### Added — the ten `generate` subcommands the coverage report was waiting on

The K∧E∧T report had ten AOT families with no generator, and each was marked "surface work,
not corpus work". This is the surface work: **43 `generate` subcommands** (was 33), every new
shape measured on a live installation with `d365fo oracle census` before it was written, and
every golden compiled by the real `xppc`.

- **`generate configuration-key`** — `AxConfigurationKey` with label, parent key, license code;
  `EnabledByDefault` is written only as the opt-out `No`, as 472 shipped keys do.
- **`generate form-part`** — `AxFormPart` (`Name`, `Caption`, `Form`: the three members all 222
  shipped parts carry). The hosted form is a required symbol.
- **`generate label-file`** — `AxLabelFile` manifest for one language plus the `.label.txt` it
  points at under `LabelResources/<lang>/`; `--entry Key=Text` seeds the content. The file id
  is refused when no `@File:Id` token could name it.
- **`generate menu`** — `AxMenu` with sub-menus (`--submenu`), menu items placed by path
  (`--item Vehicles/FMVehicle[:Action|Output]`), tiles and menu references. Element names are
  the collection's keys, so a duplicate in one container is refused rather than silently halved.
- **`generate resource`** — `AxResource` manifest; `--source` copies the file into
  `ResourceContent/<Type>/`. A manifest whose content is missing is a **Metadata Error** at
  compile time ("Resource content file 'X.png' not found"), which the first L3 run proved.
- **`generate tile`** — `AxTile` bound to a menu item; `Type` written only when not `Standard`,
  a KPI tile needs `--kpi`. The menu item is checked against the index and reported in
  `unknownMenuItems` rather than refused.
- **`generate workflow-category`** — `AxWorkflowCategory` (V2 namespace) whose `--module` is
  validated against `ModuleAxapta` in the index; the contract types it as a string and the
  platform does not.
- **`generate composite-entity`** — `AxCompositeDataEntityView`: root references with embedded
  entities bound by a relation on the child; `[CompositeDataEntityView]` on a declaration that
  does not extend `common`.
- **`generate aggregate-entity`** — `AxAggregateDataEntity` over a measurement: read-only, the
  five automatic field groups, and mapped fields whose members are written in the
  `Microsoft.Dynamics.AX.Metadata.V2` namespace with the `d3p1:` prefix every shipped file
  carries — unprefixed, the reader keeps the field and drops every mapping on it.
- **`generate extension Map`** — `AxMapExtension`, the shell the contract declares (`Name`
  only; the installation ships no instance).

Around them:

- **Eval corpus 60 → 70 cases**, coverage **83 of 83 leaves** (was 64 of 74; nine new
  capability rows joined the table). Goldens captured on this VM through the real CLI; all 70
  compile clean under `eval verify-build`.
- **Content companions.** A golden's `_companions/` may now hold non-XML files under
  sub-folders mirroring their place beside the artefact (`LabelResources/en-US/*.label.txt`,
  `ResourceContent/Images/*.png`); the L3 provisioner copies them into the artefact's AOT
  folder, because the compiler checks that the content a manifest names exists.
- The eval replay's child process now runs from the repository root, so canonical args may
  name files the repository ships (`--source eval/seeds/ConFmLogo.png`).
- `PropertyHonesty` skips `--entry`: its value lands in the `.label.txt`, and the manifest
  cannot carry it.
- Every new subcommand is also a `generate_object` objectType over MCP (XML-only, like the rest
  of that surface); `label-file` and `resource` hand back the content file's path — and, for
  labels, its text — because the manifest is one file of two. `docs/MCP_TOOLS.md` regenerated:
  187 command routes.

### Fixed — what the census said about menus

`forms-and-navigation` taught that a sub-menu is nested through a `<SubMenu>` member and that
`AxMenuElementMenuReference` was "a different, legacy concept". Counted on the installation:
of 81 shipped menus, 60 nest sub-menus **inline** — `AxMenuElementSubMenu` carries its own
`<Elements>` — one references another menu through `AxMenuElementMenuReference/<MenuName>`,
and **no file carries a `<SubMenu>` member**; the contract declares none. The topic now says
what the files say. The tile size list lost its `Small` (the `TileSize` enum has none) and
`object-extension-authoring` no longer claims there is no Map extension kind.


### Fixed — the two 1.16.0 ports the parity audit found still missing

Auditing the repository against the gap analysis, with each of its twelve upstream fixes probed
on the live index rather than ticked off the list, found two that no wave had ported:

- **A multi-word search answered nothing.** `search class "ProcessGuide AdjustIn"` returned
  `count: 0` while `InventProcessGuideAdjustInController` sat in the index and the exact-name
  search found it (upstream c43bf51). No AOT name contains a space, so a multi-word query never
  meant one verbatim string; it means "a name carrying every token", in any order. Every
  name-search method — classes, tables, EDTs, enums, queries, views, data entities, reports,
  services, workflow types, maps, business events, security policies, configuration keys, tiles,
  workspaces — now goes through one `NameFilter`, one `LIKE` per token ANDed; where a method
  matches several columns (a data entity's public names) every token must land in the same
  column. Label search is deliberately untouched: label text has spaces, and a multi-word label
  query is verbatim.
- **An extension's name was invisible to the collision check.** `prepare create
  NumberSeqModule.Kitting --type enum` answered "No collision — does not exist in the index"
  for an enum extension the installation ships, immediately before a write, and in the same
  answer listed 25 sibling extensions spelled exactly that way while flagging the dot as
  `INVALID_CHARS` (upstream 0b363e5). An extension row records the object it *extends*, so it
  lived in none of the tables `SymbolKinds` consulted. It now reports as `<kind>-extension`, the
  collision verdict says to modify the existing extension or pick another suffix, and a dotted
  name under a base type is judged by the naming rules as the extension it is.

Also brought back in line with the code: the README's token-economics section still carried the
~1 800-tokens-per-turn estimate wave 06 had measured to be off by six times, and its adapter
tool count (28) and `generate` command list (29 of 33) had drifted.


### Added — wave 06: the ten missing documents, and two of them generated

The documentation set upstream has and this repository did not. Two are not written but
**generated**, because a document that restates what the code does is a document that will
disagree with it:

- **`docs/MCP_TOOLS.md`** — the map between the adapter's tools and the commands behind them,
  emitted by `scripts/emit-mcp-tools.py` from two live sources: the command manifest
  (`d365fo schema --full`) and the adapter's own `tools/list`. The generator **refuses** to write a
  mapping where one side names something the other does not have, so it is a parity gate as much
  as a docs gate; CI runs it with `--check`. Upstream's version of this file was a copy, and a copy
  cannot notice.
- **`docs/TOKEN_ECONOMICS.md`** — rewritten around measurements from
  `scripts/measure-context-cost.py` instead of estimates. The estimate it replaces was wrong by six
  times: the document claimed ~1 800 tokens/turn for a 20-tool MCP surface; the adapter actually
  advertises **32 tools** whose minified schemas are **40 132 characters** — ~11 100 tokens — in
  context on every turn, against the CLI one-skill layout's **366**. Characters are measured;
  tokens are stated as approximate with the divisor named, because this repository cannot honestly
  measure another model's tokenizer.

The other eight, each grounded in commands run against a live installation while writing:

- **`QUICK_START.md`** — five minutes to the first answer, with `SETUP.md` for the branches.
- **`USAGE_EXAMPLES.md`** — whole tasks end to end, where `EXAMPLES.md` is one call per command.
  Every sequence was executed; where the output taught something it is quoted, including the two
  traps worth knowing: `prepare change CustTable` resolves the *form* unless you pass `--type`,
  and `CustTable` already carries 98 CoC extensions.
- **`TESTING.md`** — the four layers (unit, eval replay, build oracle, oracles), what each catches
  that the one below cannot, and every gate in the order CI runs it.
- **`NEW_TOOL_CHECKLIST.md`** — what a new command must satisfy, with the defect behind each rule.
- **`KNOWLEDGE_AUTHORING.md`** — one source, three emitted layouts, and the audit that proves the
  corpus names nothing that does not exist.
- **`CUSTOM_EXTENSIONS.md`** — declaring which models are yours, and what that answer changes; the
  two-package-tree (UDE) setup.
- **`MCP_CONFIG.md`** — client configuration, the three transports, and the `indexReachable: false`
  failure that accounts for most MCP-only trouble.
- **`SETUP_AZURE.md`** — the shared read-only deployment, which `src/D365FO.Mcp/Program.cs` has
  been pointing at since before it existed.

### Fixed

- Two documents linked to `SETUP.md#ude-unified-developer-experience-setup`, a heading that is not
  there; both now point at the section of `CUSTOM_EXTENSIONS.md` that covers it.
- `AGENT_EVAL_LOOP.md` still described a 51-case catalog. It is 60, and all 60 goldens compile.

### Added — wave 05: oracles, and what the first one found

Five `oracle` commands that measure this tool against the installation it claims to understand,
plus the corpus work that the measurement made possible. None is published over MCP: they are a
measurement of `d365fo-cli`, not an operation on anyone's X++.

- **`oracle sweep`** — every rule over every AOT file in a PackagesLocalDirectory. The bar is
  **zero errors on Microsoft's own X++**, not "few": a rule that fires on shipped code teaches a
  caller to ignore findings. `--dry` sweeps the checked-in fixtures so CI without an installation
  can still run it; findings are tallied per rule with a few samples each, because an
  installation is a quarter of a million files and keeping every finding to group at the end
  costs gigabytes and says nothing the counters do not.
- **`oracle census` / `oracle members`** — what shipped XML actually carries, member by member,
  against what the metadata contract declares. This is where compiler and metadata facts come
  from instead of from guessing.
- **`oracle probe`** — compiles one artefact (or a handful) with the real `xppc.exe` in a
  throwaway model. An xppc that rejects its argument list prints usage, which parses as "no
  diagnostics", so that case is reported as a failure of the probe rather than as a pass.
- **`oracle runtime`** — whether `SysTestConsole.exe` is wired to a database (its keys are in the
  AOS `web.config`, so the answer is derivable rather than guessable, and `--configure` copies
  only the missing ones, keeping a `.bak`), and `--negative-control`, a test class whose three
  methods pass, fail and throw on purpose. "All tests passed" is also what a runner prints when
  it ran nothing.

### Fixed — every rule that fired on Microsoft's own code

The first full sweep of a live installation (242 858 files, 107 241 X++ blocks) reported
**4 674 errors**. Every one was the validator being wrong. `SweepFalsePositiveTests` pins each
with the count it accounted for, alongside the code the rule exists for — so a "fix" that
silences a rule fails there rather than passing quietly.

- **XML007, 4 574 findings.** A member name that is also a type name was read as the type. The
  catalog has exactly two such collisions; one of them is `DataSource`, declared by
  `AxQueryExtensionEmbeddedDataSource` as an `AxQuerySimpleEmbeddedDataSource` while an unrelated
  three-member type of that name exists — so every real data-source property under it was
  reported as unknown. What the parent declares now beats what the name coincides with.
- **XML007 again, 1 463 in one model.** The rule descended into documents whose root it did not
  recognise, which is how the BP suppression list every model ships got judged against the
  metadata contracts. It now stops at an unknown root, as its own documentation always claimed,
  and skips `IgnoreDiagnostics` explicitly: those entries really do carry members
  `AxIgnoreDiagnosticItem` does not declare (reflected off the assembly — it has seven), and the
  file is read by the best-practice tooling rather than by the metadata serializer.
- **XML002 / XML003, 229 in one model, downgraded to warnings.** Both are majority conventions
  mined from standard models, and the minority is Microsoft's own. A convention most artefacts
  follow is not a defect in the ones that do not.
- **ATTR001, 72 findings.** Masking blanks a comment's content *and* its closing delimiter, so
  `[SysSetupConfig(true /*ContinueOnError*/, 600)]` reached the literal test as the argument
  `true /*`.
- **SEL010, 15 findings.** `validTimeState` is also a method on the query classes and a parameter
  name on their declarations; the rule is about the select clause and now only looks there.
- **FN001, 7 findings.** `new Info()` constructs the class of that name — a predefined function
  is never reached through `new`.
- **CS001, 3 findings.** A file may alias a CLR type into scope (`using string = System.String;`),
  which is what the platform's Commerce pricing classes do; `string groupKey` is then a
  declaration of that alias, not the C# keyword.
- **SEL008, 1 finding.** A select inside a `#localmacro` has no terminating semicolon, so the
  statement scan ran into the next macro and paired one macro's `where` with the next one's
  `order by`.
- **COC003, 1 finding.** The `_Extension` suffix is now matched case-insensitively, as X++
  identifiers are — the platform ships `JournalCheckPostIV_extension`.
- **FN001 again, 2 findings.** X++ allows a function to be declared inside a method body, and it
  takes precedence over the predefined name of the same spelling — which the compiler's own
  message says ("nor a previously defined local function"). `BatchRun.runJob` declares
  `void info()` and calls it twice.
- **RPT001, 1 finding.** An abstract data provider is a base for concrete ones and carries no
  contract of its own; `CustVendAdvanceInvoiceDP` reads `parmDataContract()` and declares none,
  because `CustAdvanceInvoiceDP` and `VendAdvanceInvoiceDP` each declare their own.

Re-swept end to end after the last fix: **242 858 files, 107 241 X++ blocks, zero errors** —
the bar holds on Microsoft's own X++, in 48 minutes. The 41 473 warnings are counted and left
alone; they are style rules that shipped code legitimately breaks, and the bar was never about
them.

### Added — the eval corpus reaches the artefacts nothing was checking

- **Companions are scored.** A report is a report plus a TempDB table, a DP, a controller and an
  output menu item; only the main artefact used to be diffed, so companions were captured once
  and never checked again. Turning it on immediately found ten companions across the two report
  cases that no golden had pinned. A companion the golden names and the run did not produce is
  missing; one the run produced and the golden does not name is extra.
- **Cases can start from an artefact.** `generate table-relation` and `generate find-methods`
  augment a table that already exists and refuse `--out` outright, so neither was reachable by a
  replay. A case may now name an `apply_to_seed` under `eval/seeds/`, which the replay copies in
  as its output file and merges into.
- **Eight new cases**, goldens captured on this VM through the real CLI, taking the corpus from
  52 to 60: the workflow approval/task elements, the service group, the deprecated `simple-list`
  alias, a report dataset extension, view/query/data-entity extensions, and the two table
  augmenters above.
- **Three families gained the command that builds them.** `AxViewExtension`,
  `AxQuerySimpleExtension` and `AxDataEntityViewExtension` were reported as having no generator
  while `generate extension` was building all three.
- **Knowledge for twelve leaves the corpus taught nothing about**: the remaining extension kinds
  and their root elements, security duty/role extensions, `AxView` and `AxMap` with the commands
  that build them, the plain `generate class`, the two form-method scaffolders and `form-clone`.

Coverage: **64 of 74 leaves** are now K ∧ E ∧ T, up from 42. The remaining ten
(`AxAggregateDataEntity`, `AxCompositeDataEntityView`, `AxConfigurationKey`, `AxFormPart`,
`AxLabelFile`, `AxMapExtension`, `AxMenu`, `AxResource`, `AxTile`, `AxWorkflowCategory`) are
blocked on a `generate` subcommand that does not exist yet — surface work, not corpus work.

### Fixed — the rest of the 1.16.0 ports
The remainder of the upstream fixes the gap analysis lists under wave 04, each checked against
this codebase before being ported: a fix for a defect we do not have is a change with no reason.

- **A rule could read the document's root out of a comment.** The XML rules asked whether the
  TEXT contained `<AxTable`, so `<!-- this was an <AxTable> before it was rewritten -->` above an
  `AxClass` made every table rule fire on it — a finding naming a rule the document cannot break.
  `XmlScan` reads the root by scanning tokens, skipping the prolog, comments, CDATA and
  processing instructions; the six rules that guessed from text now ask it. Deleting the comments
  first and re-scanning is the other tempting shape and is worse: it mutates the document being
  judged, and a `-->` inside CDATA takes the deletion with it.
- **`prepare create` on an extension asked about the wrong object.** Preparing
  `CustTable.FleetExtension` reported "no collision, nothing similar" — true of a name that by
  definition does not exist yet, and useless. It now grounds in the object being extended: does
  it exist, which model owns it, what already extends it. A base that is not in the index is a
  note rather than a veto — the index is a mirror of what was extracted, and the metadata
  provider may well see the object.
- **`get form` says when the file holds members the reader drops.** The reading half of the
  silent-drop defect class: a form that reads as missing a datasource is indistinguishable from
  one that never had it, and the caller goes looking for the wrong bug.

### Not ported, and why
- **Multi-line attribute blocks dropped on create** — this repository has no path that splits a
  supplied X++ class body into methods, and `ExtractSignatureLine` already tracks bracket depth.
- **Member list printing the signature instead of the name** — names and signatures are separate
  columns here; verified against a real class, not assumed.
- **The scaffold writing controls the platform cannot see** — the write path canonicalises member
  order before writing; checked by generating all five form patterns and validating each.
- **A form-order validation rule** was written and then **withdrawn**. Run against the
  installation it flagged files Microsoft ships: an `AxEnum` with `ConfigurationKey` after
  `UseEnumValue`, an `AccessGrant` with `Create` after `Update`, an `AxTableFieldString` with
  `Label` after `StringSize` — all shipped, all loading. The contract's member list is
  declaration order and the deserializer is more tolerant than that, so ordering what we write
  stays and calling a document broken for its order does not. `MetadataContractsAotTests`, the
  repository's own census over 10 028 shipped files, is what caught it; the reasoning is recorded
  on `ContractOrderCanonicalizer` so the rule is not rediscovered and re-added.

### Fixed — what the wave-04 audit found
Auditing the wave against the gap analysis turned up four defects, one of them in the wave's own
new code.

- **`labels update` wrote one language's text into every language file.** Targeting `en-us,cs`
  with a single value replaced the Czech translation with the English string and reported
  success — data loss dressed as a correction. A correction *is* a translation, so several
  languages now need one `--text <LANG>=<VALUE>` each, and a single text for several languages is
  refused with the arguments to pass instead.
- **A search that read nothing reported a clean zero.** `find refs` on an index built elsewhere
  (no source index, no source paths on this machine) answered `count: 0` — indistinguishable from
  a codebase where the symbol is genuinely unused, which is how "no callers" becomes "safe to
  delete". `SourceRefResult` now carries `searched` and a caveat, so `find refs`, the
  `find_references` tool and the three `analyze` modes all say the same thing.
- **`validate name` called the only extension-name shape Microsoft ships illegal.** The character
  rule rejected the dot in `CustTable.FleetExtension` as an invalid character while the kind rule
  in the same run recommended exactly that form. Counted on this host: of the **1 093**
  `AxTableExtension` objects in the installation, **1 093** use `Base.Suffix` and none uses
  `Base_Extension`. Extension kinds now accept one dot separating base from suffix; a second dot,
  a space, or a dot on a non-extension kind still fail.
- **A label file could be created under an id nothing can reference.** `--label-file "Con Fleet"`
  wrote `Con Fleet.en-us.label.txt` and reported success, but no `@File:Id` token can name it, so
  every use of the label resolves to nothing. Both surfaces now refuse an id that is not a
  referenceable identifier.
- Documentation the wave had left behind: `labels update` and `verify` were absent from
  CAPABILITIES, and none of the five new commands had an entry in EXAMPLES.

### Added — wave 04: the rest of the surface
The gap analysis's fourth wave: the MCP tools that had no CLI counterpart, plus the two write
verbs whose absence forced a workaround.

- **`analyze patterns` / `analyze implementations` / `analyze api-usage`** — learn from the
  installation rather than from training data. `patterns` takes a scenario in the words the code
  would use and returns the APIs that code reaches for, ranked by how many DISTINCT objects use
  each (one verbose class must not make its own habits look like the codebase's); it reads whole
  method bodies, because the line that mentions "number sequence" is a comment and the
  `NumberSeqGlobal` call that answers the question is three lines below it. `implementations`
  answers "who else declares this, and with what signature" — the question before writing an
  override or a CoC wrapper. `api-usage` separates construction from static calls from
  declarations, and says so when an API is never constructed, which means a `new` is the wrong
  shape.
  All three report **what they could actually read** in `coverage`: a search over method bodies
  returns nothing when nothing matches, and also when the corpus was unreadable — no source
  index, no source paths on this machine — and reporting the second as an empty result is how
  "no callers" comes to mean "unused" for something used everywhere.
- **`labels update` / `labels (action=update)`** — correct the text of a label that already
  exists. Distinct from `create --overwrite` on purpose: create writes a new entry when the key
  is absent, so a mistyped key in a correction produces a *second* label and reports success.
  Correcting a typo used to mean delete plus create, which loses the entry's position in the file
  and the comment block attached to it.
- **`verify` / `verify_project`** — do the model on disk and its `.rnrproj` agree? An object the
  project does not list is never compiled: the AOT does not see it and nothing anywhere reports
  that. A project entry whose file is gone fails the build with a path rather than a reason.
  A project with no explicit item list is not judged, because some project shapes glob and
  calling every file unregistered there would be a wall of false findings.
- `MetadataRepository.FindMethodDeclarations` — the inverse of `FindMethod`: every object that
  declares a method of this name. Read through a raw reader, because for a literal column on an
  EMPTY result set Dapper infers `byte[]` and refuses to materialise the record — the same trap
  `SearchMethodSource` documents for the FTS columns, and one that fails only when there are no
  rows.

### Fixed — the CLI and the MCP server had drifted apart
- **`modify_object` reached four of the engine's twenty operations, and mis-applied the rest.**
  An unknown `action` fell back to `SetProperty`, so `action="add-index"` did not fail — it set a
  property named after the index and reported `ok: true`. The whole structural write surface the
  CLI gained (`add-index`, `add-relation`, `add-field-group`, `add-delete-action`, `rename-field`,
  the seven `remove-*` forms, the query ranges and the privilege entry points) was unreachable over
  MCP, and asking for it did something else. The tool now resolves the action through
  `ObjectModifyEngine.TryParseOperation` — the same map the sub-commands use — and refuses what it
  cannot resolve, naming the twenty alternatives. It also gained `operations[]`, the batched form:
  one bridge round trip, one journal entry, and no intermediate state published to disk.
- **`d365fo schema` published 130 of the 200 commands the app registers.** The manifest is what an
  agent reads to find the tool, so `modify`, `knowledge`, `bp-moniker`, the table/report/mobile
  pattern catalogs and eleven `generate` sub-commands were, for that reader, not shipped. It was
  also wrong in both directions: it claimed `object_patterns (action=analyze)`, which no tool
  dispatched, and denied that `validate xpp` is `validate(mode=xpp)`, which it is.
- **`modify batch` refused the operation its own error message recommends.** The batch parser
  derived the enum spelling from the step name, which works for every operation whose command name
  matches its enum name and fails for the one that does not — `property` is `SetProperty`.

### Added — the index can be refreshed from either surface
- **`d365fo index sync <TARGET>` / `index_sync`** — re-index ONE model, named directly or by a
  path to any file inside it. This was the gap the previous wave recorded rather than closed: a
  write this tool makes refreshes the index itself, but an edit made in Visual Studio, by a git
  pull or by a colleague left the index quietly lying about that model, and the only repair was a
  full walk of every package — minutes, and not the shape of a call anyone waits on.
  A model is the unit and not a file on purpose: `ApplyExtract` replaces a model's rows
  atomically, which is what makes re-extraction idempotent, so handing it a single object would
  not add that object — it would delete every other object of that model. Measured on this
  repository's own host: `ApplicationCommon` (302 classes, 39 tables, 2 070 labels) re-indexes in
  12 s cold and under a second warm, and a second sync leaves the row counts unchanged.
  `EnumerateModelDirs` and `ComputeFingerprint` moved to Core with it, so `index refresh` and
  `index sync` cannot disagree about what a model is or whether one has changed.

### Added — the work an agent does after a write
- **`sdlc`** — build, sync, run SysTest and run xppbp over MCP. The CLI could answer "does what I
  just wrote compile?" and the MCP server could not, which leaves the one surface a remote agent
  uses able to generate code and unable to find out whether it is right. The build returns
  per-diagnostic X++ compiler findings (object, member, line, column, message, fix hint) rather
  than a log tail, and says when xppc reports stale symbols — which needs a Full Build, not a
  retry. The test action reads its verdict from the runner's XML result document, because a run
  that dies half way still exits 0 with its remaining cases marked pending. Local-only: the tool
  is in `LocalTools`, so a shared read-only deployment neither advertises nor accepts it.
- **`delete_object`** — remove an AOT object through the provider or from disk, journaled so
  `undo_last_modification` can put it back. The pre-image is captured before the delete and a
  delete that cannot capture one is refused.
- **`validate(mode=metadata)`** — the metadata provider's own verdict on a document: what it
  deserialises to, and every member it drops on the way in. MCP had only the offline half
  (`metadata-shape`), which cannot see a property the type does not declare.
- **`get_workspace_info(changes=true)`** — the uncommitted X++ diff plus the cheap rule pass over
  it, mirroring upstream. `d365fo review diff` was shell-only.
- `BridgeGate` moved into Core, where it always belonged: two Core engines had been *copying* its
  bridge-option builder rather than depending upwards, so the bridge could be configured
  differently depending on which entry point reached it. Three copies became one.
- The parity harness now requires a published command with no MCP route to say **why** in
  `CliMcpParityTests.CliOnly`. An empty route list read as "nothing to say" rather than
  "decided" — which is how the whole `modify` surface came to be shell-only without anyone
  choosing it. The remaining honest gap is recorded there: `index extract`/`refresh` walk the
  package tree for minutes, and the MCP-appropriate form is a per-file upsert that does not exist
  yet.

### Fixed — grounding was enforced on one surface of two
- **`D365FO_GROUNDING_ENFORCE=true` did nothing over MCP.** The gate that proves every generated
  identifier against the index, runs the offline BP validator and demands an object-bound
  `prepare` token lived beside the CLI's generate commands, so it ran on every CLI write and on
  no MCP write at all. A deployment that turned enforcement on had turned it on for one of its
  two front doors — and the difference was invisible, because an ungated write looks exactly
  like a successful one. `GroundingGate` moved to Core; `generate_object` now takes a
  `groundingToken`, returns what the gate saw in `grounding`, and refuses the write when
  enforcement is on and the token is missing or bound to a different object.

### Added — the sixteen scaffolds MCP could not produce
- **`generate_object` covers all thirty-three `generate` sub-commands.** It had seventeen object
  types; the CLI has thirty-three, so an agent that had picked the MCP surface could not scaffold
  a report, a workflow, a view, a map, a SysTest, a custom service, a number sequence, a
  migration script, an event handler, a form clone, a report extension, the table augmenters or a
  form method — and the only sign of it was `Unknown objectType` for something the tool does.
  Added: `view`, `map`, `systest`, `migration-script`, `custom-service`, `number-sequence`,
  `workflow`, `report` (the whole SSRS stack — AxReport + DP + contract + TmpTable + controller,
  with `preProcess`, `controllerType=print-mgmt` and `uiBuilder`), `report-extension` (the three
  compiler-checked ways to extend a shipped report), `event-handler`, `find-methods`,
  `table-relation`, `form-clone`, `datasource-method` and `control-method`. They are XML-only —
  each document comes back by name, which is the shape the existing multi-document handlers use
  and the one that works off a D365FO VM.
- `CliMcpParityTests` gained the matching invariant: a `generate` sub-command with no
  `generate_object` objectType fails the build.

### Added — what the MCP server was missing
- **`analyze(mode=completeness)`** — the one analysis that reads the developer's working tree
  rather than the index, previously CLI-only.
- **`object_patterns` gained three domains and an action**: `domain=table`, `domain=report` and
  `domain=mobile-app` (list + spec), and `domain=form, action=analyze` — the mined half of the
  form toolkit. The tool used to answer these with "not backed here, use the CLI", for catalogs
  that are pure in-process data.
- **`get_knowledge(kind=bp-moniker)`** — validate, search and render a `_BPSuppressions.xml`
  block against names extracted from a real installation.
- **`get_object_info(objectType=security-policy)`**.

### Added — what the CLI was missing
- **`d365fo find extensions --merged`** — the effective merged table schema (base fields, indexes,
  relations and field groups with every extension folded in, each member labelled with the object
  that contributes it). It was reachable over MCP and from nowhere on the CLI, which is the wrong
  way round for a repository whose CLI is the primary surface.

### Added — the check that keeps them together
- **`CliMcpParityTests`** walks Spectre's own registration tree and the MCP tool catalog and fails
  the build when a command is published in neither the manifest nor a declared exclusion, when a
  manifest entry names a command or an MCP route that does not exist, when an MCP tool is reachable
  from no command, or when an `ObjectModifyEngine.Operation` is missing from either surface. Both
  inventories were hand-written and both had gone stale; deriving the ground truth from the
  registration itself is the only thing that has ever kept them honest.
- Six implementations moved into Core so the two surfaces share them rather than agreeing by
  inspection: `CompletenessAnalyzer`, `FormPatternMiner`, `ExtensionAnswers`, `BpMonikerAnswers`,
  `PatternCatalogAnswers`, `BatchStepParser`.

### Added — grounded knowledge catalogs
- **`d365fo bp-moniker`** — `validate`, `search`, `suppress`, `extract`. Every Best-Practice
  moniker has been guessed wrong at least once, and nothing about the spelling separates the real
  `BPErrorPrivilegeNotCoveredByDuty` from the entirely plausible `BPCheckNamingConventions`; a
  suppression naming a moniker that does not exist suppresses nothing while looking deliberate.
  So the catalog never infers — a name is real because an installation declared it.
  Two sources, extracted from `PackagesLocalDirectory` and shipped as a snapshot so the answer
  works with no install: canonical names from every model's `AxRuleSet/*.xml`, and message text
  from the resx resources embedded in `bin/BPExtensions/*.dll`. Measured on this repo's own host:
  144 rule sets, **409 canonical monikers**, 17 rule assemblies, 726 entries of which 724 carry
  real message text. The 317 non-canonical entries are resource strings belonging to the upgrade
  and form-conversion tooling — kept, because their text is searchable, but flagged
  `canonical: false`, since presence in the catalog is not what makes something a rule.
  Lookup is deliberately **case-sensitive** (xppbp and the suppression reader match exactly), and
  a case-only miss is answered with the right casing rather than a flat "no".
  `D365FO_BP_CATALOG_PATH` points at a snapshot matching an instance's own D365FO version.
  The resources are read through `PEReader` rather than by loading the assemblies — loading a
  D365FO rule assembly drags in its dependency graph for the sake of a string table.
- **Five knowledge topics**, taking the corpus from 38 to 43. Each claim was checked against
  this installation before it was written, and the ones worth stating are the ones a plausible
  guess gets wrong:
  - `document-attachments` — `DocumentManagement::attachFile` takes **nine** arguments, the last
    optional. The eight-argument call therefore COMPILES, and a value meant as notes lands in
    `_attachmentName`: the attachment is stored under the note text, nothing fails, and it only
    shows when someone opens the attachment list. `DocuRef`/`DocuValue`/`DocuType` are **tables**
    while `DocumentManagement`/`DocuAction` are classes; the link is `RefTableId`/`RefRecId`/
    `RefCompanyId` (verified field names — `RefDataArea` also exists and is not the one).
  - `global-class-statics` — `Global` declares **375** static methods, of which only **34** are
    also compiler predefined functions and therefore callable bare; the other 341 need `Global::`.
    In the other direction `strFmt`, `today`, `setPrefix`, `curUserId`, `conPeek`, `funcName` and
    `fieldId2Name` are predefined functions that `Global` does NOT declare, so `Global::strFmt(…)`
    does not compile however natural it reads.
  - `system-objects` — the kernel types no AOT file declares, why the index cannot carry them, and
    why `validate references` degrades an unknown declared type to a warning. Checked: `xRecord`,
    `Common`, `xSession`, `xApplication`, `xGlobal`, `Args` and `ClassFactory` have no AOT file at
    all, while `xUserInfo` — which looks like the same family — is an ordinary `AxClass`.
  - `report-print-destinations` — `SRSPrintDestinationSettings` (68 methods) with plain accessors:
    `printMediumType()`, `fileFormat()`, `fileName()`. `parmPrintMediumType()`, `toFile()`,
    `toScreen()` and `lockDestinationProperties()` read like the API and do not exist.
    `SRSPrintMediumType` has six values and `PDF` is not one of them — PDF is an
    `SRSReportFileFormat`. Set the destination BEFORE `startOperation()`, with
    `parmShowDialog(false)`, or the run has already resolved it.
  - `rdl-design-expressions` — the `=Fields!`/`Parameters!` language, aggregate SCOPE (a total
    with no scope argument is correct in every single-group test and wrong in a page footer), and
    that `IIf` evaluates both branches, so guarding a division in the result still divides by zero.
- **`d365fo mobile-pattern list|spec`** — warehouse scanner screens, seven recipes across the two
  frameworks. The list leads with the framework DECISION rather than the recipes, because it is
  the one choice that cannot be taken back cheaply: the same screens are built by ProcessGuide
  (controller, step, page builder, data processor, navigation agent, action — each behind an
  abstract factory, each an extension point) and by the legacy `WhsWorkExecuteDisplay` hierarchy
  (all of it in one class with a `displayForm()` per mode). Picking wrong is a rewrite.
  Counted here rather than recalled: the `ProcessGuide` package holds 382 classes, of which
  `ProcessGuidePageBuilder` has 75 subclasses, `ProcessGuideStep` 74,
  `ProcessGuideNavigationAgent` 40, `ProcessGuideController` 25, `ProcessGuideAction` 23; the
  abstract `WhsWorkExecuteDisplay` has 64 subclasses in ApplicationSuite/Foundation alone.
  Two recipes deliberately ask for **no code at all** — a screen's title, icon and menu placement
  are warehouse configuration, and GS1 barcode splitting is barcode configuration. Writing a class
  for the first or a `parseBarcode()` for the second is the mistake those recipes exist to prevent.
  `MobileAppRecipesAotTests` resolves every named class against a real installation.
- **`d365fo report-pattern list|spec`** — the seven SSRS shapes as implementation recipes:
  object roster, base classes, the one `generate report` call that produces the stack, what still
  has to be written by hand, and the checks worth running. `generate report` could already build
  every one; what was missing was the layer that says WHICH — and getting it wrong is expensive in
  a way a form-pattern violation is not, because there is no pattern XML to validate a report
  against. A pre-processed requirement built on `SRSReportDataProviderBase` simply behaves wrong
  in batch with nothing failing anywhere.
  Every base class was **counted in this installation** rather than recalled —
  `SrsReportRunController` 152, `SRSReportDataProviderBase` 95,
  `SrsReportDataProviderPreProcessTempDB` 88, `SrsReportDataContractUIBuilder` 59,
  `SrsPrintMgmtFormLetterController` 6 — and each recipe names shipped reference objects to read
  instead of working from prose. `ReportRecipesAotTests` resolves all 11 of them against a real
  `PackagesLocalDirectory` and asserts each extends the base its recipe claims; it is inert where
  there is no install, rather than passing vacuously.
- **`d365fo table-pattern list|spec`** — the decision layer over `generate table`. The patterns,
  their canonical `TableGroup` and their default fields already existed, but only as a value
  `--pattern` accepted: an agent choosing a shape could not see what the choices were or what each
  implied, so it guessed a name (one wasted round trip on the refusal) or skipped `--pattern` and
  got a table with no `TableGroup` at all.

### Added — the structural half of `modify`
- **Seventeen new `modify` sub-commands**, taking the write surface from 5 operations to 22:
  `add-index`, `add-relation`, `add-field-group`, `add-delete-action`, `rename-field`,
  and the removals `remove-field`, `remove-index`, `remove-relation`, `remove-field-group`,
  `remove-delete-action`, `remove-enum-value`, `remove-control`. Before these, an agent that
  wanted to add an index to an existing table had no path through the CLI at all and fell back
  to editing AOT XML by hand — which walks straight past the grounding, form-pattern and
  reference gates the tool exists to enforce. Each goes through `ObjectModifyEngine`, so each
  inherits the extension fallback (never overwrite a model this installation does not own),
  the journalled pre-image (`d365fo undo` reverts it), and contract-order canonicalisation.
- Guards that cost nothing and save a build cycle: `add-index` refuses a field the table does
  not declare (kernel fields excepted), is unique unless `--allow-duplicates`, and rejects
  `--allow-duplicates` alongside `--alternate-key`; `add-delete-action` accepts only the four
  actions the platform defines; `add-relation` pins the concrete
  `AxTableRelationConstraintField` discriminator, without which the metadata reader throws on
  the abstract base; `rename-field` rewrites the indexes, field groups and relation constraints
  that name the field, and says plainly that X++ `fieldStr()`/`fieldNum()` references are not
  rewritten.
- `D365FoErrorCodes.MemberNotFound` for the collection members the removals address.
- **`modify` reaches every kind the bridge can write, not five.** `SupportedKinds` was a
  hand-written set of `class, table, edt, enum, form` while the bridge resolves its collections
  from `ObjectTypeRegistry.BridgeCollections()`, which names **41** — so `modify property`
  refused a query, a privilege, a menu item, a service group, a view, a report and a tile the
  layer underneath was perfectly able to write, and the refusal named the five as if they were
  the platform's limit. Same drift as the extension-type lookup in #171, same fix: derive it.
  An unknown kind now names the near misses rather than printing all 41.
- **`modify add-query-range` / `remove-query-range`** for AOT queries. The datasource is optional
  when the query has exactly one; with two or more the command refuses and lists them, because a
  range on the wrong datasource returns the wrong rows silently instead of failing.
- **`modify add-entry-point` / `remove-entry-point`** for security privileges, writing the six
  independent permissions the security model actually has — there is no `AccessLevel` member, and
  writing one produces a privilege that grants nothing while reading as deliberate.
- **`modify batch`** — several changes to one object in a single read-edit-write, from
  `--operations` JSON or `--operations-file`. Adding a field, an index over it and a field group
  showing it was three bridge round trips, three journal entries, and two intermediate states
  published to disk (one of them a table carrying a field no index covers). Batched, the object
  moves from one valid state to the next in one write, and a refused step discards the batch with
  **nothing written** — the answer says so, because "step 3 of 5 failed" otherwise reads like a
  half-applied change.
- **`get table --merged`** and `extension_info(mode="table-merge")` now return the effective
  schema: base fields, indexes, relations and field groups with every TableExtension folded in,
  each member labelled with the object contributing it. An extension whose file cannot be read is
  **reported**, not skipped — a merge missing a contributor answers "that field does not exist"
  with the same confidence as a complete one. Verified against the shipped `VendInvoiceInfoTable`:
  126 fields, 11 of them from 4 extensions.

### Added
- **`prepare test` resolves a TABLE, not only a class.** A table method is named the way a
  developer says it — `d365fo prepare test CustTable.validateWrite` — and the dotted form is
  split before resolution. The symbol index stores *declared* members only, so a table that has
  never overridden `validateWrite` has no row for it, which is exactly the method the caller
  came for; the inherited kernel data methods are now listed from `TableDataMethods` beside the
  table's own overrides, each labelled with where it comes from. The answer carries the scaffold
  call already spelled `--table`, and the shape a table test needs that a class test does not:
  arrange a buffer with `initValue()` rather than selecting a row, assert the verdict **and** the
  infolog line the rule writes, and keep the accepting case beside the rejecting one — without it
  a rule that refuses every row passes its own test. Write-path methods (`insert`/`update`/
  `delete`) get a transaction and an assertion against a re-read instead of the buffer.
- `TableDataMethods.All`, `.IsWritePath()` and `.IsVerdictMethod()` — the kernel data-method
  catalog is now enumerable, not only addressable by name.

### Fixed — found by running against the live installation
These five were invisible to a green suite: every one needed a real `IMetadataProvider`, and
three of them were wrong about what Microsoft's own AOT actually writes.

- **The bridge could not read a table at all.** `readObjectXml` serialised with
  `XmlSerializer`, which reflects a type eagerly and refuses anything implementing
  `IEnumerable` without `Add(object)` — Microsoft's `AccessGrant` does exactly that, so every
  artifact transitively holding one (`AxTable`, `AxSecurityPrivilege`, `AxMenuItem*`) failed
  with *"There was an error reflecting type 'AxTable'"*. The write path had the identical
  fault. `modify` therefore never worked against a real installation for the kinds people use
  most. Both now use `DataContractSerializer`, which is what the `validateArtifact` path
  already used and already explained: the MetaModel types are DataContract-annotated and that
  contract is what the on-disk format encodes.
- The read path reported only the outer exception, so *"error reflecting type 'AxTable'"* was
  undiagnosable. It chains the inner exceptions now, as the validate path already did.
- **Every `modify remove-*` command failed on invocation.** `ModifyRemoveSettings` was
  `abstract`, and Spectre.Console.Cli constructs a command's settings by reflection — so all
  seven compiled, registered, and appeared in `--help`, then died with *"Could not resolve type
  'ModifyRemoveSettings'"*. Engine tests could not see it because they call the engine, not the
  command. `CommandSurfaceTests` now walks all 175 registered settings types.
- **A delete action was written naming nothing.** `AxTableDeleteAction` declares
  `Name, DeleteAction, Relation, Table, Tags` — the related table is `<Table>`. The tool wrote
  `<RelatedTable>`, which the contract does not have, so the serializer dropped it and a
  `Cascade` rule with no target reached disk while the write reported `ok`. The extractor read
  the same wrong element, which is worse: measured on this repo's own host, the index held
  **0 delete actions across 214 packages** and `get table` answered `deleteActions: []` for
  every table in the installation.
- **The index held 4,896 queries and zero datasources.** Shipped queries use
  `AxQuerySimpleRootDataSource` and `AxQuerySimpleEmbeddedDataSource`; the extractor matched
  `AxQuerySimpleDataSource` / `AxQueryDataSource`, which occur in no shipped query at all. Only
  the sample fixtures use the shorter name, which is exactly why the suite stayed green. Matching
  now keys on the `DataSource` suffix, which also keeps `…DataSourceField`, `…Range` and
  `…Relation` out — a prefix match would have swept those in.

### Fixed
- **`modify` never canonicalised member order before writing back.** The disk write path has
  always done it (`ScaffoldFileWriter.Finalize`) but the bridge path serialised the edited
  document as-is. `DataContractSerializer` — which is what reads AOT XML — matches children in
  contract order and *skips* anything arriving out of turn: it is not rejected, it is dropped.
  So an edit that had to create a missing collection (`<Fields>` on a table that had none,
  `<EnumValues>`, `<Controls>`) appended it after a later-ordered sibling, the write came back
  `ok`, and the change was silently absent. `modify property` had the mirror problem, inserting
  every property directly after `<Name>` — far too early for most of the AxTable contract. Both
  are covered by tests that assert the written element order.
- **A doc comment was reported as a method's signature.** `ExtractSignatureLine` skipped blank
  lines and attribute blocks but not comments, so any method opening with `/// <summary>` — or a
  `//` note, or a `/* … */` banner — stored that line as its exact declaration. Measured on the
  shipped `SalesTable`: **362 of its 621 methods** had an unusable signature; after the fix, zero.
  This is worse than an empty signature, because `prepare change`, `get table` and `get class`
  present it as the declaration a CoC wrapper has to match exactly, and a green build cannot
  correct it. `IsStatic` and the inferred return type were derived from the same line and were
  wrong with it. Block comments that close on the declaration line are handled.
  **An index built before this fix keeps the bad signatures — re-run `d365fo index extract`.**
- `extension_info(mode="table-merge")` promised an "effective merged schema" and returned the
  extension roster. Returning a list under a contract that says "merged" is worse than returning
  a list, because a caller that trusts it reads a missing field as a field that does not exist.
  The merge is now computed — see `TableMergeAnalyzer` under Added.
- README claimed 310+ tests (1 373), 19 skills (38), 29 `generate` commands (33), 27 adapter
  tools (28), and enumerated 19 of the 38 topic files. The skill listing is now generated from
  `skills/_source`, so it cannot drift again silently.

### Security
- **`Microsoft.Data.Sqlite` 8.0.10 → 10.0.11**, which moves the transitive `SQLitePCLRaw`
  stack from 2.1.6 to 2.1.12 and clears **GHSA-2m69-gcr7-jv3q** (high severity). NuGet had
  been printing `NU1903` on every build and nothing was watching. The bump also aligns the
  package with the `net10.0` target. Verified against the shape people actually install:
  `scripts/smoke-published-cli.ps1 -Rid win-x64` passes, including the bundled native SQLite
  in the trimmed single-file binary that issue #182 was about.

### Changed — CI
- **Warning-count ratchet** (`scripts/check-build-warnings.ps1`, baseline 117). The count may
  fall freely; any rise fails the run with the new warnings grouped by code. A hundred-odd
  standing warnings are exactly how `NU1903` stayed invisible.
- **The test logger no longer collides.** Both assemblies wrote `test-results.trx` into one
  directory, so the second overwrote the first — and when a run went red, the surviving file
  was usually the assembly that PASSED, leaving the failing test unnamed in the CI artifact.
  Now each writes its own default-named file.
- **The test suite wrote into the developer's real index — and pruned their undo history.**
  `ModificationJournal.ForIndex` resolves the journal to `<dirname(D365FO_INDEX_DB)>/journal`,
  and every scaffold-writing test journals. On a machine where `d365fo init` has configured
  `D365FO_INDEX_DB` — the normal state — a full run appended into the real journal, and the
  500-entry cap then evicted the developer's genuine entries oldest-first. Measured on this
  repo's host: exactly 500 entries, the newest a `scaffold-write` naming a test temp directory.
  Both assemblies also ran as concurrent processes appending to and pruning that one directory.
  A `[ModuleInitializer]` now points each test process at its own index; `D365FO_PACKAGES_PATH`
  is deliberately left alone so the AOT-gated tests still run against a real installation.
- **The suite's intermittent failure had a name after all.** Twenty test classes called the
  process-wide `SqliteConnection.ClearAllPools()` in `Dispose` to unlock their temp database.
  That disposes every pooled connection in the process, including one another test is mid-query
  on, so the loser failed with `ObjectDisposedException: 'SQLitePCL.sqlite3'` from somewhere
  unrelated — about one full-suite run in ten, and never reproducible when an assembly ran
  alone. Replaced with `SqlitePool.ReleaseFor(path)`, which clears only that database's pools
  (all three connection-string spellings this codebase opens). **0 failures in 14 consecutive
  full-suite runs**, against 2–3 per 10 before.
- A flaky assertion in `WorkflowScaffolderTests` and `ScaffoldFileWriterAtomicityTests` read
  "which files were written" off a `Directory.GetFiles` listing taken immediately after the
  writes. Under load that listing can miss an entry the rename just created, which surfaced
  as "Expected 3, Actual 2" with no exception anywhere — the writer had already proven the
  file was there by taking `FileInfo.Length` on it. Existence is now queried per path
  (`WrittenFilesAssert`). A real but secondary cause — the `ClearAllPools` entry above is what
  the remaining failures turned out to be.

### Added — documentation
- This file, and `SECURITY.md`.

---

## 2026-09-01 — trimmed publish

### Fixed
- The trimmed, single-file, self-contained build — the shape `install.ps1` / `install.sh`
  actually produce — failed on every SQLite query and on the native SQLite load, and returned an
  empty journal instead of an error, all while the test suite stayed green (#182). CI now
  publishes that shape and diffs it against an ordinary build
  (`scripts/smoke-published-cli.ps1`).

## 2026-08-31 — the upstream 1.15.0 wave

### Added
- **Compiler-oracle validator**: the validator wave ported from upstream, with the compiler as
  the oracle rather than hand-held expectations.
- **`prepare test`** (`mode=test`): everything needed to write a SysTest, before writing it.
- **Kernel table-method catalog** for `prepare`, EDT `StringSize` inheritance, CoC target
  normalization.
- **`generate report-extension`** — the three compiler-checked ways to extend a shipped report.
- **Report scaffold depth**: `--pre-process`, `--controller-type print-mgmt`, `--ui-builder`.
- **`generate table --field-group` / `--index`**, with valid-time-state index properties.
- `generate table-relation` and `generate find-methods` augmenters, and the catalog-driven form
  expander.
- Three knowledge topics — warehouse-app, barcode-scanning, runtime-functions — plus
  language-core additions, taking the corpus from 35 to 38.
- Labels: search/resolve hits are confirmed against the `.label.txt` before being recommended.

### Fixed
- Every pattern `generate form` emits now compiles; the expanded forms are something the AOS
  accepts.
- The `#184` report DP and contract scaffolds now compile.
- Every scaffolder wraps `Source`/`Declaration` in CDATA (#183).
- Labels resolve the `@File:Id` token shape the AOT actually writes.
- Knowledge corrections carried over from the upstream 1.15.0 wave.
- Kernel enums, red-first SysTest, `NumberSeqFormHandler`, form `DataGroup` honesty.

## 2026-08-21 / 2026-08-23 — generate and modify correctness

### Added
- Fields can be added to a table extension offline.

### Fixed
- `--verify` is honoured on every `generate` subcommand, and only a refused artefact fails it.
- `modify` resolves extension kinds from the registry rather than a private table.
- EDT lookup recovers truncated whole-name matches and matches `GetEdt` case-insensitively.
- Skills frontmatter is quoted and parsed in CI (#172 shipped frontmatter no YAML parser
  could read while CI stayed green).

## 2026-08-06 / 2026-08-07 — forms, eval and validation depth

### Added
- Clone a reference form under a new name (#164).
- Cross-check the shipped catalogs against the installation (#164).
- `test run` invokes the real SysTest runner, and its result document is parsed (#160).
- The grounding gate applies uniformly across `generate`, with property honesty (#161).
- Security and data-entity scaffolding depth (#162).
- Per-family root-shape validation rules XML009–XML013 (#163).

### Fixed
- A renamed datasource is followed into the design and the join (#164).
- A bound field's control type is derived from the field (#164).
- The atomic write is actually atomic (#158).

## 2026-04-23 — bootstrap

### Added
- The `d365fo` CLI, the dual Agent Skills layer (Anthropic + Copilot), the extract pipeline and
  the first parity commands.
