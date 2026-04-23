using D365FO.Core;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Agent;

public sealed class SchemaCommand : Command<SchemaCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--full")]
        public bool Full { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        // Static catalog — deliberately handwritten so the schema ships even
        // when the CLI binary is AOT-trimmed. Keep in sync with Program.cs.
        var commands = new object[]
        {
            new { group = "search",  name = "class",       description = "Find X++ classes by name prefix.",
                  args = new[] { "<QUERY>" }, options = new[] { "--model", "--limit" } },
            new { group = "search",  name = "label",       description = "Search label file keys/values.",
                  args = new[] { "<QUERY>" }, options = new[] { "--lang", "--limit" } },
            new { group = "get",     name = "table",       description = "Return full table shape (fields, relations).",
                  args = new[] { "<NAME>" }, options = new[] { "--include" } },
            new { group = "get",     name = "edt",         description = "Return EDT definition.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "class",       description = "Return class methods and signatures.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "menu-item",   description = "Resolve a menu item to its object.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "security",    description = "Security hierarchy coverage for an object.",
                  args = new[] { "<OBJECT>" }, options = new[] { "--type" } },
            new { group = "find",    name = "coc",         description = "Chain-of-Command extensions targeting Class::method.",
                  args = new[] { "<TARGET>" }, options = Array.Empty<string>() },
            new { group = "find",    name = "relations",   description = "Inbound/outbound table relations.",
                  args = new[] { "<TABLE>" }, options = Array.Empty<string>() },
            new { group = "index",   name = "build",       description = "Ensure / create metadata index database.",
                  args = Array.Empty<string>(), options = new[] { "--db" } },
            new { group = "index",   name = "status",      description = "Report index size and config.",
                  args = Array.Empty<string>(), options = Array.Empty<string>() },
            new { group = "(root)",  name = "doctor",      description = "Diagnose configuration.",
                  args = Array.Empty<string>(), options = Array.Empty<string>() },
            new { group = "(root)",  name = "version",     description = "Print version info.",
                  args = Array.Empty<string>(), options = Array.Empty<string>() },
            new { group = "(root)",  name = "agent-prompt",description = "Emit LLM system prompt for this CLI.",
                  args = Array.Empty<string>(), options = new[] { "--out" } },
            new { group = "(root)",  name = "schema",      description = "Emit JSON manifest of commands.",
                  args = Array.Empty<string>(), options = new[] { "--full" } },
        };

        var payload = new
        {
            name = "d365fo",
            version = typeof(SchemaCommand).Assembly.GetName().Version?.ToString() ?? "0.1.0-dev",
            envelope = new { ok = "bool", data = "T", error = new { code = "string", message = "string", hint = "string?" } },
            commands = settings.Full
                ? commands
                : commands.Select(c => new { ((dynamic)c).group, ((dynamic)c).name, ((dynamic)c).description }).Cast<object>().ToArray(),
        };

        Console.Out.WriteLine(D365Json.Serialize(ToolResult<object>.Success(payload), indented: true));
        return 0;
    }
}
