using D365FO.Core.Eval;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests.Eval;

/// <summary>
/// Scoring the artefacts a case produces BESIDE its main one, and the seeds a case starts from.
/// </summary>
/// <remarks>
/// <para>
/// Several <c>generate</c> commands emit more than one file — a report is a report plus a TempDB
/// table, a DP, a controller and an output menu item. Only the main one used to be diffed, so
/// the companions were captured once under <c>_companions/</c> and then never checked again:
/// the first run with this in place found ten companions across the two report cases that no
/// golden had ever pinned.
/// </para>
/// <para>
/// Seeds are the other direction: <c>table-relation</c> and <c>find-methods</c> augment a table
/// that already exists and refuse <c>--out</c> outright, so a case for either needs an input
/// artefact rather than producing one.
/// </para>
/// </remarks>
public class EvalCompanionAndSeedTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"eval-comp-{Guid.NewGuid():N}.sqlite");
    private readonly string _goldensRoot = Path.Combine(Path.GetTempPath(), $"eval-comp-goldens-{Guid.NewGuid():N}");
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"eval-comp-work-{Guid.NewGuid():N}");
    private readonly MetadataRepository _repo;

    public EvalCompanionAndSeedTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        Directory.CreateDirectory(_goldensRoot);
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
        foreach (var dir in new[] { _goldensRoot, _workDir })
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static EvalCase Case(string goldenPath) => new(
        Id: "L0-test", Title: "Test", Tier: 0, Instruction: "test",
        CanonicalArgs: null, TargetArtifactTypes: new[] { "AxEdt" },
        GoldenPath: goldenPath, Tags: Array.Empty<string>(),
        Ignore: Array.Empty<string>(), RequiresFixtureIndex: false, GoldenPending: false);

    private string Golden(string body, string name = "Foo.xml")
    {
        var dir = Path.Combine(_goldensRoot, "L0-test");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), body);
        return dir;
    }

    private void GoldenCompanion(string name, string body)
    {
        var dir = Path.Combine(_goldensRoot, "L0-test", "_companions");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name), body);
    }

    private string Produce(string name, string body)
    {
        var path = Path.Combine(_workDir, name);
        File.WriteAllText(path, body);
        return path;
    }

    private const string Main = "<AxEdt><Name>Foo</Name></AxEdt>";

    [Fact]
    public void A_companion_that_matches_its_golden_keeps_the_case_passing()
    {
        Golden(Main);
        GoldenCompanion("FooHelper.xml", "<AxClass><Name>FooHelper</Name></AxClass>");
        var actual = Produce("actual.xml", Main);
        Produce("FooHelper.xml", "<AxClass><Name>FooHelper</Name></AxClass>");

        var score = EvalScorer.Score(Case("L0-test"), actual, _goldensRoot, _repo, producedDir: _workDir);

        Assert.True(score.GoldenMatch);
    }

    [Fact]
    public void A_companion_that_differs_is_reported_under_its_own_name()
    {
        Golden(Main);
        GoldenCompanion("FooHelper.xml", "<AxClass><Name>FooHelper</Name><IsFinal>Yes</IsFinal></AxClass>");
        var actual = Produce("actual.xml", Main);
        Produce("FooHelper.xml", "<AxClass><Name>FooHelper</Name></AxClass>");

        var score = EvalScorer.Score(Case("L0-test"), actual, _goldensRoot, _repo, producedDir: _workDir);

        Assert.False(score.GoldenMatch);
        Assert.Contains(score.GoldenDiff.Missing, m => m.StartsWith("_companions/FooHelper.xml:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_companion_the_run_did_not_produce_is_missing()
    {
        Golden(Main);
        GoldenCompanion("FooHelper.xml", "<AxClass><Name>FooHelper</Name></AxClass>");
        var actual = Produce("actual.xml", Main);

        var score = EvalScorer.Score(Case("L0-test"), actual, _goldensRoot, _repo, producedDir: _workDir);

        Assert.Contains("_companions/FooHelper.xml", score.GoldenDiff.Missing);
    }

    /// <summary>
    /// The drift this exists for: a command starts emitting a file nobody reviewed, and every
    /// other check still passes because the main artefact is unchanged.
    /// </summary>
    [Fact]
    public void A_companion_no_golden_names_is_extra()
    {
        Golden(Main);
        var actual = Produce("actual.xml", Main);
        Produce("FooSurprise.xml", "<AxClass><Name>FooSurprise</Name></AxClass>");

        var score = EvalScorer.Score(Case("L0-test"), actual, _goldensRoot, _repo, producedDir: _workDir);

        Assert.False(score.GoldenMatch);
        Assert.Contains("_companions/FooSurprise.xml", score.GoldenDiff.Extra);
    }

    /// <summary>
    /// Without a directory the caller owns, a sibling file says nothing about what produced it —
    /// the scorer must not read the whole temp directory as this run's output.
    /// </summary>
    [Fact]
    public void Siblings_are_not_treated_as_output_when_no_produced_directory_is_given()
    {
        Golden(Main);
        var actual = Produce("actual.xml", Main);
        Produce("SomeoneElses.xml", "<AxClass><Name>SomeoneElses</Name></AxClass>");

        var score = EvalScorer.Score(Case("L0-test"), actual, _goldensRoot, _repo);

        Assert.True(score.GoldenMatch);
    }

    // ── the seeds themselves ──────────────────────────────────────────────

    /// <summary>
    /// The index a replay builds comes from the MiniAot fixture. A seed that had drifted from
    /// the fixture file of the same name would be a case merging into a table the tool never saw.
    /// </summary>
    [Fact]
    public void A_seed_named_after_a_fixture_table_is_identical_to_it()
    {
        var root = EvalPaths.FindRepoRoot(AppContext.BaseDirectory);
        Assert.NotNull(root);

        var seedsDir = EvalPaths.SeedsDir(root!);
        if (!Directory.Exists(seedsDir)) return;

        foreach (var seed in Directory.GetFiles(seedsDir, "*.xml"))
        {
            var name = Path.GetFileName(seed);
            var fixture = Directory
                .EnumerateFiles(EvalPaths.FixtureDir(root!), name, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (fixture is null) continue;

            Assert.True(
                File.ReadAllBytes(seed).SequenceEqual(File.ReadAllBytes(fixture)),
                $"eval/seeds/{name} has drifted from the fixture file it mirrors ({fixture}).");
        }
    }

    [Fact]
    public void Every_seed_a_case_names_exists()
    {
        var root = EvalPaths.FindRepoRoot(AppContext.BaseDirectory);
        Assert.NotNull(root);

        var (cases, _) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(root!));
        foreach (var c in cases.Where(c => c.ApplyToSeed is not null))
        {
            var seed = Path.Combine(EvalPaths.SeedsDir(root!), c.ApplyToSeed!);
            Assert.True(File.Exists(seed), $"{c.Id}: apply_to_seed names {c.ApplyToSeed}, which is not in eval/seeds/.");
        }
    }
}
