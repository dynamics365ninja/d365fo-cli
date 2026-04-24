# Examples

One worked example per command. Setup (install, env vars, first run) lives in [SETUP.md](SETUP.md).

Every example assumes `d365fo` is on your `PATH` and `D365FO_PACKAGES_PATH` + a populated index are in place.

---

## Output contract

Every command returns a predictable result:

- **Interactive terminal** → rendered tables.
- **Piped / script / CI** → JSON envelope.
- Force either with `--output json|table|raw`.

The JSON envelope is always one of:

```json
{ "ok": true,  "data": { /* … */ }, "warnings": [] }
{ "ok": false, "error": { "code": "…", "message": "…", "hint": "…" } }
```

**Exit codes:** `0` success · `1` controlled failure (error envelope still prints) · `2` unhandled exception.

---

## Discover

### `search` — fuzzy-find AOT objects

```sh
d365fo search class Cust
```

Same pattern for `search table|edt|enum|form|query|view|entity|report|service|workflow|label`. `search label` sanitises control characters by default; pass `--raw-text` to opt out.

### `get` — full metadata for one object

```sh
d365fo get table CustTable
```

Works for `get class|edt|enum|form|menu-item|security|label|role|duty|privilege|query|view|entity|report|service|service-group`. Misspelled names return a `*_NOT_FOUND` envelope with a Levenshtein-ranked `hint: "Did you mean: …"`.

Rewrite `@File+Id` tokens to human text with `--resolve-labels` (language picked from `D365FO_LABEL_LANGUAGES`, falls back to `en-us`):

```sh
d365fo get table CustTable --resolve-labels
```

### `find` — trace cross-references

```sh
d365fo find coc CustTable::validateWrite
```

Also available: `find relations|usages|extensions|handlers|refs`. `find refs --xref` queries `DYNAMICSXREFDB` through the bridge for path/line/column/kind precision.

### `resolve label` — look up a label token

```sh
d365fo resolve label @SYS12345 --lang en-US,cs
```

### `read` — pull X++ source from AOT XML

```sh
d365fo read class CustTable_Extension --method validateWriteExt
```

`read table` and `read form` work the same way; add `--lines 10-40` or `--declaration` to scope the snippet.

### `models` — inspect indexed models

```sh
d365fo models deps ApplicationSuite
```

`models list` enumerates every model with publisher, layer, and custom-flag.

### `search any` — scope-agnostic quick jump

```sh
d365fo search any CustTable
```

UNIONs every indexed kind in one query and returns `byKind` counts for triage.

### `stats` — per-model + top-N aggregates

```sh
d365fo stats --top 10
```

Returns per-model object counts plus top tables (by field count), top classes (by method count), and top CoC extension targets. Handy for sizing a customisation and for agent prompts.

---

## Maintain the index

### `index refresh` — incremental re-extract

```sh
d365fo index refresh
d365fo index refresh --force          # re-scan every model
d365fo index extract --since 2026-04-01T00:00:00Z  # explicit threshold
```

Computes the newest `.xml` mtime under each model folder and skips models that are older than the DB's last-write timestamp (minus a 5-minute safety margin). `--force` is the no-threshold equivalent of `index extract`.

### `lint` — in-process Best-Practice heuristics

```sh
d365fo lint
d365fo lint --category table-no-index,string-without-edt --all-models
d365fo lint --format sarif > lint.sarif
```

