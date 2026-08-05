using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests.Eval;

public class EvalCaseCatalogTests
{
    private static readonly string RepoRoot =
        EvalPaths.FindRepoRoot() ?? throw new InvalidOperationException("Could not locate repo root for tests.");

    [Fact]
    public void Loads_the_real_authored_catalog_with_no_errors()
    {
        var (cases, errors) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(RepoRoot));

        Assert.Empty(errors);
        Assert.Equal(51, cases.Count);
        Assert.Contains(cases, c => c.Id == "L0-edt-basic");
        Assert.Contains(cases, c => c.Id == "L0-enum-basic");
        Assert.Contains(cases, c => c.Id == "L1-table-basic");
        Assert.Contains(cases, c => c.Id == "L1-class-basic");
        Assert.Contains(cases, c => c.Id == "L2-coc-extension");
        Assert.Contains(cases, c => c.Id == "L2-security-policy-basic");
        Assert.Contains(cases, c => c.Id == "L1-view-basic");
        Assert.Contains(cases, c => c.Id == "L2-datasource-method-basic");
    }

    [Fact]
    public void Every_authored_case_has_a_captured_golden()
    {
        var (cases, _) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(RepoRoot));

        foreach (var c in cases.Where(c => !c.GoldenPending))
        {
            var dir = Path.Combine(EvalPaths.GoldensDir(RepoRoot), c.GoldenPath);
            Assert.True(Directory.Exists(dir), $"{c.Id}: golden directory missing: {dir}");
            var files = Directory.GetFiles(dir, "*.xml");
            Assert.True(files.Length == 1, $"{c.Id}: expected exactly 1 golden *.xml in {dir}, found {files.Length}");
        }
    }

    /// <summary>
    /// `requires_fixture_index: true` makes every replay of that case pay for a
    /// full extract of tests/Samples/MiniAot, so it must be earned: the case has
    /// to actually name one of the fixture's objects. Asserted as a property
    /// rather than a hard-coded id list, which went stale the first time the
    /// catalog grew.
    /// </summary>
    [Fact]
    public void Only_cases_grounded_against_the_FmVehicle_fixture_require_the_fixture_index()
    {
        var (cases, _) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(RepoRoot));
        string[] fixtureObjects = { "FmVehicleService", "FmVehicleLine", "FmVehicle" };

        foreach (var c in cases.Where(c => c.RequiresFixtureIndex))
        {
            var args = string.Join(' ', c.CanonicalArgs ?? new List<string>());
            Assert.True(
                fixtureObjects.Any(o => args.Contains(o, StringComparison.Ordinal)),
                $"{c.Id}: requires_fixture_index is true but its canonical_args name no fixture object " +
                $"({string.Join('/', fixtureObjects)}) — drop the flag or ground the case.");
        }
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_unknown_id()
    {
        var (cases, _) = EvalCaseCatalog.LoadAll(EvalPaths.CasesDir(RepoRoot));

        Assert.NotNull(EvalCaseCatalog.Find(cases, "l0-edt-basic"));
        Assert.Null(EvalCaseCatalog.Find(cases, "L9-does-not-exist"));
    }

    [Theory]
    [InlineData("bad-id.json", "{\"id\":\"not-a-valid-id\",\"title\":\"x\",\"tier\":0,\"instruction\":\"x\",\"target_artifact_types\":[\"AxEdt\"],\"golden_path\":\"x\"}", "must match")]
    [InlineData("tier-mismatch.json", "{\"id\":\"L1-x\",\"title\":\"x\",\"tier\":2,\"instruction\":\"x\",\"target_artifact_types\":[\"AxEdt\"],\"golden_path\":\"x\"}", "does not match id prefix")]
    [InlineData("missing-title.json", "{\"id\":\"L1-x\",\"tier\":1,\"instruction\":\"x\",\"target_artifact_types\":[\"AxEdt\"],\"golden_path\":\"x\"}", "'title' is required")]
    [InlineData("missing-types.json", "{\"id\":\"L1-x\",\"title\":\"x\",\"tier\":1,\"instruction\":\"x\",\"target_artifact_types\":[],\"golden_path\":\"x\"}", "'target_artifact_types' must be non-empty")]
    public void Rejects_malformed_case_files_with_a_specific_reason(string fileName, string json, string expectedFragment)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"d365fo-eval-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, fileName), json);

            var (cases, errors) = EvalCaseCatalog.LoadAll(dir);

            Assert.Empty(cases);
            var error = Assert.Single(errors);
            Assert.Contains(expectedFragment, error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_malformed_case_file_does_not_take_the_rest_of_the_catalog_down()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"d365fo-eval-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "L0-good.json"),
                "{\"id\":\"L0-good\",\"title\":\"Good\",\"tier\":0,\"instruction\":\"x\",\"target_artifact_types\":[\"AxEdt\"],\"golden_path\":\"L0-good\"}");
            File.WriteAllText(Path.Combine(dir, "L0-bad.json"), "{ not json");

            var (cases, errors) = EvalCaseCatalog.LoadAll(dir);

            var good = Assert.Single(cases);
            Assert.Equal("L0-good", good.Id);
            var error = Assert.Single(errors);
            Assert.Contains("L0-bad.json", error);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
