using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// The argument shapes agents reach for first: bare <c>search &lt;QUERY&gt;</c>, bare
/// <c>security &lt;NAME&gt;</c> and <c>validate references --file</c>.
/// </summary>
/// <remarks>
/// All three failed with a usage error, and the agent's next call was a <c>--help</c>
/// lookup: 16 of 46 invocations in a month of sessions on a real installation. The
/// strict forms are unchanged and still documented; these tests hold both.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class ArgumentShapesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-shapes-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");

    public ArgumentShapesTests()
    {
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "index.sqlite");
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _db);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");

        var repo = new MetadataRepository(_db);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Contoso") with
        {
            IsCustom = true,
            Tables = new[] { new ExtractedTable("ContosoDealTable", null, "/ContosoDealTable.xml", Array.Empty<ExtractedTableField>()) },
            MenuItems = new[] { new ExtractedMenuItem("ContosoDealTable", "Display", "ContosoDealTable", "Form", null) },
            Roles = new[] { new ExtractedSecurityRole("ContosoDealManager", null, new[] { "ContosoDealMaintain" }, Array.Empty<string>()) },
            Duties = new[] { new ExtractedSecurityDuty("ContosoDealMaintain", null, new[] { "ContosoDealTableMaintain" }) },
            Privileges = new[]
            {
                new ExtractedSecurityPrivilege("ContosoDealTableMaintain", null, new[]
                {
                    new ExtractedSecurityEntryPoint("ContosoDealTable", "MenuItemDisplay", null, "Delete"),
                }),
            },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static CommandApp BuildApp() => CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
        Out = new AnsiConsoleOutput(new StringWriter()),
    }));

    /// <summary>Runs the args the way Program.cs does: normalise, then parse.</summary>
    private static (int Exit, JsonElement Envelope) Run(params string[] args)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = BuildApp().Run(ArgumentShapes.Normalize([.. args, "--output", "json"]));
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    // ---- search ----

    [Fact]
    public void Bare_search_query_runs_search_any_instead_of_unknown_command()
    {
        Assert.Equal(new[] { "search", "any", "ContosoDeal", "--limit", "5" },
            ArgumentShapes.Normalize(["search", "ContosoDeal", "--limit", "5"]));

        var (exit, envelope) = Run("search", "ContosoDeal");

        Assert.Equal(0, exit);
        Assert.True(envelope.GetProperty("ok").GetBoolean());
        Assert.Contains(envelope.GetProperty("data").GetProperty("items").EnumerateArray(),
            i => i.GetProperty("name").GetString() == "ContosoDealTable");
    }

    [Fact]
    public void Without_the_rewrite_a_bare_search_is_still_a_parse_error()
    {
        // Documents why ArgumentShapes exists: Spectre 0.49 cannot run a branch default
        // command when a positional follows the branch.
        var ex = Assert.ThrowsAny<CommandParseException>(() => BuildApp().Run(["search", "ContosoDeal"]));
        Assert.Contains("Unknown command", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("search", "class", "Cust")]
    [InlineData("search", "Table", "Cust")]          // sub-commands are case-insensitive
    [InlineData("search", "any", "Cust")]
    [InlineData("search", "--help")]
    [InlineData("search")]
    [InlineData("security", "role", "SystemUser")]
    [InlineData("security", "coverage", "CustTable", "--type", "MenuItemDisplay")]
    [InlineData("get", "ContosoDealTable")]           // only the two forgiving branches are rewritten
    [InlineData("validate", "references", "--file", "x.xpp")]
    public void Strict_forms_are_left_exactly_as_typed(params string[] args)
    {
        Assert.Equal(args, ArgumentShapes.Normalize(args));
    }

    /// <summary>
    /// A sub-command added to either branch but missing from <see cref="ArgumentShapes.Branches"/>
    /// would be swallowed as a query (<c>search newkind X</c> → <c>search any newkind X</c>).
    /// </summary>
    [Theory]
    [InlineData("search")]
    [InlineData("security")]
    public void Every_registered_sub_command_of_a_forgiving_branch_is_known_to_the_rewrite(string branch)
    {
        var registered = CliSurface.Leaves()
            .Where(l => l.Path.StartsWith(branch + " ", StringComparison.Ordinal))
            .Select(l => l.Path[(branch.Length + 1)..])
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(registered, ArgumentShapes.Branches[branch].SubCommands.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Contains(ArgumentShapes.Branches[branch].Default, registered);
    }

    [Fact]
    public void Unknown_sub_command_hint_names_the_branch_instead_of_generic_help()
    {
        var hint = ArgumentShapes.HintFor(["get", "ContosoDealTable"], "Unknown command 'ContosoDealTable'.");

        Assert.Contains("d365fo get <sub-command> ContosoDealTable", hint);
        Assert.Contains("d365fo get --help", hint);

        // Anything else keeps the generic hint.
        Assert.Contains("--help to see the options",
            ArgumentShapes.HintFor(["search", "table", "Cust", "--limits", "5"], "Unknown option 'limits'."));

        // A leaf command reports a stray positional with the same message; it has no
        // sub-commands, so the branch hint would send the caller looking for one.
        Assert.Contains("--help to see the options",
            ArgumentShapes.HintFor(["version", "foo"], "Unknown command 'foo'."));
        Assert.Contains("d365fo index <sub-command> CustTable",
            ArgumentShapes.HintFor(["index", "CustTable"], "Unknown command 'CustTable'."));
    }

    // ---- security ----

    [Fact]
    public void Bare_security_name_resolves_a_role_without_the_sub_command()
    {
        var (exit, envelope) = Run("security", "ContosoDealManager");

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal("role", data.GetProperty("resolvedAs").GetString());
        Assert.Equal("ContosoDealMaintain",
            data.GetProperty("role").GetProperty("duties")[0].GetString());
    }

    [Fact]
    public void Bare_security_object_name_returns_coverage_without_a_type()
    {
        var (exit, envelope) = Run("security", "ContosoDealTable");

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal("coverage", data.GetProperty("resolvedAs").GetString());

        var coverage = data.GetProperty("coverage").EnumerateArray().ToList();
        // The menu-item entry point carries the role route...
        var menuItem = Assert.Single(coverage, c => c.GetProperty("objectType").GetString() == "MenuItemDisplay");
        Assert.Equal("ContosoDealManager", menuItem.GetProperty("routes")[0].GetProperty("role").GetString());
        // ...and the table itself carries the record-level (XDS) answer.
        var table = Assert.Single(coverage, c => c.GetProperty("objectType").GetString() == "Table");
        Assert.Equal("Unknown", table.GetProperty("rowLevel").GetProperty("state").GetString());
    }

    [Fact]
    public void Security_name_the_index_does_not_know_fails_instead_of_an_empty_coverage()
    {
        // An empty route list reads as "nobody has access"; for an unknown name it must not.
        var (exit, envelope) = Run("security", "NoSuchThing");

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.SecurityNotFound,
            envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("search any NoSuchThing", envelope.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Security_any_matches_the_mcp_security_info_any_route()
    {
        var mcp = D365FO.Mcp.ToolCatalog.All.Single(d => d.Name == "security_info");
        Assert.Contains("mode=any", mcp.Description);

        var viaMcp = new D365FO.Mcp.ToolHandlers(new MetadataRepository(_db)).ResolveSecurityName("ContosoDealMaintain");
        var (_, viaCli) = Run("security", "any", "ContosoDealMaintain");

        Assert.True(viaMcp.Ok);
        Assert.Equal("duty", viaCli.GetProperty("data").GetProperty("resolvedAs").GetString());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(D365Json.Serialize(viaMcp)),
            System.Text.Json.Nodes.JsonNode.Parse(viaCli.GetRawText())));
    }

    // ---- validate references --file ----

    [Theory]
    [InlineData("references")]
    [InlineData("xpp")]
    public void Validate_accepts_the_file_as_an_option_as_well_as_an_argument(string mode)
    {
        var file = Path.Combine(_root, "Probe.xpp");
        File.WriteAllText(file, "class Probe\n{\n    public void run()\n    {\n    }\n}\n");

        var (_, viaOption) = Run("validate", mode, "--file", file);
        var (_, viaArgument) = Run("validate", mode, file);

        Assert.True(viaOption.GetProperty("ok").GetBoolean());
        Assert.Equal(viaArgument.GetRawText(), viaOption.GetRawText());
    }

    [Fact]
    public void Validate_refuses_two_different_files_instead_of_picking_one()
    {
        var (exit, envelope) = Run("validate", "references", "a.xpp", "--file", "b.xpp");

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("--file", envelope.GetProperty("error").GetProperty("hint").GetString());
    }
}
