# Quick start

Five minutes from a clean machine to an agent that can answer questions about your D365FO
metadata. [SETUP.md](SETUP.md) is the same path with every branch and pitfall spelled out; this is
the straight line through it.

**You need:** .NET 10 SDK, and a machine with a `PackagesLocalDirectory` on it (a D365FO
development VM, or a copy of the package tree).

---

## 1. Install

```sh
git clone https://github.com/dynamics365ninja/d365fo-cli
cd d365fo-cli
dotnet build -c Release
```

Then either alias it or publish a binary:

```sh
# PowerShell — add to $PROFILE
function d365fo { dotnet run --project K:/repos/d365fo-cli/src/D365FO.Cli -c Release -- @args }

# or a real executable
dotnet publish src/D365FO.Cli -c Release -r win-x64 -o C:/tools/d365fo
```

## 2. Point it at the installation

```sh
d365fo init            # writes ~/.d365fo/config.json after probing for the usual locations
d365fo doctor          # says what is missing, in the order it matters
```

`init` guesses; `doctor` checks. If the guess was wrong, one environment variable fixes it:

```sh
$env:D365FO_PACKAGES_PATH = "K:\AosService\PackagesLocalDirectory"
```

## 3. Build the index

```sh
d365fo index build      # create the SQLite schema
d365fo index extract    # ingest metadata — minutes, parallel per model
```

This is the only slow step, and it is once per installation. On the development VM this
repository is built against, the result is 203 models: 18 467 tables, 60 927 classes,
525 714 methods, 1 420 121 labels — a 750 MB SQLite file that answers in milliseconds.

Scope it while you are trying things out:

```sh
d365fo index extract --model ApplicationSuite   # or your own model, which takes seconds
```

## 4. Ask it something

```sh
d365fo get table CustTable --output json
d365fo search class "SalesLine" --limit 5
d365fo find coc CustTable::validateWrite
d365fo prepare change CustTable --goal "add a fleet reference field"
```

`prepare` is the one worth learning first: it answers "what am I about to change, what already
extends it, and which strategy applies" in a single call, and hands back a **grounding token**
that `generate` will ask for.

## 5. Generate something

```sh
d365fo generate extension Table CustTable --suffix Fleet --out ./CustTable.Fleet.xml
d365fo validate xpp ./CustTable.Fleet.xml --output json
```

Never hand-write AOT XML — the schema is proprietary and easy to get subtly wrong in ways that
parse, pass review, and drop your data on read. Every scaffold goes through `generate`, and
`validate` is the offline check that runs without a compiler.

## 6. Give an agent the same access

```powershell
# Claude Code / Claude Desktop — copies the per-topic skills into <XppRepo>/.claude/skills/
.\scripts\Install-D365FoClaudeSkills.ps1 -XppRepo "K:\D365FO\MyProject"

# GitHub Copilot in Visual Studio / VS Code
.\scripts\Install-D365FoCopilotSkills.ps1 -XppRepo "K:\D365FO\MyProject"
```

Both installers read the same `skills/_source/` topics, so the two ecosystems cannot disagree
about a rule, and both remove topics that were retired upstream rather than leaving them to feed
an agent stale guidance.

For an MCP-only host, see [MCP_CONFIG.md](MCP_CONFIG.md) — the bundled `d365fo-mcp` adapter
serves the same index over JSON-RPC. If your agent has a shell, prefer the CLI:
[TOKEN_ECONOMICS.md](TOKEN_ECONOMICS.md) measures why.

---

## What to read next

| You want to | Read |
|---|---|
| The install path with every option and pitfall | [SETUP.md](SETUP.md) |
| Everything the tool can do | [CAPABILITIES.md](CAPABILITIES.md) |
| Worked end-to-end tasks, not one command at a time | [USAGE_EXAMPLES.md](USAGE_EXAMPLES.md) |
| One example per command | [EXAMPLES.md](EXAMPLES.md) |
| Something is wrong | [TROUBLESHOOTING.md](TROUBLESHOOTING.md) |
| Every environment variable | [CONFIGURATION.md](CONFIGURATION.md) |
