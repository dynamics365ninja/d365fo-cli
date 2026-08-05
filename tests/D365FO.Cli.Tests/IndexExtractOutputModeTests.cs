using D365FO.Cli;
using D365FO.Cli.Commands.Index;

namespace D365FO.Cli.Tests;

// Named "EnvIndexDb" (not "Console output") because it now also covers the
// D365FO_INDEX_DB-mutating tests (LabelBatchCreateTests, ScaffoldingSnapshotTests,
// D365FO.Cli.Tests.Eval.*): this class redirects the process-wide Console.Out, and
// those redirect the process-wide D365FO_INDEX_DB — two different xUnit collections
// still run in parallel with each other by default, so anything touching either
// global must share this one collection or the two hazards race across collections.
[CollectionDefinition("EnvIndexDb", DisableParallelization = true)]
public sealed class EnvIndexDbCollectionDefinition { }

[Collection("EnvIndexDb")]
public sealed class IndexExtractOutputModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-extract-{Guid.NewGuid():N}");
    private readonly string _packages;
    private readonly string _db;

    public IndexExtractOutputModeTests()
    {
        _packages = Path.Combine(_root, "PackagesLocalDirectory");
        _db = Path.Combine(_root, "index.sqlite");

        // Minimal model directory shape expected by EnumerateModelDirs.
        Directory.CreateDirectory(Path.Combine(_packages, "PkgA", "ISMModel", "AxClass"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void ExtractCore_json_mode_emits_json_envelope()
    {
        var output = Capture(() =>
        {
            var code = IndexExtractCommand.ExtractCore(
                OutputMode.Kind.Json,
                packagesOverride: _packages,
                databaseOverride: _db,
                onlyModel: "ISMModel",
                sinceIso: null);
            Assert.Equal(0, code);
        });

        Assert.Contains("\"ok\":true", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"modelsProcessed\"", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractCore_table_mode_emits_human_summary_not_json_dump()
    {
        var output = Capture(() =>
        {
            var code = IndexExtractCommand.ExtractCore(
                OutputMode.Kind.Table,
                packagesOverride: _packages,
                databaseOverride: _db,
                onlyModel: "ISMModel",
                sinceIso: null);
            Assert.Equal(0, code);
        });

        Assert.Contains("index extract completed", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Index Totals", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"ok\"", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExtractCore_raw_mode_uses_standard_json_envelope()
    {
        var output = Capture(() =>
        {
            var code = IndexExtractCommand.ExtractCore(
                OutputMode.Kind.Raw,
                packagesOverride: _packages,
                databaseOverride: _db,
                onlyModel: "ISMModel",
                sinceIso: null);
            Assert.Equal(0, code);
        });

        Assert.Contains("\"ok\":true", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"modelsProcessed\"", output, StringComparison.OrdinalIgnoreCase);
    }

    private static string Capture(Action action)
    {
        var originalOut = Console.Out;
        var writer = new StringWriter();
        try
        {
            Console.SetOut(writer);
            action();
            return writer.ToString();
        }
        finally
        {
            Console.SetOut(originalOut);
            writer.Dispose();
        }
    }
}
