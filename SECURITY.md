# Security Policy

## Supported versions

This repository has no release tags yet. Security fixes land on `main`, and installations made
with `install.ps1` / `install.sh` are updated in place by re-running the installer. Once the
first tag is cut, this table will name the supported minor versions.

| Version | Supported |
|---------|-----------|
| `main`  | ✅ |

## Reporting a vulnerability

**Please do not open a public issue.**

Use GitHub's private vulnerability reporting:
**[Report a vulnerability](https://github.com/dynamics365ninja/d365fo-cli/security/advisories/new)**
(Security → Advisories → Report a vulnerability)

This opens a private thread visible only to maintainers, and lets us credit you in the published
advisory.

Helpful to include:

- Affected commit, and whether you are running the plain `d365fo` binary, the `d365fo daemon`,
  or the `d365fo-mcp` adapter (stdio or `--http`)
- The file and line, or a command that demonstrates the behaviour
- What an attacker gains — reading indexed source, writing into `PackagesLocalDirectory`,
  or executing on the build VM

### What to expect

| Stage | Target |
|-------|--------|
| Acknowledgement | 3 working days |
| Initial assessment | 10 working days |
| Fix released | 90 days, sooner for actively exploitable issues |

We request a CVE from the GitHub CNA for anything we assess as valid, and publish the advisory
once the fix ships. You will be credited under the name or handle you choose unless you ask
otherwise.

## Threat model

Some context on what this project treats as a vulnerability, so reports land accurately.

**The index is sensitive.** The SQLite index is built from your `PackagesLocalDirectory`. It
contains X++ source snippets, extension and event-handler wiring, security roles, privileges and
duties, and label text — including your own custom and ISV models, not just the standard
Microsoft ones. Treat any unauthenticated read path into it as a disclosure of proprietary
source. The same applies to an index moved between machines with `index export` / `index import`.

**The CLI writes to the AOT.** `generate --install-to`, `modify`, `labels create|rename|delete`
and `delete` write into `PackagesLocalDirectory` and register objects in `.rnrproj` files.
`build`, `sync`, `test run` and `bp check` execute Microsoft's own tooling on the VM. Anything
that lets those escape their configured root, or run against a model the caller did not name, is
in scope.

**In scope**

- Reaching `d365fo-mcp --http` tools without valid authentication (`API_KEY`)
- Reaching the `d365fo daemon` named pipe from a security context that should not have it
- Reading indexed metadata across a boundary that should have separated it
- Write paths escaping the configured metadata root, or a path traversal through an object name,
  model name or `--out` value
- Command injection through any argument that reaches MSBuild, `xppc`, `xppbp`, `SyncEngine`
  or `SysTestConsole`
- Credential leakage into logs, command output, or error envelopes

**Out of scope**

- `MCP_SERVER_MODE` — a tool-surface partition for the hybrid deployment, not a privilege
  boundary. It limits which tools are reachable, not who may reach them.
- The grounding gate (`D365FO_GROUNDING_ENFORCE`) and the form-pattern gate
  (`D365FO_FORM_PATTERN_ENFORCE`) — correctness gates against hallucinated code, not security
  controls. Bypassing one is a bug, not a vulnerability.
- Findings that require an attacker who already has filesystem access to the VM, the API key, or
  write access to `PackagesLocalDirectory`
- Vulnerabilities in D365 F&O, Visual Studio, or the Microsoft-supplied metadata assemblies
  themselves

## Hardening

- **Do not publish the index.** It is proprietary source in a single file. `index export`
  produces a portable copy — treat it like the codebase it came from.
- **`d365fo-mcp --http` requires `API_KEY`.** Put it behind a private endpoint or IP
  restrictions where the team's addresses are known, and rotate the key when someone leaves the
  project — it is a single shared secret.
- **Run the read-only surface where you can.** A shared instance serving search/get/prepare
  needs none of the write or SDLC commands.
- **The daemon is a local named pipe.** It is reachable by anything running as that user; do not
  run it on a shared interactive host.