Categories shipped today: `table-no-index`, `ext-named-not-attributed`, `string-without-edt`. Defaults to custom models only; pass `--all-models` to include ISV / MS content. `--format sarif` emits [SARIF 2.1.0](https://sarifweb.azurewebsites.net/) for CI ingestion (GitHub code-scanning, Azure DevOps).

### `validate name` — naming-rule linter

```sh
d365fo validate name Table FmVehicle --prefix Fm
d365fo validate name Coc CustTable_Extension
```

Static check against `ObjectNamingRules` (publisher prefix, PascalCase, suffix conventions, length / char-set). Returns structured `violations[]` with `code`, `severity`, `message`. Handy as a pre-commit hook.

### `init` — quickstart

```sh
d365fo init --run-extract
d365fo init --dry-run                  # show what would be done
d365fo init --persist-profile          # append env vars to $PROFILE / ~/.profile
```

Auto-detects the Windows `PackagesLocalDirectory` (C:, J:, K:, `AosService`), prepares the SQLite schema, and (with `--run-extract`) drives the full extract pipeline. `--persist-profile` idempotently writes a marker block (`# >>> d365fo-cli init >>>` … `# <<< d365fo-cli init <<<`) into the user's shell profile (`$PROFILE` on Windows PowerShell, `~/.profile` elsewhere) exporting `D365FO_PACKAGES_PATH` / `D365FO_INDEX_DB` / `D365FO_WORKSPACE` — re-running replaces the block only when values change.

---

## Scaffold

`generate` writes atomically (`.tmp` + move) and keeps a `.bak` when `--overwrite` is used. Pass `--install-to <Model>` to drop the artefact straight into a model folder via the bridge (requires `D365FO_BRIDGE_ENABLED=1`, `D365FO_PACKAGES_PATH`, `D365FO_BIN_PATH`).

### Table

```sh
d365fo generate table FmVehicle \
  --label "@Fleet:Vehicle" \
  --field VIN:VinEdt:mandatory \
  --field Make:Name \
  --out src/MyModel/AxTable/FmVehicle.xml
```

### Class

```sh
d365fo generate class FmVehicleService --extends RunBase \
  --out src/MyModel/AxClass/FmVehicleService.xml
```

### Chain-of-Command extension

```sh
d365fo generate coc CustTable --method update --method insert \
  --out src/MyModel/AxClass/CustTable_Extension.xml
```

### Simple-list form

```sh
d365fo generate simple-list FmVehicleListPage --table FmVehicle \
  --out src/MyModel/AxForm/FmVehicleListPage.xml
```

### Data entity (`AxDataEntityView`)

```sh
d365fo generate entity FmVehicleEntity --table FmVehicle \
  --public-entity-name FmVehicle --public-collection-name FmVehicles \
  --all-fields \
  --out src/MyModel/AxDataEntityView/FmVehicleEntity.xml
```

Emits a single-datasource `AxQuerySimpleRootDataSource` view with public OData names. Pass `--all-fields` to auto-populate `<Fields />` from the source table's `TableFields` (mandatory flag carries over). Without `--all-fields` or explicit `--field` flags, `<Fields />` is empty.

### Extension (table / form / EDT / enum)

```sh
d365fo generate extension Table CustTable Contoso \
  --out src/MyModel/AxTableExtension/CustTable.Contoso.xml
```

Name is always `<Target>.<Suffix>` to match the AOT convention. Kinds: `Table`, `Form`, `Edt`, `Enum`.

### Event handler

```sh
d365fo generate event-handler Contoso_CustTable_Handler \
  --source-kind Table --source-object CustTable --event inserted \
  --out src/MyModel/AxClass/Contoso_CustTable_Handler.xml
```

Picks the right attribute (`[DataEventHandler]`, `[FormEventHandler]`, `[FormDataSourceEventHandler]`, `[SubscribesTo]`) from `--source-kind`.

### Security privilege / duty

```sh
d365fo generate privilege FmVehicleReadPriv \
  --entry-point FmVehicleListPage --entry-kind MenuItemDisplay --entry-object FmVehicleListPage \
  --access Read --label "@Fleet:ReadVehicles" \
  --out src/MyModel/AxSecurityPrivilege/FmVehicleReadPriv.xml

d365fo generate duty FmVehicleMaintainDuty \
  --privilege FmVehicleReadPriv --privilege FmVehicleUpdatePriv \
  --out src/MyModel/AxSecurityDuty/FmVehicleMaintainDuty.xml
```

### Security role (new or merge)

```sh
# Scaffold a new role that references duties / privileges
d365fo generate role FmVehicleAdminRole \
  --duty FmVehicleMaintainDuty --privilege FmVehicleReadPriv \
  --label "@Fleet:VehicleAdminRole" --description "Full access to Fleet vehicles" \
  --out src/MyModel/AxSecurityRole/FmVehicleAdminRole.xml

# Merge new references into an existing role (idempotent; writes .bak)
d365fo generate role --add-to src/MyModel/AxSecurityRole/FmVehicleAdminRole.xml \
  --duty FmReportingDuty --privilege FmExportPriv
```

`--add-to` validates the root element (`AxSecurityRole`), dedupes by `Name` (case-insensitive), and returns `NoChange` when every reference already exists.

---

## Labels (read & write)

```sh
# Read
d365fo search label "Customer invoice"
d365fo search label "customer invoice" --fts        # rank-sorted FTS5
d365fo get label @SYS12345 --language en-us

# Write \u2014 atomic, preserves comments, BOM UTF-8
d365fo label create NewKey "New value" --file path/Foo.en-us.label.txt
d365fo label create NewKey "Updated"   --file path/Foo.en-us.label.txt --overwrite
d365fo label rename NewKey RenamedKey  --file path/Foo.en-us.label.txt
d365fo label delete RenamedKey         --file path/Foo.en-us.label.txt
```

`search label --fts` requires a schema-v6 index (run `d365fo index refresh --force` once after upgrading) and falls back to `LIKE` scans on SQLite builds without FTS5. Write commands return envelope codes `KEY_EXISTS` / `KEY_NOT_FOUND` / `FILE_NOT_FOUND` / `WRITE_FAILED` and keep a `.bak` of the previous file.

---

## Review

```sh
d365fo review diff --base HEAD
```

Compare two revs with `--base main --head feature/my-branch`. Rules shipped today:

- `FIELD_WITHOUT_EDT` — table field without `<ExtendedDataType>`.
- `FIELD_WITHOUT_LABEL` — user-facing field without `<Label>`.
- `HARDCODED_STRING` — verbatim string literal in X++ source.
- `DYNAMIC_QUERY` — dynamic `Query` construction (flag for security review).

---

## Windows-only ops (D365FO VM)

These commands wrap the Microsoft tooling Visual Studio uses, so you can drive the IDE's workflow from a terminal, script, or CI pipeline.

```powershell
d365fo build --project C:\AosService\PackagesLocalDirectory\MyModel\MyModel.rnrproj
d365fo sync --full
d365fo test run --suite MyModel.Tests
d365fo bp check --model MyModel
```

Each parses the tool output and returns a structured JSON envelope (errors, warnings, elapsed time, tail of stdout). On non-Windows they return `UNSUPPORTED_PLATFORM`.

---

## Agent integration

### Emit the system prompt

```sh
d365fo agent-prompt --out .prompts/d365fo.md
```

`d365fo schema --full` emits a machine-readable catalog of every command.

### GitHub Copilot (VS Code / Visual Studio)

```sh
cp skills/copilot/* .github/instructions/
d365fo agent-prompt --out .github/copilot-instructions.md
```

Copilot picks up `.github/instructions/*.instructions.md` via `applyTo` globs and drives `d365fo` through its terminal tool.

### Claude Code / Claude Desktop

Drop `skills/anthropic/` into the project or `~/.claude/skills/`. Each `SKILL.md` triggers via its `applies_when` front-matter.

### Codex CLI / Gemini CLI

Paste the output of `d365fo agent-prompt` into the session system prompt, or reference it from `AGENTS.md`.

### MCP server (`d365fo-mcp`)

Standalone JSON-RPC 2.0 server (protocol `2024-11-05`) that shares the CLI's index. Config sample for Claude Desktop:

```jsonc
{
  "mcpServers": {
    "d365fo": {
      "command": "dotnet",
      "args": ["run", "--project", "/abs/path/to/src/D365FO.Mcp", "--no-build"],
      "env": { "D365FO_INDEX_DB": "/abs/path/d365fo-index.sqlite" }
    }
  }
}
```

After `dotnet publish src/D365FO.Mcp -c Release -r osx-arm64` you get a standalone `d365fo-mcp` binary you can drop on `$PATH`. The adapter exposes **53 tools** covering CLI parity (search / get / find / read / index_status), security & labels — read (`get_label`, `search_labels`, `search_labels_fts`) and write (`create_label`, `rename_label`, `delete_label`) — heuristics (`search_any`, `suggest_edt`, `validate_object_naming`, `analyze_extension_points`), aggregation (`stats`, `batch_search`), workspace info, and the in-proc `lint` runner. Items still missing from MCP are tracked in [ROADMAP.md](ROADMAP.md).

---

## Daemon (warm cache)

For latency-sensitive integrations, run the CLI as a daemon so the SQLite handle and read caches stay hot:

```sh
d365fo daemon start
d365fo daemon status
d365fo daemon stop
```

Transport: Windows named pipe `\\.\pipe\d365fo-cli`; Unix socket at `$XDG_RUNTIME_DIR/d365fo-cli.sock` (fallback `$TMPDIR`). The frame format matches `d365fo-mcp`: one newline-terminated JSON-RPC request per connection, one response, close.

---

## CI / automation

Every command is scriptable: exit codes are reliable, output is JSON by default in non-TTY, no interactive prompts.

```yaml
- name: D365 review
  run: |
    d365fo index build
    d365fo review diff --base origin/main --head HEAD --output json \
      | jq -e '.data.violationCount == 0'
```
