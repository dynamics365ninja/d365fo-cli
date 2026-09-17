# Configuration

Run `d365fo init` — in a terminal it's a wizard that asks for what you need and writes `settings.json` (`--persist-profile`); nothing below has to be edited by hand. The tables in this page document what it can write and what every setting does.

`d365fo` resolves settings in the following priority order — first match wins:

1. **Explicit CLI flags** (`--packages`, `--db`, …) — highest priority
2. **Process environment variables** (`D365FO_PACKAGES_PATH`, …)
3. **Active named profile** (`%LOCALAPPDATA%\d365fo-cli\profiles\<name>.json`) — only when a profile is selected; see [Named profiles](#named-profiles)
4. **JSON config file** (`%LOCALAPPDATA%\d365fo-cli\settings.json` on Windows, `~/.local/share/d365fo-cli/settings.json` on Linux/macOS)
5. **Built-in defaults** (e.g. `en-us` language, default SQLite path)

With no profile selected, step 3 does not exist and resolution is exactly what it was before profiles were added.

---

## Environment variables

| Variable | Purpose | Default |
|---|---|---|
| `D365FO_PACKAGES_PATH` | Primary `PackagesLocalDirectory` root | *(required for indexing)* |
| `D365FO_CUSTOM_PACKAGES_PATH` | Additional `PackagesLocalDirectory` roots (semicolon/comma separated). Indexed for reads **and** forwarded to the bridge so `generate --install-to <Model>` can resolve custom models that live outside the primary packages path (UDE dual-folder setups). Formerly named `D365FO_EXTRA_PACKAGES_PATH` — the old name is still honored as a fallback when the new one is unset, but is deprecated; `d365fo doctor` warns when it is in use. | — |
| `D365FO_LABEL_LANGUAGES` | Label languages to extract, e.g. `en-us,cs,de` | `en-us` |
| `D365FO_INDEX_DB` | Path to the SQLite index file | `%LOCALAPPDATA%\d365fo-cli\d365fo-index.sqlite`; with a profile active, `%LOCALAPPDATA%\d365fo-cli\profiles\<name>\d365fo-index.sqlite` |
| `D365FO_PROFILE` | Selects a [named profile](#named-profiles). Read from the process env or `settings.json` (where `d365fo config use` writes it), never from a profile file | — |
| `D365FO_CONFIG_DIR` | Relocates the whole config root (`settings.json`, `profiles\`, default index DB). Process env only. Useful for throw-away or portable configs — Windows ignores a changed `LOCALAPPDATA` env var | `%LOCALAPPDATA%\d365fo-cli` |
| `D365FO_WORKSPACE_PATH` | Root of your X++ solution (enables scaffold output) | — |
| `D365FO_CUSTOM_MODELS` | Comma-separated list of custom model names | — |
| `D365FO_BRIDGE_ENABLED` | `1`/`true` enables the metadata bridge (required for `generate --install-to` and `find refs --xref`) | `false` |
| `D365FO_BRIDGE_PATH` | Path to `D365FO.Bridge.exe` (auto-detected next to `d365fo.exe` when unset) | *(auto)* |
| `D365FO_BIN_PATH` | Folder containing `Microsoft.Dynamics.AX.Metadata.*.dll` (usually `<PackagesLocalDirectory>\bin`) | — |
| `D365FO_MSBUILD_PATH` | MSBuild executable for `d365fo build` on non-X++ projects (an `.rnrproj` is compiled with `LabelC.exe` + `xppc.exe`, because its MSBuild tasks run only inside Visual Studio). Unset, the Visual Studio MSBuild is resolved through `vswhere` (then the known install roots) and only then `PATH` — the `msbuild.exe` first on `PATH` is usually the .NET Framework one, which cannot load the X++ build tasks and fails with `MSB4062`. `d365fo doctor` reports which one is resolved. | *(auto)* |
| `D365FO_XREF_CONNECTIONSTRING` | Cross-reference DB connection string | — |
| `D365FO_FORCE_JSON` | `1` forces machine-readable JSON output even on a TTY | — |
| `D365FO_HOME` | Root for the provenance/grounding token store | `%USERPROFILE%\.d365fo` |
| `D365FO_GROUNDING_ENFORCE` | `true` = `generate` rejects writes without a valid grounding token or with unresolved references / BP errors | `false` (warn only) |
| `D365FO_FORM_PATTERN_ENFORCE` | `false` = disable the form-pattern write gate; by default `generate form` rejects structural pattern violations (FP001–FP005, FP007) | `true` |

> ⚠️ **`D365FO_WORKSPACE_PATH` is not the same thing as your editor's "workspace".** This variable only tells the `d365fo` CLI where scaffolded X++ output should be written. It has **no effect** on where Visual Studio or VS Code loads GitHub Copilot instruction files from — that is determined purely by the folder/`.sln` you have open in the editor (see [SETUP.md — Connect your AI agent](SETUP.md#step-5--connect-your-ai-agent)). Setting `D365FO_WORKSPACE_PATH` does **not** make Copilot pick up `.github/copilot-instructions.md` from that path.

### `d365fo-mcp`-only variables

Read the same way (env var → `settings.json` → default) but only consulted by
the `d365fo-mcp` server, not the `d365fo` CLI. See
[MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md#http-transport--shared-deployment-azure-app-service)
for the shared-deployment story these enable.

| Variable | Purpose | Default |
|---|---|---|
| `MCP_SERVER_MODE` | `full` \| `read-only` \| `write-only` — gates which MCP tools are exposed/callable (both transports) | `full` |
| `API_KEY` | Shared secret required in the `X-Api-Key` header on `POST /mcp` (`--http` transport only). Unset = unauthenticated, with a startup warning | — |
| `MCP_HTTP_PORT` | Listen port for `d365fo-mcp --http` when `--port` isn't passed | `3000` |

---

## JSON config file

The JSON config file is the **recommended** way to persist settings when running `d365fo` from multiple shell hosts (Windows PowerShell 5.1, PowerShell 7, Visual Studio Developer PowerShell, CI, etc.) because it is not tied to any shell profile.

### Location

| OS | Default path |
|---|---|
| Windows | `%LOCALAPPDATA%\d365fo-cli\settings.json` |
| Linux | `~/.local/share/d365fo-cli/settings.json` |
| macOS | `~/Library/Application Support/d365fo-cli/settings.json` |

### Format

A flat JSON object mapping variable names to string values:

```json
{
  "D365FO_PACKAGES_PATH": "K:\\AosService\\PackagesLocalDirectory",
  "D365FO_INDEX_DB": "C:\\Users\\you\\AppData\\Local\\d365fo-cli\\d365fo-index.sqlite",
  "D365FO_LABEL_LANGUAGES": "en-us,cs"
}
```

### Creating / updating the file

Run `d365fo init --persist-profile` — this writes (or updates) both the JSON config file and the shell profiles for all PowerShell versions found on the machine.

To write the file manually, create it at the path above. Only the keys you need to override have to be present; missing keys fall back to environment variables and then built-in defaults.

> **Every variable in the table above can be set in the JSON config file** — it is a complete alternative to environment variables. Each key is resolved with the same precedence (CLI flag → environment variable → JSON config → default), so a value set only in `settings.json` is honored everywhere, including the bridge stack (`D365FO_BRIDGE_ENABLED`, `D365FO_BRIDGE_PATH`, `D365FO_BIN_PATH`).

---

## Named profiles

One machine, several customers: each with its own UDE, `PackagesLocalDirectory`, custom-packages root and index. A **profile** is a file `%LOCALAPPDATA%\d365fo-cli\profiles\<name>.json` with exactly the same flat format as `settings.json`. Names match `^[A-Za-z0-9][A-Za-z0-9._-]*$`.

```powershell
d365fo --profile contoso init --packages K:\AosService\PackagesLocalDirectory --extra-packages C:\git\contoso\Metadata
d365fo --profile fabrikam init --packages J:\AosService\PackagesLocalDirectory

d365fo --profile contoso index extract        # one call
$env:D365FO_PROFILE = 'contoso'               # this shell only
d365fo config use contoso                     # persisted default (settings.json)
d365fo config use --clear                     # back to plain settings.json
```

### Which profile is active

First match wins:

1. the global `--profile <name>` option (anywhere on the command line, also `--profile=<name>`; everything after a literal `--` is left alone),
2. the `D365FO_PROFILE` environment variable,
3. `D365FO_PROFILE` in the global `settings.json` (written by `d365fo config use <name>`).

`d365fo config current` and `d365fo config list` say which one is active and which of these selected it; `d365fo doctor` reports it as the first check.

### What a profile changes

- **Values:** a key in the profile file overrides the same key in `settings.json`; keys the profile does not set still fall through to `settings.json`. **Process environment variables still win over the profile** — if an older `d365fo init --persist-profile` left a `$env:D365FO_PACKAGES_PATH = …` block in your PowerShell `$PROFILE`, that value pins every profile. `d365fo doctor` warns about it (`config.profile (env overrides)`) and `d365fo config show` prints `env` as the source; remove the block to let profiles apply.
- **Index DB:** each profile gets its own default index, `profiles\<name>\d365fo-index.sqlite`. A `D365FO_INDEX_DB` in the global `settings.json` is deliberately **not** inherited by a profile (every non-profile `init --persist-profile` writes one, and inheriting it would make all environments share one index). Set `D365FO_INDEX_DB` in the profile file or the environment to choose another path.
- **Guardrail:** selecting a profile that does not exist (or has an invalid name) is a hard error — `PROFILE_NOT_FOUND` / `INVALID_PROFILE_NAME` — never a silent fallback to the global settings. Only `init`, `config`, `doctor`, `version` and `--help` still run, so you can create or inspect it.

### Commands

| Command | What it does |
|---|---|
| `d365fo --profile <name> init …` | Writes the profile file instead of `settings.json` (persisting is implied) and leaves your shell profile alone. `D365FO_PROFILE=<name>` + `init --persist-profile` does the same. |
| `d365fo config list` | All profiles, the active one, and why it is active. |
| `d365fo config show [name]` | Every setting with its resolved value and source (`env`, `profile:<name>`, `settings`, `default`). Secret-looking keys (`API_KEY`, connection strings) are masked. |
| `d365fo config use <name>` | Persists the default profile in `settings.json`; prints the `$env:D365FO_PROFILE='<name>'` alternative for a per-shell choice. `--create` makes an empty profile file first; `--clear` unsets the default. |
| `d365fo config current` | The active profile, its source, and the index DB it resolves to. |

All of them accept `--output json`.

### MCP server

`d365fo-mcp` resolves settings through the same chain, so a profile can be pinned per MCP client — either `"env": { "D365FO_PROFILE": "contoso" }` in the client's server config or `"args": ["--profile", "contoso"]`. A missing profile makes the server exit at start-up with the same `PROFILE_NOT_FOUND` error instead of serving another environment's index.

### Not (yet) supported

Per-repository configuration (a `.d365fo/settings.json` discovered from the working directory, the way git and npm find theirs) is a possible follow-up; today, pick the profile explicitly.

---

## Developer PowerShell in Visual Studio

VS Developer PowerShell is **Windows PowerShell 5.1** (`powershell.exe`), which reads a **different** `$PROFILE` than PowerShell 7 (`pwsh.exe`):

| Shell | Profile |
|---|---|
| Windows PowerShell 5.1 (VS Developer PowerShell) | `%USERPROFILE%\Documents\WindowsPowerShell\Microsoft.PowerShell_profile.ps1` |
| PowerShell 7+ (`pwsh`) | `%USERPROFILE%\Documents\PowerShell\Microsoft.PowerShell_profile.ps1` |

If you only set env vars in one profile, `d365fo doctor` will report different results from the other shell. **Use the JSON config file** (via `d365fo init --persist-profile`) to avoid this.

Alternatively, set variables at **machine scope** so they are inherited by every process:

```powershell
[System.Environment]::SetEnvironmentVariable(
    "D365FO_PACKAGES_PATH",
    "K:\AosService\PackagesLocalDirectory",
    [System.EnvironmentVariableTarget]::Machine)
```

---

## See also

- [SETUP.md](SETUP.md) — install and the `d365fo init` wizard.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — env var / profile disagreements and other failure modes.
- [ARCHITECTURE.md](ARCHITECTURE.md) — where the bridge and MCP variables plug in.
