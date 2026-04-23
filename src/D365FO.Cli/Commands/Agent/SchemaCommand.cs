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
            new { group = "search",  name = "class",       description = "Find X++ classes by substring.",
                  args = new[] { "<QUERY>" }, options = new[] { "--model", "--limit" } },
            new { group = "search",  name = "table",       description = "Find tables by substring.",
                  args = new[] { "<QUERY>" }, options = new[] { "--model", "--limit" } },
            new { group = "search",  name = "edt",         description = "Find Extended Data Types.",
                  args = new[] { "<QUERY>" }, options = new[] { "--limit" } },
            new { group = "search",  name = "enum",        description = "Find base enums.",
                  args = new[] { "<QUERY>" }, options = new[] { "--limit" } },
            new { group = "search",  name = "label",       description = "Search label file keys/values.",
                  args = new[] { "<QUERY>" }, options = new[] { "--lang", "--limit" } },
            new { group = "get",     name = "table",       description = "Return full table shape (fields, relations).",
                  args = new[] { "<NAME>" }, options = new[] { "--include" } },
            new { group = "get",     name = "edt",         description = "Return EDT definition.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "class",       description = "Return class methods and signatures.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "enum",        description = "Return base enum values.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "menu-item",   description = "Resolve a menu item to its object.",
                  args = new[] { "<NAME>" }, options = Array.Empty<string>() },
            new { group = "get",     name = "security",    description = "Security hierarchy coverage for an object.",
                  args = new[] { "<OBJECT>" }, options = new[] { "--type" } },
            new { group = "get",     name = "label",       description = "Resolve single label entry.",
                  args = new[] { "<FILE>", "<KEY>" }, options = new[] { "--lang" } },
            new { group = "find",    name = "coc",         description = "Chain-of-Command extensions targeting Class::method.",
                  args = new[] { "<TARGET>" }, options = Array.Empty<string>() },
            new { group = "find",    name = "relations",   description = "Inbound/outbound table relations.",
                  args = new[] { "<TABLE>" }, options = Array.Empty<string>() },
            new { group = "find",    name = "usages",      description = "Find index entities whose name contains a substring.",
                  args = new[] { "<SYMBOL>" }, options = new[] { "--limit" } },
            new { group = "index",   name = "build",       description = "Ensure / create metadata index database.",
                  args = Array.Empty<string>(), options = new[] { "--db" } },
            new { group = "index",   name = "status",      description = "Report index size and config.",
                  args = Array.Empty<string>(), options = Array.Empty<string>() },
            new { group = "index",   name = "extract",     description = "Walk PACKAGES_PATH and ingest AOT metadata.",
                  args = Array.Empty<string>(), options = new[] { "--packages", "--db", "--model" } },
            new { group = "generate",name = "table",       description = "Scaffold a new AxTable XML.",
                  args = new[] { "<NAME>" }, options = new[] { "--out", "--label", "--field", "--overwrite" } },
            new { group = "generate",name = "class",       description = "Scaffold a new AxClass XML.",
                  args = new[] { "<NAME>" }, options = new[] { "--out", "--extends", "--non-final", "--overwrite" } },
            new { group = "generate",name = "coc",         description = "Scaffold a Chain-of-Command extension.",
                  args = new[] { "<TARGET>" }, options = new[] { "--out", "--method", "--overwrite" } },
            new { group = "generate",name = "simple-list", description = "Scaffold a SimpleList AxForm.",
                  args = new[] { "<FORM_NAME>" }, options = new[] { "--out", "--table", "--overwrite" } },
            new { group = "review",  name = "diff",        description = "Inspect AOT changes vs. a git revision.",
                  args = Array.Empty<string>(), options = new[] { "--base", "--head", "--repo" } },
            new { group = "test",    name = "run",         description = "Run D365FO tests via SysTestRunner (Windows VM).",
                  args = Array.Empty<string>(), options = new[] { "--runner", "--suite" } },
            new { group = "bp",      name = "check",       description = "Run best-practice checks via xppbp (Windows VM).",
                  args = Array.Empty<string>(), options = new[] { "--tool", "--model" } },
            new { group = "(root)",  name = "build",       description = "Invoke MSBuild (Windows VM).",
                  args = Array.Empty<string>(), options = new[] { "--msbuild", "--project", "--config" } },
            new { group = "(root)",  name = "sync",        description = "Run DB sync (Windows VM).",
                  args = Array.Empty<string>(), options = new[] { "--tool", "--full" } },
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
