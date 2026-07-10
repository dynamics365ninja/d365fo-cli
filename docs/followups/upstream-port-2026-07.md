# Follow-up: upstream d365fo-mcp-server changes NOT ported in the 2026-07 wave

The 2026-07 port wave (branch `claude/d365fo-mcp-port-plan-joqtl0`) synced the
CLI with upstream `d365fo-mcp-server` up to PR #671 (2026-07-09) for bug fixes
to functionality the CLI already has. The items below were deliberately
deferred — each is either a whole missing subsystem (a feature decision, not a
bug fix) or targets a surface this CLI does not expose.

## Deferred — candidate future features

- **Modify operations on existing objects** (upstream `b81ae94` modify-enum-value
  rename, `922d262` add-control, `03d698f` modify-property false-success,
  `4b772f1`/`3b14715` modify param aliases, `f045b6c` CDATA source guard).
  The CLI mutates existing objects only via `generate datasource-method` /
  `control-method` and `generate role --add-to`; there is no
  modify-property/add-field/add-control/replace-code surface. Porting this is a
  new command family (`d365fo modify …`) plus bridge verbs — the CLI bridge
  (`src/D365FO.Bridge`) only exposes generic Save/Update/Delete of whole
  artifacts.
- **Knowledge base + build-error hint scoring** (upstream `bf4ed28` error-hint
  scoring false positives, `31b9ced` Excel/CSV + parallel-batch + direct-SQL
  X++ topics, `9183f63` AxMenuElementSubMenu doc). The CLI has no
  `get_knowledge`/error-help subsystem; only the form-pattern catalog was ever
  ported (`src/D365FO.Core/FormPatterns`).
- **View / Map creation** (upstream `a36f4ac` map `edt` alias + property docs,
  `dac1413` view datasource docs). The CLI only reads/indexes views and maps;
  there is no `ViewScaffolder`/`MapScaffolder`. The upstream alias fixes become
  relevant only once creation exists.
- **TRUDUtils-style generators + form auto-repair** (upstream `17ac6e2`
  deterministic form control expander, find-methods, relation-xpp; `7c8a210`
  form repair + Create Table Relation). Partial overlap exists
  (`FormMethodScaffolder`, `FormPatternValidator`), but the control
  expander/repair pipeline is a feature port of its own.
- **Bulk multi-key label creation** (upstream `2cfaf38` `labels:[{labelId,…}]`
  with per-item error isolation). The CLI's `label create` fans out one key to
  multiple locales; multi-key batches would extend
  `src/D365FO.Cli/Commands/Label/LabelWriteCommands.cs`.

## Not applicable to this codebase (permanently)

- Upstream bridge verbs the CLI bridge does not expose: per-property setters
  (`Set<Type>Property`), `CreateFormControl`, `AddMenuItemToMenu`,
  `add-data-source` (upstream `7d4314c`, `111949a` bridge parts). The CLI
  bridge persists caller-built XML via `MetadataBootstrap.SaveArtifact`;
  `BuildModelSaveInfo` already resolves runtime `ModelInfo` incl. `Name`,
  `Id`, `SequenceId` — verified equivalent to upstream `111949a`.
- `createSmartTable` routing (`817bd4c`): the CLI's `generate table` already
  emits smart defaults (field groups, PrimaryIdx, ClusteredIndex) directly.
- MCP-server infrastructure: eval framework/oracle/corpus, tool-schema token
  diet, stdio crash handling, in-flight dedup, Node interactive management
  CLI, startup UX.
