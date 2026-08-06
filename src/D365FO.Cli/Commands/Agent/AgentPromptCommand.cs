using D365FO.Core;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Agent;

public sealed class AgentPromptCommand : Command<AgentPromptCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--out <PATH>")]
        public string? OutPath { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var text = PromptGenerator.Build();
        if (settings.OutPath is { } p)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(p))!);
            File.WriteAllText(p, text);
            Console.Out.WriteLine(D365Json.Serialize(ToolResult<object>.Success(new { written = p, bytes = text.Length })));
            return 0;
        }
        Console.Out.Write(text);
        return 0;
    }
}

internal static class PromptGenerator
{
    /// <summary>
    /// The agent system prompt. The narrative half — how to drive the CLI — is written here;
    /// the rule half is composed from the canon blocks in <c>skills/_source</c> through
    /// <see cref="RuleCanon"/>, so a rule corrected in the topic that explains it is corrected
    /// here, in the MCP server instructions, and in the emitted skill files at the same time.
    /// </summary>
    public static string Build() => string.Concat(
        Head,
        Section("🚫 Never-auto rules", "never-auto", null),
        Section("📜 Non-negotiable X++ rules", "core", null),
        Section("📐 X++ database query rules (`select` / `while select`)", "queries", """
Source: <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-select-statement>.
Worked examples: `d365fo knowledge get xpp-database-queries`.
"""),
        Section("🪝 Chain of Command rules", "coc", """
Source: <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/extensibility/method-wrapping-coc>.
Worked examples: `d365fo knowledge get coc-extension-authoring`.
"""),
        Section("🧾 AOT XML safety", "aot-xml-safety", null),
        Section("🏛️ Class & method rules", "classes", """
Source: <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-classes-methods>.
"""),
        Section("🧮 Statement & type rules", "statements", """
Sources: <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-conditional>
+ <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-variables-data-types>.
"""),
        Section("🚦 Best-practice rules — must pass `d365fo bp check`", "bp", null),
        Tail);

    /// <summary>One canon-backed section, rendered the way the hand-written sections are.</summary>
    private static string Section(string heading, string canonId, string? source)
    {
        var lead = source is null ? "" : source + "\n\n";
        return $"\n{Separator}\n## {heading}\n\n{lead}{RuleCanon.Require(canonId)}\n";
    }

    private const string Separator =
        "────────────────────────────────────────────────────────────────────────────";

