# Configuration

`d365fo` resolves settings in the following priority order — first match wins:

1. **Explicit CLI flags** (`--packages`, `--db`, …) — highest priority
2. **Process environment variables** (`D365FO_PACKAGES_PATH`, …)
3. **JSON config file** (`%LOCALAPPDATA%\d365fo-cli\settings.json` on Windows, `~/.local/share/d365fo-cli/settings.json` on Linux/macOS)
4. **Built-in defaults** (e.g. `en-us` language, default SQLite path)

---

## Environment variables

| Variable | Purpose | Default |
|---|---|---|
| `D365FO_PACKAGES_PATH` | Primary `PackagesLocalDirectory` root | *(required for indexing)* |
| `D365FO_CUSTOM_PACKAGES_PATH` | Additional `PackagesLocalDirectory` roots (semicolon/comma separated). Indexed for reads **and** forwarded to the bridge so `generate --install-to <Model>` can resolve custom models that live outside the primary packages path (UDE dual-folder setups). Formerly named `D365FO_EXTRA_PACKAGES_PATH` — the old name is still honored as a fallback when the new one is unset, but is deprecated; `d365fo doctor` warns when it is in use. | — |
| `D365FO_LABEL_LANGUAGES` | Label languages to extract, e.g. `en-us,cs,de` | `en-us` |
| `D365FO_INDEX_DB` | Path to the SQLite index file | `%LOCALAPPDATA%\d365fo-cli\d365fo-index.sqlite` |
| `D365FO_WORKSPACE_PATH` | Root of your X++ solution (enables scaffold output) | — |
| `D365FO_CUSTOM_MODELS` | Comma-separated list of custom model names | — |

> ⚠️ **`D365FO_WORKSPACE_PATH` is not the same thing as your editor's "workspace".** This variable only tells the `d365fo` CLI where scaffolded X++ output should be written. It has **no effect** on where Visual Studio or VS Code loads GitHub Copilot instruction files from — that is determined purely by the folder/`.sln` you have open in the editor (see [SETUP.md — Connect your AI agent](SETUP.md#step-5--connect-your-ai-agent)). Setting `D365FO_WORKSPACE_PATH` does **not** make Copilot pick up `.github/copilot-instructions.md` from that path.
| `D365FO_BRIDGE_ENABLED` | `1`/`true` enables the metadata bridge (required for `generate --install-to` and `find refs --xref`) | `false` |
| `D365FO_BRIDGE_PATH` | Path to `D365FO.Bridge.exe` (auto-detected next to `d365fo.exe` when unset) | *(auto)* |
| `D365FO_BIN_PATH` | Folder containing `Microsoft.Dynamics.AX.Metadata.*.dll` (usually `<PackagesLocalDirectory>\bin`) | — |
| `D365FO_XREF_CONNECTIONSTRING` | Cross-reference DB connection string | — |
| `D365FO_FORCE_JSON` | `1` forces machine-readable JSON output even on a TTY | — |
| `D365FO_HOME` | Root for the provenance/grounding token store | `%USERPROFILE%\.d365fo` |
| `D365FO_GROUNDING_ENFORCE` | `true` = `generate` rejects writes without a valid grounding token or with unresolved references / BP errors | `false` (warn only) |
| `D365FO_FORM_PATTERN_ENFORCE` | `false` = disable the form-pattern write gate; by default `generate form` rejects structural pattern violations (FP001–FP005, FP007) | `true` |

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
