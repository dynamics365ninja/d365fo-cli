using D365FO.Cli.Commands.Index;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Tests;

/// <summary>
/// <c>index refresh --model X</c> is the step the docs give for "I just changed X". Without
/// <c>--index-source</c> it used to delete X's method bodies from the full-text index, so the
/// refresh that was meant to make <c>find refs</c> current made it miss every hit in X.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class IndexRefreshKeepsSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-refresh-src-{Guid.NewGuid():N}");
    private readonly string _packages;
    private readonly string _db;

    public IndexRefreshKeepsSourceTests()
    {
        _packages = Path.Combine(_root, "PackagesLocalDirectory");
        _db = Path.Combine(_root, "index.sqlite");

        var classDir = Path.Combine(_packages, "ConRefresh", "ConRefresh", "AxClass");
        Directory.CreateDirectory(classDir);
        File.WriteAllText(Path.Combine(classDir, "ConRefreshCaller.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <AxClass xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConRefreshCaller</Name>
              <SourceCode>
                <Declaration><![CDATA[
            class ConRefreshCaller
            {
            }
            ]]></Declaration>
                <Methods>
                  <Method>
                    <Name>run</Name>
                    <Source><![CDATA[
            public void run()
            {
                ConRefreshTarget::doWork();
            }
            ]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
            </AxClass>
            """);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private int Extract(bool? indexSource) => Quiet(() => IndexExtractCommand.ExtractCore(
        OutputMode.Kind.Json, _packages, _db, onlyModel: "ConRefresh", sinceIso: null,
        indexSource: indexSource));

    private long FtsRows() => new MetadataRepository(_db).CountMethodSource("ConRefresh");

    [Fact]
    public void Refresh_without_a_source_flag_keeps_method_bodies_it_indexed_before()
    {
        Assert.Equal(0, Extract(indexSource: true));
        Assert.Equal(1, FtsRows());

        Assert.Equal(0, Extract(indexSource: null));

        Assert.Equal(1, FtsRows());
    }

    [Fact]
    public void No_index_source_on_refresh_drops_the_method_bodies()
    {
        Assert.Equal(0, Extract(indexSource: true));

        var app = BuildApp();
        var code = Quiet(() => app.Run(new[]
        {
            "index", "refresh", "--model", "ConRefresh", "--packages", _packages, "--db", _db,
            "--force", "--no-index-source", "--output", "json",
        }));

        Assert.Equal(0, code);
        Assert.Equal(0, FtsRows());
        Assert.False(new MetadataRepository(_db).IsSourceIndexed("ConRefresh"));
    }

    [Fact]
    public void Index_source_and_no_index_source_together_are_refused_before_any_write()
    {
        var app = BuildApp();
        string output = "";
        var code = Quiet(() => app.Run(new[]
        {
            "index", "refresh", "--model", "ConRefresh", "--packages", _packages, "--db", _db,
            "--index-source", "--no-index-source", "--output", "json",
        }), o => output = o);

        Assert.NotEqual(0, code);
        Assert.Contains("BAD_INPUT", output, StringComparison.Ordinal);
        Assert.False(File.Exists(_db));
    }

    private static CommandApp BuildApp()
    {
        var scopedConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(new StringWriter()),
        });
        return CliApp.Build(scopedConsole);
    }

    private static int Quiet(Func<int> action, Action<string>? output = null)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            return action();
        }
        finally
        {
            Console.SetOut(originalOut);
            output?.Invoke(writer.ToString());
        }
    }
}
