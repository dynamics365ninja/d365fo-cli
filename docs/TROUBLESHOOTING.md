# Troubleshooting

Common failure modes and their fixes. For installation details see [SETUP.md](SETUP.md).

---

## Installer (`install.ps1` / `install.sh`)

### `d365fo` not recognized after the installer finishes

The installer adds its publish directory to the **User** `PATH` via the registry / shell profile, but the *current* shell process doesn't reread that until you open a new one. Open a new PowerShell/terminal window, or `Refresh-Path`-equivalent: `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')` (bash: `source ~/.bashrc` / `~/.zshrc` / `~/.profile`, whichever the script appended to).

### `.NET SDK still not on PATH` / winget unavailable

D365FO VMs are Windows Server, where `winget` is often missing. The installer falls back to the official `dotnet-install.ps1`/`.sh` script (no admin rights needed, installs under `%LOCALAPPDATA%\Microsoft\dotnet` / `~/.dotnet`). If it still isn't found afterward, open a new shell first (see above) before assuming the install failed.

### Picked up the wrong existing checkout

The installer probes `$env:D365FO_CLI_DIR` / `$D365FO_CLI_DIR`, then `K:\d365fo-cli` (Windows) or `~/d365fo-cli`, and updates in place (`git pull --ff-only`) whichever it finds first. If you have a checkout elsewhere, set that env var before running the installer so it targets the right directory instead of cloning a second copy.

### `The type initializer for 'Microsoft.Data.Sqlite.SqliteConnection' threw an exception`

Also seen as `A parameterless default constructor ... is required for ... materialization`,
`Must add values for the following parameters: @v, @t`, or a `journal list` that reports zero
entries on a journal that plainly has some. All four are the same thing: a binary published
before the fix for [#182](https://github.com/dynamics365ninja/d365fo-cli/issues/182), where
trimming removed the reflection the SQLite provider, Dapper and `System.Text.Json` need, and
`PublishSingleFile` left the native `e_sqlite3` library outside the executable.

**Fix:** re-run the installer, or re-publish from a checkout that includes the fix. Nothing
about the publish command changes — the settings live in `src/D365FO.Cli/D365FO.Cli.csproj`
and `src/D365FO.Cli/TrimmerRootDescriptor.xml`, and CI now diffs a published build against an
ordinary one on every push (`scripts/smoke-published-cli.ps1`).

To confirm which one you are running, `d365fo models list --output json` on the fixed build
returns models rather than an error.

### `git pull --ff-only` failed

Local commits or a detached `HEAD` in the install directory diverge from `origin/main`. The installer won't merge or discard anything for you — resolve it yourself (`git status` in that directory), then re-run the installer.

---

## Package path patterns

The `D365FO_PACKAGES_PATH` environment variable (or `--packages <PATH>`) must point to the root of `PackagesLocalDirectory`. Common locations:

| Environment | Typical path |
|---|---|
| Cloud-hosted dev VM (Azure) | `K:\AosService\PackagesLocalDirectory` |
| Local Hyper-V devbox | `C:\AOSService\PackagesLocalDirectory` |
| Azure Files share (mounted) | `Z:\PackagesLocalDirectory` (drive letter varies) |
| Docker container | `/mnt/packages` (depends on volume mount) |
| UDE (primary — shared drive) | `K:\AosService\PackagesLocalDirectory` |
| UDE (extra — local laptop) | `C:\LocalMetadata\PackagesLocalDirectory` — set in `D365FO_CUSTOM_PACKAGES_PATH` |
| Custom override | Set `D365FO_PACKAGES_PATH` to any absolute path |

### UDE — two separate `PackagesLocalDirectory` folders

UDE setups split standard Microsoft metadata (on a shared drive) from your custom model XML (on the local laptop). The CLI supports this via `D365FO_CUSTOM_PACKAGES_PATH` or `--extra-packages`:

```powershell
# Environment variables (persist in $PROFILE)
$env:D365FO_PACKAGES_PATH         = 'K:\AosService\PackagesLocalDirectory'
$env:D365FO_CUSTOM_PACKAGES_PATH   = 'C:\LocalMetadata\PackagesLocalDirectory'
d365fo index extract

# — or — one-shot CLI flags
d365fo index extract `
    --packages       K:\AosService\PackagesLocalDirectory `
    --extra-packages C:\LocalMetadata\PackagesLocalDirectory
