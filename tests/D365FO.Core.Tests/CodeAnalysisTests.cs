using D365FO.Core.Analysis;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The three "learn from the installation" modes, and the one property that matters most about
/// them: they never report an unread corpus as an empty one.
/// </summary>
/// <remarks>
/// The index here is empty, which is exactly the condition under which a search over method
/// bodies is tempted to answer "no callers" — and "no callers" reads as "unused", which is how a
/// caller talks itself into deleting something that is used everywhere. Every mode has to say it
/// searched nothing.
/// </remarks>
public class CodeAnalysisTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-analysis-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public CodeAnalysisTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
        GC.SuppressFinalize(this);
    }

    private static (bool Searched, string? Caveat) CoverageOf(ToolResult<object> result)
    {
        var coverage = result.Data!.GetType().GetProperty("coverage")!.GetValue(result.Data)!;
        return (
            (bool)coverage.GetType().GetProperty("Searched")!.GetValue(coverage)!,
            (string?)coverage.GetType().GetProperty("Caveat")!.GetValue(coverage));
    }

    [Fact]
    public void Patterns_over_an_unread_corpus_says_it_read_nothing()
    {
        var result = CodeAnalysis.Patterns(_repo, "number sequence", model: null);

        Assert.True(result.Ok);
        var (searched, caveat) = CoverageOf(result);
        Assert.False(searched);
        Assert.NotNull(caveat);
        // The caveat has to be reachable as a warning too — a caller reading only the envelope
        // must not take the empty list at face value.
        Assert.NotNull(result.Warnings);
        Assert.Contains("not evidence of absence", result.Warnings![0], StringComparison.Ordinal);
    }

    [Fact]
    public void Implementations_over_an_unread_corpus_says_it_read_nothing()
    {
        var result = CodeAnalysis.Implementations(_repo, "validateWrite", model: null);

        Assert.True(result.Ok);
        Assert.False(CoverageOf(result).Searched);
        Assert.NotNull(result.Warnings);
    }

    [Fact]
    public void Api_usage_over_an_unread_corpus_says_it_read_nothing()
    {
        var result = CodeAnalysis.ApiUsage(_repo, "NumberSeq", model: null);

        Assert.True(result.Ok);
        Assert.False(CoverageOf(result).Searched);
        Assert.NotNull(result.Warnings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    // Every word is too short to search on: a scenario of noise is refused rather than answered
    // with whatever the corpus happens to contain.
    [InlineData("a of to")]
    public void A_scenario_with_nothing_to_search_on_is_refused(string scenario)
    {
        var result = CodeAnalysis.Patterns(_repo, scenario, model: null);
        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
    }

    [Fact]
    public void An_unknown_method_says_where_kernel_methods_live_rather_than_just_zero()
    {
        var result = CodeAnalysis.Implementations(_repo, "insert", model: null);

        Assert.True(result.Ok);
        var note = (string?)result.Data!.GetType().GetProperty("note")!.GetValue(result.Data);
        Assert.Contains("kernel", note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Method_declarations_come_back_empty_rather_than_throwing_on_an_empty_index()
    {
        Assert.Empty(_repo.FindMethodDeclarations("validateWrite"));
        Assert.Empty(_repo.FindMethodDeclarations(""));
    }
}
