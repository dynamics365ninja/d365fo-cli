using System.Text.Json;
using D365FO.Cli.Commands.Index;
using D365FO.Cli.Commands.Read;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>find refs</c> reports a form data source or control method by its owner-qualified name
/// (<c>CustTable.init</c>). <c>read form --method</c> has to accept that name back, or the hit
/// cannot be opened — and a bare <c>init</c> would open the form's own method instead.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class ReadFormMemberTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-readform-{Guid.NewGuid():N}");
    private readonly string? _oldIndexDb = Environment.GetEnvironmentVariable("D365FO_INDEX_DB");

    public ReadFormMemberTests()
    {
        var packages = Path.Combine(_root, "PackagesLocalDirectory");
        var db = Path.Combine(_root, "index.sqlite");
        var formDir = Path.Combine(packages, "ConRead", "ConRead", "AxForm");
        Directory.CreateDirectory(formDir);
        File.WriteAllText(Path.Combine(formDir, "ConReadForm.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConReadForm</Name>
              <SourceCode>
                <Methods>
                  <Method>
                    <Name>init</Name>
                    <Source><![CDATA[
            public void init()
            {
                super();
                formLevelInit();
            }
            ]]></Source>
                  </Method>
                </Methods>
                <DataSources>
                  <DataSource>
                    <Name>CustTable</Name>
                    <Methods>
                      <Method>
                        <Name>init</Name>
                        <Source><![CDATA[
            public void init()
            {
                super();
                dataSourceInit();
            }
            ]]></Source>
                      </Method>
                    </Methods>
                  </DataSource>
                </DataSources>
              </SourceCode>
              <DataSources>
                <AxFormDataSource>
                  <Name>CustTable</Name>
                  <Table>CustTable</Table>
                </AxFormDataSource>
              </DataSources>
            </AxForm>
            """);

        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", db);
        Assert.Equal(0, Run(() => IndexExtractCommand.ExtractCore(
            OutputMode.Kind.Json, packages, db, onlyModel: "ConRead", sinceIso: null)).Exit);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_INDEX_DB", _oldIndexDb);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static (int Exit, string Output) Run(Func<int> command)
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

    private static JsonElement Read(string method, out int exit)
    {
        var (code, output) = Run(() => new ReadFormCommand().Execute(null!,
            new ReadFormCommand.Settings { Name = "ConReadForm", Method = method, Output = "json" }));
        exit = code;
        return JsonDocument.Parse(output).RootElement.Clone();
    }

    [Fact]
    public void A_data_source_method_reads_back_by_its_qualified_name()
    {
        var envelope = Read("CustTable.init", out var exit);

        Assert.Equal(0, exit);
        var data = envelope.GetProperty("data");
        Assert.Equal("CustTable.init", data.GetProperty("method").GetString());
        Assert.Contains("dataSourceInit", data.GetProperty("source").GetString());
    }

    [Fact]
    public void A_bare_name_still_reads_the_forms_own_method()
    {
        var envelope = Read("init", out var exit);

        Assert.Equal(0, exit);
        Assert.Contains("formLevelInit", envelope.GetProperty("data").GetProperty("source").GetString());
    }

    [Fact]
    public void An_unknown_qualified_name_is_method_not_found()
    {
        var envelope = Read("NoSuchDs.init", out var exit);

        Assert.NotEqual(0, exit);
        Assert.Equal("METHOD_NOT_FOUND", envelope.GetProperty("error").GetProperty("code").GetString());
    }
}
