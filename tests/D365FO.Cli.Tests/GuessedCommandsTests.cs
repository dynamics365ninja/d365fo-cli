using System.Text.Json;
using D365FO.Cli.Commands.Index;
using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Commands agents guessed and the CLI did not have.
/// </summary>
/// <remarks>
/// 15 of 43 failed CLI calls in a sample of real agent sessions were such guesses, and the
/// error said only "Run the command with --help", so the next call was a retry or a grep.
/// The shapes: <c>get method &lt;Owner&gt; &lt;method&gt;</c> (the MCP tool is <c>get_method</c>),
/// <c>find tables-by-field</c> (MCP <c>find_tables_by_field</c>), <c>search form &lt;Name&gt;</c>,
/// <c>get class X --source</c> and <c>labels resolve @A @B @C</c>. Each now runs the command
/// that answers it, and an unknown sub-command names the ones that exist.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class GuessedCommandsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-guessed-{Guid.NewGuid():N}");
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");

    public GuessedCommandsTests()
    {
        var packages = Path.Combine(_root, "PackagesLocalDirectory");
        var db = Path.Combine(_root, "index.sqlite");
        var model = Path.Combine(packages, "ContosoCore", "ContosoCore");

        Write(model, "AxClass", "ContosoRateCalc", """
            <AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ContosoRateCalc</Name>
              <SourceCode>
                <Declaration><![CDATA[
            public class ContosoRateCalc
            {
            }
            ]]></Declaration>
                <Methods>
                  <Method>
                    <Name>calc</Name>
                    <Source><![CDATA[
            public real calc()
            {
                real rate = 1;
                rate = rate * 2;
                return rate;
            }
            ]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
            </AxClass>
            """);

        // A table and a form of the same name, as CustTable is both.
        Write(model, "AxTable", "ContosoDealTable", """
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ContosoDealTable</Name>
              <SourceCode>
                <Declaration><![CDATA[
            public class ContosoDealTable extends common
            {
            }
            ]]></Declaration>
                <Methods>
                  <Method>
                    <Name>validateWrite</Name>
                    <Source><![CDATA[
            public boolean validateWrite()
            {
                return super() && tableLevelCheck();
            }
            ]]></Source>
                  </Method>
                  <Method>
                    <Name>init</Name>
                    <Source><![CDATA[
            public void init()
            {
                super();
                tableInit();
            }
            ]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
              <Fields>
                <AxTableField xmlns="" i:type="AxTableFieldString">
                  <Name>DealId</Name>
                  <ExtendedDataType>ContosoDealId</ExtendedDataType>
                </AxTableField>
              </Fields>
            </AxTable>
            """);

        Write(model, "AxForm", "ContosoDealTable", """
            <AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ContosoDealTable</Name>
              <SourceCode>
                <Methods>
                  <Method>
                    <Name>init</Name>
                    <Source><![CDATA[
            public void init()
            {
                super();
                formInit();
            }
            ]]></Source>
                  </Method>
                  <Method>
                    <Name>close</Name>
                    <Source><![CDATA[
            public void close()
            {
                formClose();
                super();
            }
            ]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
              <DataSources>
                <AxFormDataSource>
                  <Name>ContosoDealTable</Name>
                  <Table>ContosoDealTable</Table>
                </AxFormDataSource>
              </DataSources>
            </AxForm>
            """);

        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", db);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
        Assert.Equal(0, Capture(() => IndexExtractCommand.ExtractCore(
            OutputMode.Kind.Json, packages, db, onlyModel: "ContosoCore", sinceIso: null)).Exit);

        new MetadataRepository(db).ApplyExtract(ExtractBatch.Empty("ContosoLabels") with
        {
            IsCustom = true,
            Labels = new[]
            {
                new ExtractedLabel("ContosoExt", "en-US", "DealId", "Deal ID"),
                new ExtractedLabel("ContosoExt", "en-US", "DealName", "Deal name"),
            },
            MenuItems = new[] { new ExtractedMenuItem("ContosoDealTable", "Display", "ContosoDealTable", "Form", null) },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string model, string folder, string name, string xml)
    {
        var dir = Path.Combine(model, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".xml"), xml);
    }

    private static (int Exit, string Output) Capture(Func<int> command)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            return (command(), writer.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static CommandApp BuildApp(StringWriter? help = null) => CliApp.Build(AnsiConsole.Create(new AnsiConsoleSettings
    {
        Ansi = AnsiSupport.No,
        ColorSystem = ColorSystemSupport.NoColors,
        Out = new AnsiConsoleOutput(help ?? new StringWriter()),
    }));

    /// <summary>Runs the args the way Program.cs does: normalise, then parse.</summary>
    private static (int Exit, JsonElement Envelope) Run(params string[] args)
    {
        var (exit, output) = Capture(() => BuildApp().Run(ArgumentShapes.Normalize([.. args, "--output", "json"])));
        return (exit, JsonDocument.Parse(output).RootElement.Clone());
    }

    /// <summary>The hint Program.cs renders for a parse failure of these args.</summary>
    private static string HintForParseFailure(params string[] args)
    {
        var normalized = ArgumentShapes.Normalize(args);
        var ex = Assert.ThrowsAny<CommandParseException>(() => BuildApp().Run(normalized));
        return ArgumentShapes.HintFor(normalized, ex.Message);
    }

    // ---- get method ----

    [Theory]
    [InlineData("ContosoRateCalc", "calc")]
    [InlineData("ContosoRateCalc.calc", null)]
    public void Get_method_reads_a_class_method_without_naming_the_kind(string owner, string? method)
    {
        var (exit, envelope) = method is null ? Run("get", "method", owner) : Run("get", "method", owner, method);
        var (_, viaRead) = Run("read", "class", "ContosoRateCalc", "--method", "calc");

        Assert.Equal(0, exit);
        Assert.Equal(viaRead.GetRawText(), envelope.GetRawText());
        Assert.Equal("class", envelope.GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public void Get_method_finds_the_kind_that_declares_the_method_when_a_table_and_form_share_a_name()
    {
        var (_, onTable) = Run("get", "method", "ContosoDealTable", "validateWrite");
        var (_, onForm) = Run("get", "method", "ContosoDealTable.close");

        Assert.Equal("table", onTable.GetProperty("data").GetProperty("kind").GetString());
        Assert.Contains("tableLevelCheck", onTable.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal("form", onForm.GetProperty("data").GetProperty("kind").GetString());
        Assert.Contains("formClose", onForm.GetProperty("data").GetProperty("source").GetString());
    }

    [Fact]
    public void Get_method_refuses_to_pick_between_two_kinds_that_both_declare_the_method()
    {
        var (exit, envelope) = Run("get", "method", "ContosoDealTable", "init");

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, envelope.GetProperty("error").GetProperty("code").GetString());
        var hint = envelope.GetProperty("error").GetProperty("hint").GetString();
        Assert.Contains("--kind table", hint);
        Assert.Contains("--kind form", hint);

        var (_, chosen) = Run("get", "method", "ContosoDealTable", "init", "--kind", "form");
        Assert.Contains("formInit", chosen.GetProperty("data").GetProperty("source").GetString());
    }

    [Fact]
    public void Get_method_honours_lines_like_read()
    {
        var (_, sliced) = Run("get", "method", "ContosoRateCalc.calc", "--lines", "3-4");
        var (_, viaRead) = Run("read", "class", "ContosoRateCalc", "--method", "calc", "--lines", "3-4");

        Assert.Equal(viaRead.GetRawText(), sliced.GetRawText());
    }

    [Fact]
    public void Get_method_without_a_method_name_says_how_to_name_one()
    {
        var (exit, envelope) = Run("get", "method", "ContosoRateCalc");

        Assert.NotEqual(0, exit);
        Assert.Contains("get method ContosoRateCalc <method>",
            envelope.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Get_method_on_a_missing_method_lists_the_ones_that_exist()
    {
        var (_, envelope) = Run("get", "method", "ContosoDealTable", "nope");

        Assert.Equal(D365FoErrorCodes.MethodNotFound, envelope.GetProperty("error").GetProperty("code").GetString());
        var hint = envelope.GetProperty("error").GetProperty("hint").GetString();
        Assert.Contains("validateWrite", hint);
        Assert.Contains("close", hint);
    }

    // ---- find tables-by-field ----

    [Fact]
    public void Find_tables_by_field_answers_exactly_like_find_fields()
    {
        var (exit, alias) = Run("find", "tables-by-field", "DealId");
        var (_, canonical) = Run("find", "fields", "DealId");

        Assert.Equal(0, exit);
        Assert.Equal(canonical.GetRawText(), alias.GetRawText());
        Assert.Contains("ContosoDealTable", alias.GetRawText());
    }

    // ---- search <kind> ----

    [Theory]
    [InlineData("form", "form", "Form")]
    [InlineData("menu-item", "menuitem", "MenuItem")]
    [InlineData("map", "map", null)]
    public void Search_with_a_kind_that_has_no_sub_command_runs_search_any_filtered_to_it(string typed, string kind, string? expectedKind)
    {
        Assert.Equal(new[] { "search", "any", "ContosoDeal", "--limit", "5", "--kind", kind },
            ArgumentShapes.Normalize(["search", typed, "ContosoDeal", "--limit", "5"]));

        var (exit, envelope) = Run("search", typed, "ContosoDeal");

        Assert.Equal(0, exit);
        var items = envelope.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        if (expectedKind is null)
        {
            Assert.Empty(items);
        }
        else
        {
            Assert.NotEmpty(items);
            Assert.All(items, i => Assert.Equal(expectedKind, i.GetProperty("kind").GetString()));
        }
    }

    [Fact]
    public void Search_kind_rewrite_leaves_everything_else_alone()
    {
        // A bare kind word is still a query, as before.
        Assert.Equal(new[] { "search", "any", "form" }, ArgumentShapes.Normalize(["search", "form"]));
        // Help is Spectre's call.
        Assert.Equal(new[] { "search", "any", "form", "--help" }, ArgumentShapes.Normalize(["search", "form", "--help"]));

        // Two filters is not rewritten into one: the stray positional fails loudly instead of
        // either --kind being dropped.
        var hint = HintForParseFailure("search", "form", "ContosoDeal", "--kind", "table");
        Assert.NotNull(hint);
    }

    // ---- get <kind> --source ----

    [Fact]
    public void Get_class_source_runs_read_class_and_passes_every_other_option_on()
    {
        Assert.Equal(new[] { "read", "class", "ContosoRateCalc", "--method", "calc" },
            ArgumentShapes.Normalize(["get", "class", "ContosoRateCalc", "--source", "--method", "calc"]));

        var (exit, viaGet) = Run("get", "class", "ContosoRateCalc", "--source");
        var (_, viaRead) = Run("read", "class", "ContosoRateCalc");

        Assert.Equal(0, exit);
        Assert.Equal(viaRead.GetRawText(), viaGet.GetRawText());
    }

    [Fact]
    public void Get_source_with_an_option_read_does_not_take_still_fails_loudly()
    {
        // `--include` belongs to `get table`, not `read table`: the flag is not dropped, it is refused.
        var hint = HintForParseFailure("get", "table", "ContosoDealTable", "--source", "--include", "fields");
        Assert.Contains("--help", hint);
    }

    [Fact]
    public void Get_without_source_is_untouched()
    {
        string[] args = ["get", "class", "ContosoRateCalc", "--output", "json"];
        Assert.Equal(args, ArgumentShapes.Normalize(args));
    }

    // ---- unknown sub-commands ----

    [Fact]
    public void Unknown_sub_command_lists_the_valid_ones_inline_and_suggests_the_closest()
    {
        var hint = HintForParseFailure("find", "field", "DealId");

        Assert.Contains("Did you mean `d365fo find fields`?", hint);
        Assert.Contains("Sub-commands of `find`:", hint);
        foreach (var sub in new[] { "coc", "fields", "refs", "tables-by-field", "usages" })
            Assert.Contains(sub, hint);
    }

    [Fact]
    public void Unknown_sub_command_with_nothing_close_lists_without_guessing()
    {
        var hint = HintForParseFailure("get", "zzzz");

        Assert.DoesNotContain("Did you mean", hint);
        Assert.Contains("method", hint);
        Assert.Contains("d365fo get <sub-command> zzzz", hint);
    }

    [Fact]
    public void Unknown_top_level_command_lists_the_commands_and_suggests_the_closest()
    {
        var hint = HintForParseFailure("serach", "ContosoDeal");

        Assert.Contains("Did you mean `d365fo search`?", hint);
        Assert.Contains("generate", hint);
    }

    [Theory]
    [InlineData("metod", "method")]
    [InlineData("field", "fields")]
    [InlineData("reference", "references")]
    [InlineData("tbl", null)]
    public void Did_you_mean_stays_close(string typed, string? expected)
    {
        var suggestion = ArgumentShapes.DidYouMean(typed, ["method", "fields", "references", "refs", "table"], s => s);
        if (expected is null) Assert.Equal("", suggestion);
        else Assert.Equal($" Did you mean `{expected}`?", suggestion);
    }

    // ---- labels resolve with several tokens ----

    [Fact]
    public void Labels_resolve_takes_several_tokens_and_answers_one_result_each()
    {
        var (exit, envelope) = Run("labels", "resolve", "@ContosoExt:DealId", "@ContosoExt:DealName", "@ContosoExt:Nope");

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal(3, data.GetProperty("count").GetInt32());
        Assert.Equal(2, data.GetProperty("resolved").GetInt32());

        var results = data.GetProperty("results").EnumerateArray().ToList();
        Assert.Equal(new[] { "@ContosoExt:DealId", "@ContosoExt:DealName", "@ContosoExt:Nope" },
            results.Select(r => r.GetProperty("token").GetString()));
        Assert.Equal("Deal ID", results[0].GetProperty("items")[0].GetProperty("value").GetString());
        Assert.False(results[2].GetProperty("found").GetBoolean());
        Assert.Contains("@ContosoExt:Nope", data.GetProperty("notResolved")[0].GetString());
    }

    [Fact]
    public void Labels_resolve_with_one_token_keeps_its_original_shape()
    {
        var (exit, envelope) = Run("labels", "resolve", "@ContosoExt:DealId");

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal(1, data.GetProperty("count").GetInt32());
        Assert.False(data.TryGetProperty("results", out _));
        Assert.Equal("Deal ID", data.GetProperty("items")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void Labels_resolve_fails_when_none_of_several_tokens_resolves()
    {
        var (exit, envelope) = Run("labels", "resolve", "@ContosoExt:Nope", "@ContosoExt:Gone");

        Assert.NotEqual(0, exit);
        Assert.Equal("LABEL_NOT_FOUND", envelope.GetProperty("error").GetProperty("code").GetString());
    }
}
