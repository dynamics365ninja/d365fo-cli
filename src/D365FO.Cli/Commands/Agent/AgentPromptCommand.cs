using D365FO.Core;
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
    public static string Build() => """
# d365fo CLI — agent system prompt

You have access to a single tool: a shell that can execute the `d365fo` CLI.
All `d365fo` subcommands return JSON on stdout when stdout is not a TTY.
Prefer `--output json` explicitly to make parsing deterministic.

## Core workflow rules

1. ALWAYS check the metadata index before generating X++ code:
   - `d365fo get table <Name>` before referencing a field.
   - `d365fo search class <query>` before extending a class.
   - `d365fo find coc <Class>::<method>` before writing a Chain-of-Command extension.
   - `d365fo search label <text>` before hardcoding a user-visible string.

2. NEVER hallucinate field names, EDT names, or method signatures. If a lookup
   returns `ok: false` with code `*_NOT_FOUND`, stop and ask or re-index.

3. Scaffolding commands (`d365fo generate ...`, when available) write XML to
   disk via `--out`. Stdout only contains a short JSON summary (path, size).
   Do NOT request the full XML back into the conversation unless the user asks.

4. Build / sync / test (`d365fo build`, `d365fo sync`, `d365fo test run`,
   `d365fo bp check`) produce large logs. Always pipe through `--output json`
   and summarise counts + first error; only fetch full log on request.

5. Respect `--raw-text`: by default labels are sanitized (control chars stripped)
   to defend against prompt-injection embedded in customer data. Only use
   `--raw-text` when the user explicitly requests raw content.

## Command catalog (summary)

- search: class, label
- get: table, edt, class, menu-item, security
- find: coc, relations
- index: build, status
- doctor, version, agent-prompt, schema

Run `d365fo <group> --help` for exhaustive options. Run `d365fo schema --full`
for a JSON manifest of all commands and parameters.

## Output contract

Every command emits a `ToolResult<T>` envelope:
```
{ "ok": true, "data": ..., "warnings": [...] }
{ "ok": false, "error": { "code": "...", "message": "...", "hint": "..." } }
```
Parse `ok` first. On `false`, surface `error.message` to the user and follow
`error.hint` if present.
""";
}
