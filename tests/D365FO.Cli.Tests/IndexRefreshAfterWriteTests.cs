using D365FO.Cli.Commands.Daemon;
using D365FO.Core;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;

namespace D365FO.Cli.Tests;

/// <summary>
/// The CLI side of re-indexing after a write: the rendered result says what was refreshed,
/// <c>--no-refresh</c> is honoured, and the daemon watches the custom roots too.
/// </summary>
[Collection("EnvIndexDb")]
public sealed class IndexRefreshAfterWriteTests : IDisposable
{
    private readonly string _work = Path.Combine(Path.GetTempPath(), $"d365fo-refresh-write-{Guid.NewGuid():N}");
    private readonly string _root;
    private readonly string _db;

    public IndexRefreshAfterWriteTests()
    {
        _root = Path.Combine(_work, "PackagesLocalDirectory");
        Directory.CreateDirectory(Path.Combine(_root, "ConFleet", "ConFleet", "AxClass"));
        _db = Path.Combine(_work, "index.sqlite");
    }

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { }
    }

    [Fact]
    public void A_rendered_write_result_reports_the_model_it_re_indexed()
    {
        string output;
        using (IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root }))
        {
            var path = Path.Combine(_root, "ConFleet", "ConFleet", "AxClass", "ConFleetDispatcher.xml");
            ScaffoldFileWriter.Write(
                "<AxClass><Name>ConFleetDispatcher</Name><SourceCode><Declaration><![CDATA[public class ConFleetDispatcher { }]]></Declaration><Methods /></SourceCode></AxClass>",
                path);
            output = Capture(() => RenderHelpers.Render(OutputMode.Kind.Json, ToolResult<object>.Success(new { path })));
        }

        Assert.Contains("\"indexRefreshed\":{\"model\":\"ConFleet\"", output, StringComparison.Ordinal);
        Assert.NotNull(new MetadataRepository(_db).GetClassDetails("ConFleetDispatcher"));
    }

    [Fact]
    public void A_read_with_nothing_written_renders_unchanged()
    {
        string output;
        using (IndexAutoRefresh.BeginScope(databasePath: _db, packagesRoots: new[] { _root }))
            output = Capture(() => RenderHelpers.Render(OutputMode.Kind.Json, ToolResult<object>.Success(new { count = 0 })));

        Assert.DoesNotContain("indexRefreshed", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { "generate", "class", "X", "--no-refresh" }, true)]
    [InlineData(new[] { "--no-refresh", "modify", "method" }, true)]
    [InlineData(new[] { "generate", "class", "X" }, false)]
    public void No_refresh_is_a_global_flag_peeled_off_before_parsing(string[] args, bool expected)
    {
        var (rest, noRefresh) = AutoRefreshArgs.Strip(args);

        Assert.Equal(expected, noRefresh);
        Assert.DoesNotContain(AutoRefreshArgs.Flag, rest);
    }

    [Fact]
    public void No_refresh_after_a_literal_double_dash_is_left_alone()
    {
        var (rest, noRefresh) = AutoRefreshArgs.Strip(new[] { "eval", "run", "--", "--no-refresh" });

        Assert.False(noRefresh);
        Assert.Equal(new[] { "eval", "run", "--", "--no-refresh" }, rest);
    }

    [Fact]
    public void The_daemon_watches_the_custom_packages_roots_as_well()
    {
        // A UDE keeps custom models in their own root. Watching only the packages path meant an
        // edit to a custom model never refreshed anything.
        var custom = Path.Combine(_work, "CustomMetadata");
        Directory.CreateDirectory(custom);
        using var cts = new CancellationTokenSource();

        using var handle = DaemonStartCommand.StartWatchers(new[] { _root, custom }, _db, 3000, cts.Token);

        var watched = DaemonStartCommand.WatchedPaths(handle);
        Assert.Equal(2, watched.Count);
        Assert.Contains(watched, w => w.EndsWith("CustomMetadata", StringComparison.Ordinal));
    }

    private static string Capture(Func<int> render)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            render();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
