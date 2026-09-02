# Adding a command

What a new `d365fo` command has to satisfy before it is real. Most of it is enforced — the list
exists so you meet the gates on purpose rather than by bisecting a red build.

---

## 1. The command itself

`src/D365FO.Cli/Commands/<Area>/<Name>Command.cs`, registered in `CliApp.cs` under its branch.

```csharp
public sealed class ThingListCommand : Command<ThingListCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("What this argument is, in the words a caller would use.")]
        public string Name { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings) { … }
}
```

Non-negotiables, each learned from a defect:

- **The settings type must be concrete and constructible.** All seven `remove-*` commands once
  compiled, registered, appeared in `--help` and died on call with "Could not resolve type",
  because their settings inherited an abstract base. `CommandSurfaceTests` constructs every
  registered command's settings type, so that class of failure now fails the build.
- **Return a `ToolResult` envelope**, rendered through `RenderHelpers.Render(kind, …)`. Every
  caller — human, agent, MCP adapter — depends on `ok` / `data` / `error.code` / `error.hint`.
- **A refusal names the fix.** `error.hint` is the difference between a wall and a door.
- **Never accept an option and quietly drop it.** `generate table-relation` refuses `--out`
  outright rather than pretending: a caller who passed it meant something by it.

## 2. Registries, if the command is about an AOT type

Four registries describe the object model, and the tests hold them to each other:

| Registry | Add a row when |
|---|---|
| `ObjectTypeRegistry` | The command reads or writes a new AOT family. The row carries the folder, root element, provider collection, namespace, and the `generate` subcommand that builds it. |
| `GenerateSurface` | The command is a new `generate` subcommand. The row names the root elements one invocation writes — including the companions. |
| `MetadataContracts` | Never by hand: regenerate with `scripts/emit-metadata-contracts.ps1` on a machine with the metadata assemblies. |
| `FormPatternCatalog` / `TablePattern` | The command adds a pattern. |

`ObjectTypeRegistryAotTests` checks the row against the real AOT on the machine running it — a
guessed folder name or root element fails there rather than in a customer's model. Wave 05 found
three families whose registry rows named no generator while `generate extension` was building all
three, and the coverage report had been reporting them as untooled for exactly as long.

## 3. The MCP side

`CliMcpParityTests` fails the build if a command is neither published in the manifest with an
`mcpTool` route, nor listed with a written reason. Pick one:

- **Route it.** Add the tool or the discriminator in `src/D365FO.Mcp/ToolCatalog.cs` and
  `ToolHandlers`, and declare it on the command's manifest entry.
- **Declare it out of scope**, in `CliMcpParityTests.OutOfScope`, with a sentence saying why an
  agent holding an MCP connection has nothing to do with it. "Eval harness", "operator task",
  "shell integration" are the existing shapes.

Then regenerate the map:

```sh
python scripts/emit-mcp-tools.py
```

The generator refuses to write a mapping where the manifest and the adapter disagree about which
tools exist, and CI runs it with `--check`.

## 4. Tests

At minimum:

- A test of the behaviour, named after what it prevents.
- If it writes an artefact: an **eval case** with a golden captured through the real CLI
  (`d365fo eval capture`), never hand-authored. See [AGENT_EVAL_LOOP.md](AGENT_EVAL_LOOP.md).
- If it is a `generate` subcommand: check `d365fo eval coverage` — the new leaf wants K, E and T,
  and the report will tell you which of the three you are missing.

```sh
dotnet test d365fo-cli.slnx -c Release
dotnet run --project src/D365FO.Cli -c Release -- eval run --all
dotnet run --project src/D365FO.Cli -c Release -- eval coverage --check
```

## 5. Knowledge

A command an agent cannot find is a command that does not exist. Add or extend a topic in
`skills/_source/` so the corpus names it — [KNOWLEDGE_AUTHORING.md](KNOWLEDGE_AUTHORING.md) — then
`python scripts/emit-skills.py` and `d365fo knowledge audit --verify`.

## 6. Documentation

| File | What to add |
|---|---|
| [CAPABILITIES.md](CAPABILITIES.md) | The command, its options, and the judgement behind them |
| [EXAMPLES.md](EXAMPLES.md) | One worked invocation |
| [USAGE_EXAMPLES.md](USAGE_EXAMPLES.md) | Only if it changes how a whole task is done |
| `CHANGELOG.md` | What changed and why — the reason, not just the noun |

## 7. The gates, in the order CI runs them

```sh
dotnet test d365fo-cli.slnx -c Release
./scripts/check-build-warnings.ps1                # ratchet: never up, and lower it when it drops
dotnet run --project src/D365FO.Cli -c Release -- eval run --all
dotnet run --project src/D365FO.Cli -c Release -- eval coverage --check
dotnet run --project src/D365FO.Cli -c Release -- knowledge audit --verify
python scripts/emit-skills.py && git status --porcelain skills/
python scripts/emit-mcp-tools.py --check
```

And, if you have a D365FO VM and touched a scaffolder or a rule:

```sh
dotnet run --project src/D365FO.Cli -c Release -- eval verify-build   # goldens still compile
dotnet run --project src/D365FO.Cli -c Release -- oracle sweep        # zero errors on shipped X++
```

---

## The two questions worth asking first

**Does the command tell the truth when it cannot answer?** A degraded zero — "no callers" because
the corpus could not be read — is worse than an error, because it reads as a fact. Every read path
in this repository that can degrade says so in its payload.

**Would an agent reach for this, or for something else?** A command that duplicates an existing
surface splits the knowledge corpus and the eval coverage between two spellings of the same thing.
Wave 04 removed `find extensions --merged` for that reason a week after adding it.
