using System.Text.Json;
using D365FO.Cli.Commands.Get;
using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Output that costs an agent tokens without telling it anything: colour escapes in captured
/// help, and the whole property bag of every field when it asked what a table holds.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class LeanerOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-lean-{Guid.NewGuid():N}");
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");

    public LeanerOutputTests()
    {
        Directory.CreateDirectory(_root);
        var db = Path.Combine(_root, "index.sqlite");
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", db);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");

        var repo = new MetadataRepository(db);
        repo.EnsureSchema();
        repo.ApplyExtract(ExtractBatch.Empty("Contoso") with
        {
            IsCustom = true,
            Tables = new[]
            {
                new ExtractedTable("ContosoDealTable", "@Contoso:Deal", "/ContosoDealTable.xml", new[]
                {
                    new ExtractedTableField("DealId", "String", "ContosoDealId", "@Contoso:DealId", true),
                    new ExtractedTableField("Status", "Enum", null, "@Contoso:Status", false),
                })
                {
                    Indexes = new[] { new ExtractedTableIndex("DealIdx", false, true, new[] { "DealId" }) },
                    Relations = new[] { new ExtractedTableRelation("CustTable", "CustTable", "ZeroMore", "Association") },
                    Methods = new[] { new ExtractedMethod("find", "public static ContosoDealTable find(ContosoDealId _id)", "ContosoDealTable", true) },
                },
                new ExtractedTable("ContosoDealLine", null, "/ContosoDealLine.xml", Array.Empty<ExtractedTableField>())
                {
                    // Declared on the other table: the brief of ContosoDealTable must not list it.
                    Relations = new[] { new ExtractedTableRelation("ContosoDealTable", "ContosoDealTable", "ZeroMore", "Association") },
                },
            },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (int Exit, JsonElement Envelope) Run(GetTableCommand.Settings settings)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = new GetTableCommand().Execute(null!, settings);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    // ---- ANSI ----

    [Theory]
    [InlineData(true, null, "xterm-256color", true)]   // redirected: the agent case
    [InlineData(false, "1", "xterm-256color", true)]    // NO_COLOR, any non-empty value
    [InlineData(false, null, "dumb", true)]
    [InlineData(false, null, "DUMB", true)]
    [InlineData(false, "", "xterm-256color", false)]    // NO_COLOR set but empty does not count
    [InlineData(false, null, "xterm-256color", false)]  // interactive terminal: unchanged
    [InlineData(false, null, null, false)]
    public void Plain_output_is_chosen_when_redirected_or_asked_for(bool redirected, string? noColor, string? term, bool plain)
    {
        Assert.Equal(plain, ConsoleSetup.PlainOutputWanted(redirected, noColor, term));
    }

    [Fact]
    public void Help_rendered_through_the_plain_console_carries_no_ansi_escapes()
    {
        // Pin the CI markers Spectre's profile enrichers react to (they force ANSI on), so the
        // answer is the same on a developer terminal and on a GitHub Actions / Azure DevOps runner.
        var oldGitHub = Environment.GetEnvironmentVariable("GITHUB_ACTIONS");
        var oldTfBuild = Environment.GetEnvironmentVariable("TF_BUILD");
        var plain = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", "true");
            Environment.SetEnvironmentVariable("TF_BUILD", "True");
            Assert.Equal(0, CliApp.Build(ConsoleSetup.Plain(plain)).Run(["search", "--help"]));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_ACTIONS", oldGitHub);
            Environment.SetEnvironmentVariable("TF_BUILD", oldTfBuild);
        }
        Assert.Contains("USAGE", plain.ToString());
        Assert.DoesNotContain('\u001b', plain.ToString());

        // Control: the same help through an ANSI console does carry escapes, so the
        // assertion above is about the console choice and not about the help text.
        var ansi = new StringWriter();
        CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Out = new AnsiConsoleOutput(ansi),
        })).Run(["search", "--help"]);
        Assert.Contains('\u001b', ansi.ToString());
    }

    // ---- get table --brief ----

    [Fact]
    public void Brief_table_keeps_names_types_and_keys_and_drops_the_property_bags()
    {
        var (exit, envelope) = Run(new GetTableCommand.Settings { Name = "ContosoDealTable", Brief = true, Output = "json" });

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal("brief", data.GetProperty("detail").GetString());
        Assert.Equal("ContosoDealTable", data.GetProperty("table").GetProperty("name").GetString());

        var dealId = data.GetProperty("fields")[0];
        Assert.Equal("DealId", dealId.GetProperty("name").GetString());
        Assert.Equal("String", dealId.GetProperty("type").GetString());
        Assert.Equal("ContosoDealId", dealId.GetProperty("edt").GetString());
        Assert.False(dealId.TryGetProperty("label", out _));
        Assert.False(dealId.TryGetProperty("mandatory", out _));

        var index = Assert.Single(data.GetProperty("indexes").EnumerateArray());
        Assert.Equal("DealIdx", index.GetProperty("name").GetString());
        Assert.Equal("DealId", index.GetProperty("fields")[0].GetString());

        // Only the relations this table declares, by name and related table.
        var relation = Assert.Single(data.GetProperty("relations").EnumerateArray());
        Assert.Equal("CustTable", relation.GetProperty("relatedTable").GetString());

        Assert.Equal(1, data.GetProperty("methodCount").GetInt32());
        Assert.False(data.TryGetProperty("methods", out _));
        Assert.False(data.TryGetProperty("deleteActions", out _));
    }

    [Fact]
    public void Default_get_table_output_is_unchanged_by_the_brief_option()
    {
        var (_, envelope) = Run(new GetTableCommand.Settings { Name = "ContosoDealTable", Output = "json" });

        var data = envelope.GetProperty("data");
        Assert.False(data.TryGetProperty("detail", out _));
        Assert.Equal("@Contoso:DealId", data.GetProperty("fields")[0].GetProperty("label").GetString());
        Assert.True(data.GetProperty("fields")[0].GetProperty("mandatory").GetBoolean());
        Assert.True(data.TryGetProperty("deleteActions", out _));
    }

    [Theory]
    [InlineData("fields", false, "--include")]
    [InlineData(null, true, "--merged")]
    public void Brief_refuses_options_it_cannot_honour_instead_of_ignoring_them(string? include, bool merged, string named)
    {
        var (exit, envelope) = Run(new GetTableCommand.Settings
        {
            Name = "ContosoDealTable", Brief = true, Include = include, Merged = merged, Output = "json",
        });

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, envelope.GetProperty("error").GetProperty("code").GetString());
        Assert.Contains(named, envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Brief_matches_the_mcp_get_object_info_detail_brief_answer()
    {
        var (_, viaCli) = Run(new GetTableCommand.Settings { Name = "ContosoDealTable", Brief = true, Output = "json" });

        var handlers = new D365FO.Mcp.ToolHandlers(new MetadataRepository(Environment.GetEnvironmentVariable("D365FO_INDEX_DB")!));
        var descriptor = D365FO.Mcp.ToolCatalog.All.Single(d => d.Name == "get_object_info");
        var args = JsonDocument.Parse("""{"objectType":"table","name":"ContosoDealTable","detail":"brief"}""").RootElement;
        var viaMcp = descriptor.Invoke(handlers, args);

        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(D365Json.Serialize(viaMcp)),
            System.Text.Json.Nodes.JsonNode.Parse(viaCli.GetRawText())));
    }
}
