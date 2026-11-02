using System.Text.Json;
using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Mcp;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Issue #231 on both front doors: <c>d365fo modify method</c> and the MCP <c>modify_method</c>
/// tool refuse a full declaration passed as a body, and both accept the whole method through
/// <c>--source</c> / <c>source</c>.
/// </summary>
/// <remarks>
/// The bridge is switched off here, so a call that passes the input checks ends in
/// <c>BRIDGE_REQUIRED</c>. That is the proof the option was honoured rather than dropped: a
/// dropped <c>source</c> would have been refused as a missing body instead.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class ModifyMethodBodyParityTests : IDisposable
{
    private const string FullDeclaration = "public real calc()\n{\n    return 2;\n}";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-modmethod-{Guid.NewGuid():N}");
    private readonly string _db;
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");
    private readonly MetadataRepository _repo;

    public ModifyMethodBodyParityTests()
    {
        Directory.CreateDirectory(_root);
        _db = Path.Combine(_root, "index.sqlite");
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _db);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
        _repo = new MetadataRepository(_db);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);

        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (int Exit, JsonElement Envelope) Cli(params string[] args)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Out = new AnsiConsoleOutput(new StringWriter()),
            })).Run([.. args, "--output", "json"]);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    private JsonElement Mcp(object args)
    {
        var descriptor = ToolCatalog.All.Single(d => d.Name == "modify_method");
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(args));
        var result = descriptor.Invoke(new ToolHandlers(_repo), doc.RootElement);
        return JsonDocument.Parse(D365Json.Serialize(result)).RootElement.Clone();
    }

    private static string Code(JsonElement envelope) => envelope.GetProperty("error").GetProperty("code").GetString()!;

    [Fact]
    public void Full_declaration_as_body_is_refused_the_same_way_on_cli_and_mcp()
    {
        var (exit, cli) = Cli("modify", "method", "class", "CustBalance", "calc", "--body", FullDeclaration);
        var mcp = Mcp(new { kind = "class", name = "CustBalance", method = "calc", body = FullDeclaration });

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, Code(cli));
        Assert.Equal(D365FoErrorCodes.BadInput, Code(mcp));
        Assert.Equal(
            cli.GetProperty("error").GetProperty("message").GetString(),
            mcp.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("`source`", mcp.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Source_is_honoured_on_cli_and_mcp_rather_than_dropped()
    {
        var (_, cli) = Cli("modify", "method", "class", "CustBalance", "calc", "--source", FullDeclaration);
        var mcp = Mcp(new { kind = "class", name = "CustBalance", method = "calc", source = FullDeclaration });

        Assert.Equal(D365FoErrorCodes.BridgeRequired, Code(cli));
        Assert.Equal(D365FoErrorCodes.BridgeRequired, Code(mcp));
    }

    [Fact]
    public void Source_naming_another_method_is_refused_on_cli_and_mcp()
    {
        const string other = "public real other()\n{\n    return 2;\n}";
        var (_, cli) = Cli("modify", "method", "class", "CustBalance", "calc", "--source", other);
        var mcp = Mcp(new { kind = "class", name = "CustBalance", method = "calc", source = other });

        Assert.Equal(D365FoErrorCodes.BadInput, Code(cli));
        Assert.Equal(D365FoErrorCodes.BadInput, Code(mcp));
    }

    [Fact]
    public void Neither_body_nor_source_is_refused_on_cli_and_mcp()
    {
        var (_, cli) = Cli("modify", "method", "class", "CustBalance", "calc");
        var mcp = Mcp(new { kind = "class", name = "CustBalance", method = "calc" });

        Assert.Equal(D365FoErrorCodes.BadInput, Code(cli));
        Assert.Equal(D365FoErrorCodes.BadInput, Code(mcp));
    }
}
