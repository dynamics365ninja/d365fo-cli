using System.Text.Json;
using System.Text.Json.Nodes;
using D365FO.Cli.Commands.Find;
using D365FO.Cli.Commands.Get;
using D365FO.Cli.Commands.Index;
using D365FO.Core;

namespace D365FO.Cli.Tests;

/// <summary>
/// Lookups that used to answer a different question than the one asked, or nothing at all,
/// without saying so.
/// </summary>
/// <remarks>
/// Measured by an outside benchmark (CLI vs XRef DB vs grep): <c>find refs --xref</c> with the
/// bridge off quietly ran a 12-minute text scan and returned 42 of 72 callers;
/// <c>get table SalesLine</c> spent ~44k tokens, mostly method signatures, because
/// <c>--include</c> was declared and never read.
/// </remarks>
[Collection("EnvIndexDb")]
public sealed class SilentEmptyLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-silent-{Guid.NewGuid():N}");
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
    private readonly string? _oldBridge = Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");
    private readonly string? _oldXref = Environment.GetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING");

    public SilentEmptyLookupTests()
    {
        Directory.CreateDirectory(_root);
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", Path.Combine(_root, "index.sqlite"));
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
        // Nothing listens on port 9: the direct xref query fails at once, on every machine.
        Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING",
            "Server=tcp:127.0.0.1,9;Database=DYNAMICSXREFDB;Integrated Security=true;Connect Timeout=1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", _oldBridge);
        Environment.SetEnvironmentVariable("D365FO_XREF_CONNECTIONSTRING", _oldXref);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (int Exit, JsonElement Envelope) Run(Func<int> command)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(writer);
            exit = command();
        }
        finally
        {
            Console.SetOut(originalOut);
        }
        return (exit, JsonDocument.Parse(writer.ToString()).RootElement.Clone());
    }

    private static string ErrorCode(JsonElement envelope) =>
        envelope.GetProperty("error").GetProperty("code").GetString()!;

    // ---- find refs --xref ----

    [Fact]
    public void Xref_with_the_database_unreachable_fails_instead_of_falling_back_to_the_text_scan()
    {
        var (exit, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.False(envelope.GetProperty("ok").GetBoolean());
        Assert.Equal(D365FoErrorCodes.XrefUnavailable, ErrorCode(envelope));
        // The direct query ran and said why it failed; the bridge (off here) was not tried.
        Assert.Contains("SqlException", envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.DoesNotContain("Bridge fallback", envelope.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains("D365FO_XREF_CONNECTIONSTRING", envelope.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Xref_rejects_a_model_filter_it_cannot_apply()
    {
        var (exit, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Model = "Foundation", Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, ErrorCode(envelope));
        Assert.Contains("model filter", envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Xref_rejects_an_artifact_kind_where_a_reference_kind_is_meant()
    {
        // `--kind class` means "artifact kind" to the text scan; the xref query would filter on a
        // reference kind called "class", which does not exist, and return an empty list.
        var (exit, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Kind = "class", Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, ErrorCode(envelope));
        Assert.Contains("Call", envelope.GetProperty("error").GetProperty("hint").GetString());
    }

    [Fact]
    public void Xref_accepts_a_reference_kind_and_only_then_needs_the_database()
    {
        var (_, envelope) = Run(() => new FindRefsCommand().Execute(null!,
            new FindRefsCommand.Settings { Name = "SalesLine", Xref = true, Kind = "call", Output = "json" }));

        Assert.Equal(D365FoErrorCodes.XrefUnavailable, ErrorCode(envelope));
    }

    // ---- get table --include ----

    [Fact]
    public void Include_defaults_to_every_part_but_methods()
    {
        Assert.True(GetTableCommand.TryParseInclude(null, out var parts, out _));

        Assert.Contains("fields", parts);
        Assert.Contains("indexes", parts);
        Assert.Contains("relations", parts);
        Assert.Contains("deleteActions", parts);
        Assert.DoesNotContain("methods", parts);
    }

    [Theory]
    [InlineData("all", new[] { "fields", "indexes", "relations", "deleteActions", "fieldGroups", "methods" })]
    [InlineData("fields", new[] { "fields" })]
    [InlineData("Methods, delete-actions", new[] { "methods", "deleteActions" })]
    public void Include_selects_exactly_the_named_parts(string include, string[] expected)
    {
        Assert.True(GetTableCommand.TryParseInclude(include, out var parts, out _));
        Assert.Equal(expected.OrderBy(p => p), parts.Select(p => GetTableCommand.Parts.First(k =>
            string.Equals(k, p, StringComparison.OrdinalIgnoreCase))).OrderBy(p => p));
    }

    [Fact]
    public void Include_rejects_an_unknown_part_instead_of_ignoring_it()
    {
        Assert.False(GetTableCommand.TryParseInclude("fields,feilds", out _, out var failure));
        Assert.Equal(D365FoErrorCodes.BadInput, failure!.Error!.Code);
        Assert.Contains("feilds", failure.Error.Message);
    }

    [Fact]
    public void Include_trims_a_bridge_payload_and_counts_the_methods_it_dropped()
    {
        var payload = new JsonObject
        {
            ["Name"] = "SalesLine",
            ["Fields"] = new JsonArray("ItemId", "SalesPrice"),
            ["Methods"] = new JsonArray("find", "exist", "validateWrite"),
            ["Indexes"] = new JsonArray("TransIdIdx"),
        };
        GetTableCommand.TryParseInclude("fields", out var parts, out _);

        Assert.Equal(3, GetTableCommand.ApplyInclude(payload, parts));

        Assert.NotNull(payload["Fields"]);
        Assert.Null(payload["Methods"]);
        Assert.Null(payload["Indexes"]);
        Assert.Equal(3, (int)payload["methodCount"]!);
    }

    [Fact]
    public void Get_table_leaves_methods_out_by_default_and_says_how_many()
    {
        IndexOneTable();

        var (exit, envelope) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Output = "json" }));

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.True(data.TryGetProperty("fields", out _));
        Assert.False(data.TryGetProperty("methods", out _));
        Assert.Equal(2, data.GetProperty("methodCount").GetInt32());
        Assert.Contains("--include methods", envelope.GetProperty("warnings")[0].GetString());

        var (_, all) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Include = "methods", Output = "json" }));
        var allData = all.GetProperty("data");
        Assert.Equal(2, allData.GetProperty("methods").GetArrayLength());
        Assert.False(allData.TryGetProperty("fields", out _));
        Assert.False(allData.TryGetProperty("methodCount", out _));
    }

    // ---- search class|table --model ----

    [Fact]
    public void Search_rejects_a_package_name_where_a_model_is_meant()
    {
        // The MCP search tools already resolved the model; the CLI commands passed it straight
        // to `m.Name = @model` and answered a successful zero.
        IndexOneTable();

        var (tableExit, tables) = Run(() => new D365FO.Cli.Commands.Search.SearchTableCommand().Execute(null!,
            new D365FO.Cli.Commands.Search.SearchTableCommand.Settings { Query = "ConSilent", Model = "ConPkg", Output = "json" }));
        Assert.NotEqual(0, tableExit);
        Assert.Equal(D365FoErrorCodes.ModelNotFound, ErrorCode(tables));

        var (classExit, classes) = Run(() => new D365FO.Cli.Commands.Search.SearchClassCommand().Execute(null!,
            new D365FO.Cli.Commands.Search.SearchClassCommand.Settings { Query = "ConSilent", Model = "ConPkg", Output = "json" }));
        Assert.NotEqual(0, classExit);
        Assert.Equal(D365FoErrorCodes.ModelNotFound, ErrorCode(classes));

        var (_, found) = Run(() => new D365FO.Cli.Commands.Search.SearchTableCommand().Execute(null!,
            new D365FO.Cli.Commands.Search.SearchTableCommand.Settings { Query = "ConSilent", Model = "conmodel", Output = "json" }));
        Assert.Equal(1, found.GetProperty("data").GetProperty("count").GetInt32());
    }

    // ---- get table --merged --include ----

    [Fact]
    public void Merged_returns_only_the_included_parts()
    {
        IndexOneTable(withExtension: true);

        var (exit, envelope) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Merged = true, Include = "fields", Output = "json" }));

        Assert.Equal(0, exit);
        var merged = envelope.GetProperty("data").GetProperty("merged");
        Assert.Equal(new[] { "ConExtField", "ConId" },
            merged.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).OrderBy(n => n));
        Assert.False(merged.TryGetProperty("indexes", out _));
        Assert.False(merged.TryGetProperty("relations", out _));
        Assert.False(merged.TryGetProperty("fieldGroups", out _));
        Assert.True(merged.GetProperty("complete").GetBoolean());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("all")]
    public void Merged_by_default_and_with_all_returns_every_merged_part(string? include)
    {
        IndexOneTable(withExtension: true);

        var (exit, envelope) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Merged = true, Include = include, Output = "json" }));

        Assert.Equal(0, exit);
        var merged = envelope.GetProperty("data").GetProperty("merged");
        foreach (var part in new[] { "fields", "indexes", "relations", "fieldGroups" })
            Assert.True(merged.TryGetProperty(part, out _), part);
        Assert.Equal("ConExtGroup", merged.GetProperty("fieldGroups")[0].GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("methods")]
    [InlineData("fields,deleteActions")]
    public void Merged_refuses_a_part_the_merge_does_not_fold(string include)
    {
        // Answering with the part simply missing is the silence --include was fixed to end.
        var (exit, envelope) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Merged = true, Include = include, Output = "json" }));

        Assert.NotEqual(0, exit);
        Assert.Equal(D365FoErrorCodes.BadInput, ErrorCode(envelope));
        Assert.Contains("--merged", envelope.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public void Field_groups_asked_of_the_index_say_where_to_get_them()
    {
        IndexOneTable();

        var (exit, envelope) = Run(() => new GetTableCommand().Execute(null!,
            new GetTableCommand.Settings { Name = "ConSilentTable", Include = "fieldGroups", Output = "json" }));

        Assert.Equal(0, exit);
        Assert.Contains(envelope.GetProperty("warnings").EnumerateArray(),
            w => w.GetString()!.Contains("--merged", StringComparison.Ordinal));
    }

    private void IndexOneTable(bool withExtension = false)
    {
        var packages = Path.Combine(_root, "packages");
        if (withExtension)
        {
            var extDir = Path.Combine(packages, "ConPkg", "ConModel", "AxTableExtension");
            Directory.CreateDirectory(extDir);
            File.WriteAllText(Path.Combine(extDir, "ConSilentTable.ConExt.xml"), """
                <?xml version="1.0" encoding="utf-8"?>
                <AxTableExtension xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                  <Name>ConSilentTable.ConExt</Name>
                  <FieldGroups>
                    <AxTableFieldGroup>
                      <Name>ConExtGroup</Name>
                      <Fields>
                        <AxTableFieldGroupField>
                          <DataField>ConExtField</DataField>
                        </AxTableFieldGroupField>
                      </Fields>
                    </AxTableFieldGroup>
                  </FieldGroups>
                  <Fields>
                    <AxTableField xmlns="" i:type="AxTableFieldString">
                      <Name>ConExtField</Name>
                    </AxTableField>
                  </Fields>
                </AxTableExtension>
                """);
        }
        var tableDir = Path.Combine(packages, "ConPkg", "ConModel", "AxTable");
        Directory.CreateDirectory(tableDir);
        File.WriteAllText(Path.Combine(tableDir, "ConSilentTable.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConSilentTable</Name>
              <SourceCode>
                <Declaration><![CDATA[public class ConSilentTable extends common {}]]></Declaration>
                <Methods>
                  <Method>
                    <Name>find</Name>
                    <Source><![CDATA[public static ConSilentTable find(str _id) { ConSilentTable t; return t; }]]></Source>
                  </Method>
                  <Method>
                    <Name>exist</Name>
                    <Source><![CDATA[public static boolean exist(str _id) { return true; }]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
              <Fields>
                <AxTableField xmlns="" i:type="AxTableFieldString">
                  <Name>ConId</Name>
                </AxTableField>
              </Fields>
            </AxTable>
            """);

        var code = IndexExtractCommand.ExtractCore(
            OutputMode.Kind.Json,
            packagesOverride: packages,
            databaseOverride: Path.Combine(_root, "index.sqlite"),
            onlyModel: null,
            sinceIso: null);
        Assert.Equal(0, code);
    }
}