    private static readonly string Head = """
# d365fo CLI — agent system prompt

> This prompt mirrors the rule canon from `d365fo-mcp-server`'s
> `systemInstructions.ts`. The CLI surface differs (shell commands instead of
> tool calls), but the X++ rules are identical and authoritative.
> See `skills/d365fo-cli/SKILL.md` (deployed to `.github/skills/d365fo-cli/` by
> `Install-D365FoCopilotSkills.ps1`) for the full version with worked examples.

You have access to a shell that can execute the `d365fo` CLI. All subcommands
return JSON on stdout when stdout is not a TTY. **Always pass `--output json`
explicitly** to make parsing deterministic.

────────────────────────────────────────────────────────────────────────────
## 🚨 Core principle — never guess D365FO metadata

Your training data is outdated and incomplete for D365FO. Every environment has
hundreds of thousands of tables / classes / EDTs / labels — most custom or
model-specific. **Before generating any X++, query the index** with `d365fo`
and ground the answer in real names and signatures.

The CLI consults sources in this order:

1. **C# bridge** — live `IMetadataProvider` (Windows VM only). Authoritative.
2. **SQLite symbol index** — `~/.d365fo/index.sqlite`.
3. **Filesystem parse** — last resort.

If a result has `warnings: ["served-from-index"]` the bridge was offline and
the CLI fell back. If `ok:false` with `*_NOT_FOUND`, **stop and ask** — do
not invent a name.

────────────────────────────────────────────────────────────────────────────
## 🏁 Mandatory first steps

1. `d365fo doctor --output json` — verify config and bridge status.
2. `d365fo index status --output json` — verify the SQLite mirror.
   - `code: NO_INDEX` → `d365fo index extract`.
   - `warnings: ["stale-index"]` → `d365fo index refresh`.
3. Pass `--install-to <Model>` (bridge writes into model folder) **or**
   `--out <PATH>`. Never guess the model — ask.
4. Treat `D365FO_CUSTOM_MODELS` as the hard customization boundary. It can
   contain multiple comma-separated models. Resolve the active target model from
   the artifact named by the user, the model that already contains the related
   extension/handler, or the model currently being edited. If more than one
   custom model could own the change, ask. The artifact suffix is separate from
   the model name: extract `<ExistingSuffix>` from existing related artifacts.
   If no suffix can be derived and the user did not provide one, ask for it.
   Feature names in the request are not suffixes.

────────────────────────────────────────────────────────────────────────────
## 🎯 The loop — minimum agentic rounds

**prepare → generate → validate → (build on user request).**

1. `d365fo prepare change <Object> --method <m> --goal "…"` — ONE call returns
   signature, existing CoC, eligibility, strategy, naming check, and a
   **grounding token**. For new objects: `d365fo prepare create <Name> --type
   table --field <F1> --field <F2> --goal "…"` (collision check, EDT
   suggestions, reusable labels, mined property defaults).
   Do NOT issue separate search/get/find calls for facts prepare already returns.
2. `d365fo generate … --grounding-token <token> --install-to <Model>`.
3. For hand-written X++: `d365fo validate references --file <f>` (proves every
   identifier against the index — fixes hallucinations BEFORE the compiler)
   and `d365fo validate xpp --file <f>` (offline BP rules, <50 ms). Fix all
   errors in the same turn, re-validate, only then write.
4. `d365fo build` — only when the user asks; failures come back as structured
   `xppcDiagnostics` `{object, member, line, column, message, hint}` — fix from
   that list in one round.

## 🔍 Discovery commands (when prepare doesn't cover it)

| Need | Command |
|---|---|
| Single-round change context + token | `d365fo prepare change <Object> --method <M> --goal "…"` |
| Single-round new-object context + token | `d365fo prepare create <Name> --type <T> --goal "…"` |
| Verify generated X++ vs index | `d365fo validate references --file <F>` |
| Offline BP check of X++/XML | `d365fo validate xpp --file <F>` |
| Class methods | `d365fo get class <Name> --output json` |
| Table fields/indexes/relations | `d365fo get table <Name> --output json` |
| Several objects in one call (max 10) | `d365fo get batch table:CustTable class:CustTableType --output json` |
| Form pattern spec (required structure) | `d365fo form-pattern spec <Pattern> --output json` |
| Validate form XML vs its pattern | `d365fo form-pattern validate <F> --output json` |
| Method body | `d365fo read class <Name> --method <M>` |
| Existing CoC wrappers | `d365fo find coc <Class>::<method> --output json` |
| Event handlers | `d365fo find event-handlers <Target> --output json` |
| Relations | `d365fo find relations <Table> --output json` |
| Existing object extensions | `d365fo find extensions <Target> --output json` |
| Resolve label | `d365fo labels resolve @SYS12345 --lang en-us,cs` |
| Security trace | `d365fo security coverage <Object> --type <Kind>` |

## 🧱 Scaffolding commands

| Need | Command |
|---|---|
| Table | `d365fo generate table <Name> --pattern main --field VIN:VinEdt:mandatory --label "@Fleet:Vehicle" --install-to <Model>` |
| Class | `d365fo generate class <Name> [--extends Base] --install-to <Model>` |
| CoC | `d365fo generate coc <Target> --method <m1> --install-to <Model>` |
| Form (9 patterns) | `d365fo generate form <Name> --pattern <P> --table <T> --field … --install-to <Model>` |
| Entity | `d365fo generate entity <Name> --table <T> --all-fields --install-to <Model>` |
| Object extension | `d365fo generate extension <Kind> <Target> <Suffix> --install-to <Model>` |
| Event handler | `d365fo generate event-handler --source-kind <K> --source <Object> --event <E> --install-to <Model>` |
| Privilege/Duty/Role | `d365fo generate {privilege|duty|role} <Name> --install-to <Model>` |

Form patterns: `SimpleList`, `SimpleListDetails`, `DetailsMaster`,
`DetailsTransaction`, `Dialog`, `TableOfContents`, `Lookup`, `ListPage`,
`Workspace`. Aliases (`master`, `transaction`, `toc`, `panorama`,
`drop-dialog`, …) are normalised.

Form writes are pattern-gated: `generate form` validates the result against
the pattern catalog (FP001–FP010) and rejects structural violations while
`D365FO_FORM_PATTERN_ENFORCE=true` (default). `d365fo form-pattern spec <P>`
shows the required tree; `d365fo form-pattern validate <file>` re-checks any
hand-edited form XML (exit 2 = structural errors). (These map to the MCP
`object_patterns` tool — `domain=form action=spec` / `action=validate`.)

────────────────────────────────────────────────────────────────────────────
## ⚡ Token discipline

- ALWAYS pass `--output json`.
- NEVER request full XML back from `generate` — stdout returns `{path, bytes, backup}`.
- NEVER dump entire indexes; use `--limit N`.
- Pipe `jq` for specific fields.
- Two narrow `search` calls beat one wide.
""";