```

`D365FO_CUSTOM_PACKAGES_PATH` accepts semicolon- or comma-separated paths. Extra roots that don't exist are silently skipped. See [SETUP.md — UDE setup](SETUP.md#ude-unified-developer-experience-setup) for the full walkthrough.

```sh
# PowerShell — set for the current session
$env:D365FO_PACKAGES_PATH = "K:\AosService\PackagesLocalDirectory"

# Persist across sessions (append to $PROFILE)
d365fo init --persist-profile
```

The CLI also accepts `--packages <PATH>` on every command as a one-shot override without touching the environment.

---

## Common extraction failures

### `PACKAGES_PATH_NOT_FOUND`

```
error: { "code": "PACKAGES_PATH_NOT_FOUND", "hint": "Set D365FO_PACKAGES_PATH or pass --packages <PATH>" }
```

Fix: set `D365FO_PACKAGES_PATH` or pass `--packages`. Verify the path exists: `Test-Path $env:D365FO_PACKAGES_PATH`.

### Unicode characters in the path

Paths containing non-ASCII characters (accented letters, CJK, etc.) can cause the .NET file-system walker to throw `DirectoryNotFoundException` on certain Windows builds. Fix: move the packages directory to an ASCII-only path, or set a junction point:

```powershell
New-Item -ItemType Junction -Path "C:\D365Packages" -Target "K:\AosService\PackagesLocalDirectory"
$env:D365FO_PACKAGES_PATH = "C:\D365Packages"
```

### Locked AOT files during build

If Visual Studio is actively compiling when `index extract` runs, certain `.xml` files may be locked by `MSBuild`. The extractor skips locked files and records a warning in the extraction log. Run `index extract` again after the build completes:

```sh
d365fo index extract --model MyModel
d365fo index history --model MyModel   # confirm no extraction errors
```

### `.NET 4.8 bridge not found` on non-Windows systems

The `D365FO.Bridge` child process requires the .NET Framework 4.8 runtime, which is Windows-only. On macOS or Linux the bridge is unavailable and the CLI falls back to the SQLite index automatically:

```
warning: bridge unavailable on this platform; falling back to index
```

This is expected behaviour — the index still serves `search`, `get`, `find`, and `generate` commands. Only `find refs --xref` (which queries `DYNAMICSXREFDB` directly) requires the bridge.

---

## SQLite WAL-mode locking

The index uses WAL (Write-Ahead Logging) mode for concurrent reads. You may see these files alongside the main database:

```
d365fo-index.sqlite
d365fo-index.sqlite-wal      ← in-progress transactions
d365fo-index.sqlite-shm      ← shared memory for WAL
```

**Symptom:** `d365fo index extract` hangs or returns `DATABASE_LOCKED`.

**Cause:** Two writers accessing the database simultaneously — typically a `daemon` process and a manual `index extract` running at the same time.

**Fix:**

1. Stop any running daemon: `d365fo daemon stop`
2. Stop any running `d365fo-mcp` process.
3. Delete stale WAL sidecars **only when no process is using the database**:
   ```sh
   # PowerShell
   Remove-Item "$env:LOCALAPPDATA\d365fo-cli\d365fo-index.sqlite-wal" -ErrorAction SilentlyContinue
   Remove-Item "$env:LOCALAPPDATA\d365fo-cli\d365fo-index.sqlite-shm" -ErrorAction SilentlyContinue
   ```
4. Re-run extraction, then optimise: `d365fo index optimize`

`index optimize` runs `PRAGMA wal_checkpoint(FULL)` + `PRAGMA optimize` which compacts the WAL and reclaims space. Schedule it periodically in CI.

---

## Bridge child process issues

**Symptom:** Commands that normally use the bridge (`get table`, `find refs --xref`) return stale or incomplete data, or `_source: "index"` when you expect `"bridge"`.

**Checklist:**

| Check | Command |
|---|---|
| Is the bridge enabled? | `echo $env:D365FO_BRIDGE_ENABLED` — must be `1` or `true` |
| Is `D365FO_BIN_PATH` set? | Must point to the D365FO binaries folder (contains `Microsoft.Dynamics.Ax.Xpp.Support.dll`) |
| Is the bridge executable present? | `Test-Path "$env:D365FO_BRIDGE_PATH"` or auto-discovered at `<CLI root>/bin/D365FO.Bridge.exe` |
| Is the target platform Windows? | Bridge is Windows-only; non-Windows always falls back |

Bridge startup errors are written to stderr. Capture them:

```powershell
d365fo get table CustTable --output json 2>bridge-stderr.txt
```

Common message: `Assembly not found: Microsoft.Dynamics.Ax.Xpp.Support`. Fix: ensure `D365FO_BIN_PATH` points to the correct version of the D365FO binaries.

---

## Label language detection

### Expected language code format

Label files follow the pattern `<File>.<lang-tag>.label.txt` (e.g. `ApplicationCommon.en-US.label.txt`, `ApplicationCommon.cs-CZ.label.txt`). Language codes use IETF BCP 47 format (`en-US`, `cs-CZ`, `de-DE`, not `en`, `cs`, `de`).

### Limiting which languages get indexed

There is no `--languages` flag on `index extract` — the language list comes from `D365FO_LABEL_LANGUAGES` (default `en-us`), read the same way as every other setting: env var → `settings.json` → default. Extraction indexes only the languages named there (faster, smaller index than indexing every `.label.txt` file found):

```sh
d365fo init --label-languages en-us,cs-CZ --persist-profile   # writes D365FO_LABEL_LANGUAGES + reruns nothing
d365fo index extract                                          # picks it up; extract always re-populates, no --force
```

Or set it directly for one session: `$env:D365FO_LABEL_LANGUAGES = "en-us,cs-CZ"` before running `index extract`.

### Label not found after extraction

If `d365fo labels resolve @SYS12345 --lang cs-CZ` returns `LABEL_NOT_FOUND`:

1. Check the language code is correct: `d365fo index status --output json` lists indexed languages under `data.languages`.
2. Add it to `D365FO_LABEL_LANGUAGES` (see above) and re-run `d365fo index extract` — extraction is idempotent and always reprocesses every model, so no `--force` is needed (that flag exists only on `index refresh`).
3. Verify the label file exists on disk: `Get-ChildItem $env:D365FO_PACKAGES_PATH -Recurse -Filter "*.cs-CZ.label.txt"`.

---

## Schema migration

### When the index was built with an older schema version

The CLI auto-migrates the schema on first connection via `EnsureSchema()`. This is safe and additive — it never drops existing data.

The migration is applied automatically on first connection and is always additive:

- New columns are added via `ALTER TABLE … ADD COLUMN` with safe defaults (`NULL` or `0`).
- New tables (e.g. `BusinessEvents`, `SecurityPolicies`, `ConfigurationKeys`, `Tiles`) are created empty.
- New virtual tables (e.g. the `MethodSourceFts` method-body index) are created empty.
- Lint flag columns (`HasInsertInLoop`, `HasNestedSelect`, etc.) default to `0` in existing rows.

After migration, newly added columns are empty until you re-extract — `index extract` has no `--force` flag (that's `index refresh` only), but it doesn't need one: every run reprocesses every model and replaces its rows:

```sh
d365fo index extract    # re-populate all models with new columns
```

The opt-in method-body index stays empty until you re-extract *with the flag*:

```sh
d365fo index extract --index-source   # backfill MethodSourceFts too
```

### When to use `index build` vs `index extract`

| Command | What it does | When to use |
|---|---|---|
| `index build` | Creates the SQLite schema (no data) | First time only, or after `index drop` |
| `index extract` | Walks AOT XML and populates the database | After build, after `--force`, or to pick up new objects |
| `index refresh` | Incremental: re-extracts only changed models | Routine use — after editing XML files |
| `index optimize` | WAL checkpoint + ANALYZE | Periodically, or after a large extraction |

If you see `NO_INDEX` errors, run `index build` then `index extract`. If data is stale but the schema is intact, run `index refresh` or `index refresh --force`.

---

## Form pattern gate

### `FORM_PATTERN_VIOLATION` on `generate form`

The generated form failed the structural pattern self-test (rules FP001–FP005,
FP007). The error message lists each violation with its tree path and a fix.

1. `d365fo form-pattern spec <Pattern> --output json` — see the required
   structure (control types, order, allowed sub-patterns).
2. Adjust the `generate form` flags (`--pattern`, `--table`, `--lines-table`,
   `--section`) and retry.
3. After hand edits, re-check with
   `d365fo form-pattern validate <file> --output json` (exit 2 = errors).

To bypass the gate deliberately (e.g. replicating a legacy form), set
`D365FO_FORM_PATTERN_ENFORCE=false` for that invocation. Warnings
(FP006, FP008–FP010, version drift) never block — they surface in the
`warnings` array of the JSON envelope.

### `FP002: version X is newer than the catalog's Y`

