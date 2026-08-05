using D365FO.Core.Eval;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests.Eval;

public class EvalScorerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-scorer-{Guid.NewGuid():N}.sqlite");
    private readonly string _goldensRoot = Path.Combine(Path.GetTempPath(), $"eval-scorer-goldens-{Guid.NewGuid():N}");
    private readonly MetadataRepository _repo;

    public EvalScorerTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        Directory.CreateDirectory(_goldensRoot);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
        if (Directory.Exists(_goldensRoot)) Directory.Delete(_goldensRoot, recursive: true);
    }

    private EvalCase MakeCase(string goldenPath, IReadOnlyList<string>? ignore = null)
        => new(
            Id: "L0-test", Title: "Test", Tier: 0, Instruction: "test",
            CanonicalArgs: null, TargetArtifactTypes: new[] { "AxEdt" },
            GoldenPath: goldenPath, Tags: Array.Empty<string>(),
            Ignore: ignore ?? Array.Empty<string>(), RequiresFixtureIndex: false, GoldenPending: false);

    private string WriteFile(string content, string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"eval-scorer-actual-{Guid.NewGuid():N}-{fileName}");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Matching_golden_scores_GoldenMatch_true()
    {
        var golden = "<AxEdt><Name>Foo</Name><Label>Bar</Label></AxEdt>";
        var goldenDir = Path.Combine(_goldensRoot, "L0-test");
        Directory.CreateDirectory(goldenDir);
        File.WriteAllText(Path.Combine(goldenDir, "Foo.xml"), golden);
        var actual = WriteFile(golden, "actual.xml");

        var score = EvalScorer.Score(MakeCase("L0-test"), actual, _goldensRoot, _repo);

        Assert.True(score.GoldenMatch);
        Assert.True(score.GoldenDiff.IsMatch);
    }

    [Fact]
    public void Differing_golden_scores_GoldenMatch_false_with_a_diff()
    {
        var goldenDir = Path.Combine(_goldensRoot, "L0-test");
        Directory.CreateDirectory(goldenDir);
        File.WriteAllText(Path.Combine(goldenDir, "Foo.xml"), "<AxEdt><Name>Foo</Name><Label>Bar</Label></AxEdt>");
        var actual = WriteFile("<AxEdt><Name>Foo</Name></AxEdt>", "actual.xml");

        var score = EvalScorer.Score(MakeCase("L0-test"), actual, _goldensRoot, _repo);

        Assert.False(score.GoldenMatch);
        Assert.Contains("AxEdt/Label", score.GoldenDiff.Missing);
    }

    [Fact]
    public void Missing_golden_directory_scores_GoldenMatch_false_without_throwing()
    {
        var actual = WriteFile("<AxEdt><Name>Foo</Name></AxEdt>", "actual.xml");

        var score = EvalScorer.Score(MakeCase("L0-does-not-exist"), actual, _goldensRoot, _repo);

        Assert.False(score.GoldenMatch);
    }

    [Fact]
    public void Reference_violation_is_reflected_in_the_scorecard()
    {
        var goldenDir = Path.Combine(_goldensRoot, "L0-test");
        Directory.CreateDirectory(goldenDir);
        var xml = "<AxClass><SourceCode><Declaration>class Foo {}</Declaration>" +
                  "<Methods><Method><Name>bar</Name><Source>void bar() { tableStr(NoSuchTableAtAll); }</Source></Method></Methods>" +
                  "</SourceCode></AxClass>";
        File.WriteAllText(Path.Combine(goldenDir, "Foo.xml"), xml);
        var actual = WriteFile(xml, "actual.xml");

        var score = EvalScorer.Score(MakeCase("L0-test"), actual, _goldensRoot, _repo);

        Assert.False(score.ReferencesClean);
        Assert.True(score.ReferenceErrors > 0);
        // Both sides are byte-identical, so the golden-diff dimension is independent of the reference-gate dimension.
        Assert.True(score.GoldenMatch);
    }

    [Fact]
    public void Ignore_list_on_the_case_is_passed_through_to_the_diff()
    {
        var goldenDir = Path.Combine(_goldensRoot, "L0-test");
        Directory.CreateDirectory(goldenDir);
        File.WriteAllText(Path.Combine(goldenDir, "Foo.xml"), "<AxEdt><Name>Foo</Name><Volatile>111</Volatile></AxEdt>");
        var actual = WriteFile("<AxEdt><Name>Foo</Name><Volatile>222</Volatile></AxEdt>", "actual.xml");

        var withoutIgnore = EvalScorer.Score(MakeCase("L0-test"), actual, _goldensRoot, _repo);
        Assert.False(withoutIgnore.GoldenMatch);

        var withIgnore = EvalScorer.Score(MakeCase("L0-test", new[] { "AxEdt/Volatile" }), actual, _goldensRoot, _repo);
        Assert.True(withIgnore.GoldenMatch);
    }
}