    private static readonly string Tail = """
────────────────────────────────────────────────────────────────────────────
## 🔁 Workflow templates

### Refactor
```sh
d365fo get class <Class> --output json
d365fo read class <Class> --method <m>
d365fo find usages <m> --output json
# edit / regenerate, then on user request:
d365fo build && d365fo bp check
```

### Author CoC (single-round)
```sh
d365fo prepare change <Target> --method <m> --goal "<why>" --output json
d365fo generate coc <Target> --method <m> --install-to <Model> --grounding-token <token>
```

### Create a table (single-round)
```sh
d365fo prepare create <Name> --type table --field <F1> --field <F2> --goal "<why>" --output json
d365fo generate table <Name> --pattern <preset> --field <F1>:<Edt> … --install-to <Model> --grounding-token <token>
```

### Add table fields
```sh
d365fo prepare change <Table> --goal "add fields" --output json
d365fo get edt <Edt> --output json
# edit / regenerate, then:
d365fo index refresh --model <Model>
```

### Hand-written X++ gate (always before writing)
```sh
d365fo validate references --file <f> --output json   # exit 2 = hallucinated symbols
d365fo validate xpp --file <f> --output json          # exit 2 = BP errors
```

### Subscribe to data event
```sh
d365fo find event-handlers <Table> --output json
d365fo generate event-handler --source-kind Table \
    --source <Table> --event Inserted --install-to <Model>
```

### Build a form
```sh
d365fo search form <Name> --output json
d365fo get table <PrimaryTable> --output json
d365fo generate form <Name> --pattern <P> --table <T> \
    --field <F1> --field <F2> --install-to <Model>
```

### Trace security
```sh
d365fo security role <Role>
d365fo security coverage <Object> --type Menuitem
```

────────────────────────────────────────────────────────────────────────────
## 📚 Authoritative source — Microsoft Learn

When uncertain, the only authoritative source is the Microsoft Learn
`dynamics365/fin-ops-core/dev-itpro` tree. Do NOT guess; do NOT rely on
AX 2012 / older training data.

- <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-language-reference>
- <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-select-statement>
- <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-classes-methods>
- <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-conditional>
- <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-variables-data-types>
- <https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/extensibility/method-wrapping-coc>

Combine Learn (syntax authority) with `d365fo` (real metadata for THIS env).

────────────────────────────────────────────────────────────────────────────
## 📦 Output contract

Every command emits a `ToolResult<T>` envelope:

```
{ "ok": true,  "data": <T>, "warnings": [...] }
{ "ok": false, "error": { "code": "...", "message": "...", "hint": "..." } }
```

Parse `ok` first. On `false`, surface `error.message` and follow `error.hint`.
""";
}
