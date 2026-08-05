# Follow-up: upstream d365fo-mcp-server changes NOT ported in the 2026-08 wave

This doc extends `docs/followups/upstream-port-2026-07.md`, which synced the
CLI with upstream `d365fo-mcp-server` up to PR #671 / commit `fd78ece`
(2026-07-08). It does **not** replace that doc — the 2026-07 doc stays as the
historical record for that pass, and anything it already covers is not
re-litigated here unless upstream has done additional work in that area.

Boundary for this pass: **`fd78ece` (PR #671, 2026-07-08) .. `7c0fef6` (PR
#813, 2026-08-04)** — 354 commits / 115 first-parent PRs, spanning upstream
releases v1.1.0 through v1.8.0. Upstream repo cloned fresh from
`https://github.com/dynamics365ninja/d365fo-mcp-server` (default branch) on
2026-08-04; all SHAs below are from that clone.

Two items from the 2026-07 doc are being worked on in parallel by other
agents right now and are **not** re-investigated here — see the "in
progress" notes under Deferred item 1 below:
- GitHub issue #112 (structured method-level modify via bridge)
- GitHub issue #113 (modification journal/undo)

This pass also catalogued a set of targeted bug fixes — label where-used in
`find references --xref`, case-insensitive `[ExtensionOf]` parsing, custom/ISV
model search ranking, `generate table` configuration key / form ref, inherited
class members, and row-level security coverage. **All of them have since been
ported**, so they are no longer listed here; see the git history for the
analysis behind each.

## Deferred — candidate future features

1. **Modify operations on existing objects — extends the 2026-07 item, IN
   PROGRESS elsewhere (issues #112, #113).** Not investigated in depth per
   task instructions. Upstream has continued heavy investment in exactly
   this area since `fd78ece`: PR #800 `feat/form-extension-and-data-entity-
   extension-support` (laeliand), PR #799 `fix/table-extension-fallback-
   for-relation-and-index-ops` (laeliand), PR #804 `fix/extension-fallback-
   for-remaining-write-ops`, PR #776 `fix/extension-writer-properties`, PR
   #746 `fix/bridge-write-honesty`, PR #731 `fix/modify-silent-param-drop`,
   PR #728 `fix/form-design-root-add-control`, plus commits `cab3284`
   (data-entity-extension add-field), `3868d23` (extension write gaps),
   `ec07ca3` (extension fallback for field-group/enum-value/data-source),
   `a04a583` (addField RPC param forwarding), `015d697` (modify-property
   for data-entity), `95a474f` (add-control on form design root), `982d8b7`
   (stop discarding params in silence), `8be57d7` (honour dropped write
   params). Whoever resumes work on #112/#113 should treat this list as
   the up-to-date upstream state of that subsystem, not the 2026-07 doc's
   snapshot.

2. **`connect` command — configure an editor for a deployed HTTP-mode
   server** (upstream `e1f56bb`/PR #712-adjacent `feat(cli): add connect —
   configure an editor for a deployed server`, 2026-07-21). Once a server
   is reachable over HTTP, `npx d365fo-mcp connect <url>` merges the right
   MCP config entry into VS Code / Claude Code / other editor config files
   (probing `/health` first to distinguish a typo from a cold-starting
   server; `--force` to override; merges rather than clobbers existing MCP
   entries). **This has no CLI equivalent today and is a direct follow-on
   to the in-progress #114 HTTP-transport work** — right now #114 is adding
   `X-Api-Key` + `MCP_SERVER_MODE` to `D365FO.Mcp`'s HTTP transport, but
   once a deployed server exists there is still no CLI-side helper to wire
   an editor's MCP config at it; a user would have to hand-edit
   `mcp.json`/`.vscode/mcp.json`/`~/.claude.json` per the upstream doc's own
   admission that this was previously "documented as write this JSON by
   hand." Worth a follow-up ticket once #114 lands, scoped as a new
   `d365fo connect <url>` command (or `D365FO.Mcp` equivalent) under
   `src/D365FO.Cli/Commands/`.

3. **Knowledge base + build-error hint scoring — extends the 2026-07
   item.** Upstream keeps growing this subsystem: `2941ed6` adds a
   class-inheritance topic, `d3f268b` fixes the custom-services topic to
   stop mandating the deprecated `SysEntryPointAttribute`, `1e9ddb0`
   corrects the electronic-reporting entry and documents dual-write's
   table-side prerequisite, `d4587c2`/`786c9ec` gate knowledge content the
   same way generated code is gated. Still no `get_knowledge`/error-help
   equivalent in this CLI (`src/D365FO.Core/FormPatterns` remains the only
   ported slice); the gap is unchanged in kind, just larger in upstream
   content volume.

4. **View / Map / Query creation writers — extends the 2026-07 item.**
   Further writer-honesty fixes landed: `6b66454` "write the ranges, and
   stop inventing a literal Title" and `01d902d` "emit DynamicFields where
   the contract puts it, before Table" (both in `queryViewXml.ts`), plus
   `79caaee` "stop the create/generate path emitting metadata that cannot
   build." Still not applicable until the CLI has a `ViewScaffolder`/
   `MapScaffolder` writer to receive these fixes.

5. **TRUDUtils-style generators + form auto-repair** — no material new
   upstream work in this specific area since `fd78ece` beyond eval-goldens
   closing out `form-lifecycle` coverage (`1d45e81`, verified now depends
   on PR #728's `add-control` landing for real rather than the
   `create overwrite=true` escape hatch — itself part of item 1 above).
   No change to this item's status from the 2026-07 doc.

6. **Bulk multi-key label creation** — no upstream changes to
   `src/tools/labels.ts` since `fd78ece`. No change to this item's status
   from the 2026-07 doc.

## Not applicable to this codebase (permanently)

- **npm/Node packaging and distribution infrastructure**: `dcb182f`
  `feat(installer): install from npm instead of cloning`, `ab649a2`
  `feat(bridge): build the C# bridge outside the npm package`, `391ae21`
  `feat(cli): keep config and index outside the npm package`, `89b7715`
  `feat(cli): make an npm install a full installation`, `fc93d0e`
  `feat(install): one-line PowerShell installer + npm publish preparation`,
  `c262b19`/`82a97bc`/`8a6b07f` (installer npm path, bridge-outside-package,
  npm-full-install), `722d0e7` "trusted publishing and script guard",
  `88270f7` `refactor/node-sqlite-drop-native-dep`, `753`/`754`
  (`better-sqlite3` → `node:sqlite`, SQLite startup preflight). This CLI
  ships as a .NET global tool via NuGet; none of upstream's npm-registry /
  Node-native-module distribution concerns apply.
- **Setup wizard / Copilot & MCP-client bootstrap**: `281d7db`
  `feat(setup): configure the server through a wizard, not a hand-edited
  .env`, `282cc84`/`0754063`/`df03306` (JSON starter config, dev-environment
  preselection, workspace-path prompt), `594fe99`
  `feat(setup): generate .mcp.json and stage copilot setup files`,
  `65520e0`/`b28515f` (Copilot instructions packaging), `13f9553`
  `fix/setup-bridge-double-prompt`, `2ab5aee`
  `feat/setup-workspace-path-and-index-progress`. This is upstream's
  interactive first-run wizard for its own Node CLI/`.env`-based config
  model; this repo's instance configuration and bridge-build story are
  already structurally different (see the existing "Not applicable" bullet
  on bridge verbs) and don't share this surface.
- **MCP-server infrastructure (extends the 2026-07 bullet)**: the eval
  framework/golden-corpus work continued at very high volume this pass —
  roughly 40+ of the 115 first-parent PRs are `eval:`/`chore(eval):` golden
  captures, coverage-matrix regeneration, or oracle/scoring fixes (e.g.
  `1d45e81`, `ab35b2c`, `d5e42d9`, `26de4e9` fixture harness + INPUT/OUTPUT
  classifier, `50e66d9` "close eval sweep findings"). Also new in this
  category: `158754b`/PR #716 `perf(index): keep scoped builds O(scope),
  not O(database)` (upstream's own FTS5/`better-sqlite3` incremental-build
  perf work in `scripts/build-database.ts` — the CLI's incremental refresh
  in `D365FO.Cli/Commands/Index/IndexCommands.cs` already exists as a
  differently-shaped mtime-based mechanism; no evidence of the same O(N)
  regression here, not investigated further). Still no CLI analogue to any
  of this — it is entirely upstream's own test/tooling harness.
- **Bridge single-op RPC dispatch surface** (upstream `0f15f5b`/PR #812
  "close single-op RPC dispatch gaps left by PR #804", where 4 write ops were
  wired into the batch-modify dispatch arm but not the single-op `Dispatch()`
  arm, so real calls got "-32601 Unknown method"). Confirmed not applicable:
  the CLI's bridge (`src/D365FO.Bridge/Handlers.cs`, `Program.cs`) has no
  generic `RequestDispatcher`/named-verb RPC surface at all (no
  `add-full-text-index`, `add-table-mapping`, etc.) — it exposes only generic
  `SaveObject`/`UpdateObject`/`DeleteObject` plus the read-only
  `FindReferences` xref handler. This confirms (does not extend) the existing
  bullet above on bridge verbs the CLI bridge doesn't expose.

## HTTP transport / auth (issue #114 relevance check)

Checked explicitly per this task's scope: **upstream has not materially
changed `src/server/transport.ts` or `src/server/serverMode.ts` since the
version already inspected for #114.** Only two commits touched either file
in the whole `fd78ece..HEAD` range (354 commits):

- `d2adafd` (2026-07-15) — changes the `/health` endpoint to read
  `getCachedSymbolCounts()?.total` instead of calling
  `getSymbolCount()` synchronously, so a health probe never triggers a
  30-60s COUNT scan. Cosmetic/perf only, no auth or transport change.
- `0432c5d` (2026-07-17) — adds `isToolAllowedInMode(mode, toolName)` to
  `serverMode.ts` as a single source of truth for mode-gating, fixing a
  real bug: write-only mode's runtime call-gate in `toolHandler.ts` ignored
  `ALWAYS_TOOLS`, so tools like `get_object_info`/labels/`d365fo_file` were
  advertised in `ListTools` under write-only mode but then refused at call
  time. **This is directly relevant to the #114 work**, since #114 is
  porting the `MCP_SERVER_MODE` concept to this CLI's HTTP transport — if
  the C# port has separate code paths for "what's advertised" vs "what's
  allowed at call time" (mirroring the pre-fix upstream split), it should
  use a single shared predicate from the start rather than reproducing
  this drift bug. No auth-token or `X-Api-Key`/rate-limiting mechanism
  changed in either commit or anywhere else in the 354-commit range — a
  keyword search for `X-Api-Key`/`apiKey`/`rate.limit`/`authoriz*`/
  `authentic*` across the full commit range since `fd78ece` returned no
  matches outside of unrelated `npm audit` and doc-wording commits.

**Bottom line for #114**: the upstream design already inspected (auth
header, rate limiting) is still current — no follow-up needed there. The
one thing worth carrying into the C# port is the `isToolAllowedInMode`
single-predicate pattern from `0432c5d`, to avoid upstream's own
advertise/enforce drift bug in write-only mode.
