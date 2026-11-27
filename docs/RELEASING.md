# Versioning and releases

How a `d365fo` build is identified, what a release contains, and how to install a specific one.

## Version identity

```sh
d365fo --version
# 0.2.0+5ef6a7d0c0e4…        version + the commit it was built from

d365fo version --output json
# { "version": "0.2.0", "commit": "5ef6a7d0c0e4…", "informationalVersion": "0.2.0+5ef6a7d0c0e4…", … }
```

- The version is set once, in `Directory.Build.props` (`VersionPrefix` and `VersionSuffix`).
  The CLI, `d365fo-cli-mcp` (its `serverInfo.version`), the bridge and SARIF output all report it.
- The commit is added by the .NET SDK when the build runs in a git checkout. A build from a
  source archive reports the version alone. The commit is `HEAD`: uncommitted changes are not
  visible in it.
- A **release** reports a version without a suffix (`0.2.0`). A build between releases reports
  `-dev` (`0.3.0-dev+<commit>`), which names the commit and no release.

## Versioning policy

[Semantic Versioning 2.0.0](https://semver.org/spec/v2.0.0.html). The public surface the number
describes:

- command, argument and option names;
- the JSON envelope (`ok` / `data` / `error.code` / `error.hint`) and the error codes;
- MCP tool names and input schemas;
- configuration keys (environment variables and `settings.json`);
- the index database, as far as it affects users (whether an upgrade needs a re-extract).

| Change | From 1.0.0 | While 0.y.z |
|---|---|---|
| Removes or changes part of that surface | major | minor |
| Adds to it | minor | minor |
| Fixes behaviour only | patch | patch |

While the major version is `0`, a minor release may break compatibility; its notes say what and
how to adapt.

## Tags and release notes

- A release is the annotated tag `v<version>` (`v0.2.0`; a pre-release is `v0.3.0-rc.1`) on a
  commit of `main` whose `Directory.Build.props` states exactly that version.
- **Tags are immutable.** A published tag is never moved or reused. A bad release is superseded
  by the next patch version. Protect `v*` with a repository tag ruleset so this is enforced.
- The notes are the release's `## [<version>] - <date>` section of
  [CHANGELOG.md](../CHANGELOG.md). They state compatibility requirements: whether the index needs
  a re-extract (schema version change), and anything required of the bridge or the D365FO
  platform. GitHub limits the body to 125 000 characters: keep the section a summary and put
  long per-change entries under a separate `## ` heading below it.

## What a release publishes

`.github/workflows/release.yml` runs on the tag, checks it against the source version, runs the
tests, and attaches to the GitHub release:

| Asset | Contents |
|---|---|
| `d365fo-cli-<version>-win-x64.zip` | `bin\d365fo.exe`, `mcp\d365fo-cli-mcp.exe`, `D365FO.Bridge\` |
| `d365fo-cli-<version>-linux-x64.tar.gz` | `bin/d365fo`, `mcp/d365fo-cli-mcp` |
| `d365fo-cli-<version>-osx-x64.tar.gz` | `bin/d365fo`, `mcp/d365fo-cli-mcp` |
| `d365fo-cli-<version>-osx-arm64.tar.gz` | `bin/d365fo`, `mcp/d365fo-cli-mcp` |
| `SHA256SUMS` | Checksums of the archives |

The binaries are self-contained (no .NET runtime needed; the bridge uses the .NET Framework 4.8
that a D365FO VM already has). They are not code-signed. There is no package registry.

## CLI and bridge compatibility

The CLI and `D365FO.Bridge.exe` talk over a JSON protocol that has no version negotiation, so the
only supported combination is **both from the same release (the same commit)**. Release archives
and the installers keep them together. `d365fo doctor` reports `bridge.version` and warns when the
bridge it would start is a different build.

## Installing a specific release

With the installer (builds from the tagged source):

```powershell
$env:D365FO_CLI_VERSION = 'v0.2.0'
irm https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main/install.ps1 | iex
```

```sh
curl -fsSL https://raw.githubusercontent.com/dynamics365ninja/d365fo-cli/main/install.sh | D365FO_CLI_VERSION=v0.2.0 bash
```

The same command with another tag upgrades or downgrades. Without the variable the installer
returns the checkout to `main` and builds its latest commit.

From a release archive: download it, verify it against `SHA256SUMS`, extract it, and put `bin` on
`PATH`. Keep `D365FO.Bridge` beside `bin`, where the CLI looks for it.

From source:

```sh
git clone --branch v0.2.0 https://github.com/dynamics365ninja/d365fo-cli.git
```

## Cutting a release

1. On a branch: remove `<VersionSuffix>` from `Directory.Build.props` (set `VersionPrefix` if the
   changes call for a different number than the one in development).
2. In `CHANGELOG.md`, rename `## [Unreleased]` to `## [<version>] - <date>`, add an empty
   `## [Unreleased]` above it, and write the compatibility notes. Check the result with
   `./scripts/get-release-notes.ps1 -Version <version>`.
3. Open a pull request and merge it once CI is green.
4. Tag the merge commit and push the tag:

   ```sh
   git switch main && git pull --ff-only
   git tag -a v<version> -m "d365fo-cli <version>"
   git push origin v<version>
   ```

5. Check the release workflow and the published release.
6. In a follow-up pull request, set `VersionPrefix` to the next version and restore
   `<VersionSuffix>dev</VersionSuffix>`.