A platform update introduced a newer `PatternVersion` than the built-in catalog
knows. This is a warning, not an error — verify the form opens in Visual Studio
and report it so the catalog's version list can be updated.

---

## Copilot isn't finding the instruction files

### "I set `D365FO_WORKSPACE_PATH` / `D365FO_CUSTOM_PACKAGES_PATH` but Copilot still doesn't use the Skills"

These are two completely unrelated concepts that happen to share the word "workspace":

| Term | What it controls |
|---|---|
| `D365FO_WORKSPACE_PATH`, `D365FO_PACKAGES_PATH`, `D365FO_CUSTOM_PACKAGES_PATH` (CLI env vars) | Where the `d365fo` CLI indexes metadata from / writes scaffolded output to |
| The folder/`.sln` open in Visual Studio or VS Code ("workspace" in the IDE sense) | Where Copilot looks for `.github/skills/d365fo-cli/SKILL.md` |

No `D365FO_*` environment variable or `settings.json` entry has any effect on Copilot's instruction discovery. Copilot (both in Visual Studio and VS Code) only walks **upward from the folder/solution you actually opened in the editor** looking for a `.github/` folder — it never reads CLI configuration.

Fix, in order:

1. Confirm which folder is actually open in the editor (Visual Studio: the `.sln`'s folder; VS Code: File → Open Folder).
2. Re-run `Install-D365FoCopilotSkills.ps1` targeting that exact folder (or a parent of it) as `-XppRepo`, not the `D365FO_WORKSPACE_PATH` / `D365FO_CUSTOM_PACKAGES_PATH` value.
3. Visual Studio only: confirm the **GitHub Copilot** extension is enabled and skills auto-discovery is active (look for the `.github/skills/` folder being picked up in Copilot Chat's reference list).
4. No shared parent solution/`.sln` above your projects? Copy the skill to a higher common ancestor folder:
   - Run `Install-D365FoCopilotSkills.ps1 -XppRepo <common-parent>` to place `.github/skills/d365fo-cli/` where Copilot can walk up to it from any solution.
   - Legacy fallback (pre-skill hosts): `skills/copilot/*.instructions.md` are still emitted and can be placed in `.github/instructions/` as before.
5. Verify: after Copilot answers, expand **References / "Used N references"** in the reply — loaded instruction files are listed there. If your file isn't listed, it wasn't discovered.

---

## MCP tool count and token budget

### How many tools are exposed

The MCP adapter (`d365fo-mcp`) exposes **20 consolidated, discriminator-based tools** covering the CLI search, get, find, generate, and analyze surface (a single tool dispatches on a `type` / `objectType` / `mode` / `action` / `domain` / `include` field). Each tool definition is included in the model context on every turn.

### Reducing token usage

The primary way to reduce token cost is to use the CLI directly instead of the MCP adapter:

| Approach | Cost per turn |
|---|---|
| MCP adapter (20 consolidated tools) | ~1,800 tokens injected into every turn |
| CLI + lazy-loaded Skills | ~100 tokens — one shell tool definition |

See [TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) for the full analysis.

### Using targeted search instead of `search any`

`search any` UNIONs every indexed kind, which returns many results and uses more tokens. Prefer targeted commands when you know the object type:

```sh
# Broad — returns hits across all kinds
d365fo search any CustCustom --output json

# Targeted — only tables; fewer results, cheaper
d365fo search table CustCustom --output json

# Batch — multiple targeted lookups in one process call
d365fo search batch CustTable SalesTable CustAccount --output json
```

`--limit N` (default 25) caps result set size on every search command.

---

## See also

- [SETUP.md](SETUP.md) — install, configure, connect an AI agent.
- [EXAMPLES.md](EXAMPLES.md) — one worked example per command.
- [ARCHITECTURE.md](ARCHITECTURE.md) — index schema, lint rules, the daemon.
- [CONFIGURATION.md](CONFIGURATION.md) — every env var and the JSON config file.
