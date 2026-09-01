using D365FO.Core.Analysis;
using D365FO.Core.Index;
using D365FO.Core.FormPatterns;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #164 / R5 — the mined-usage cross-check: what the installation uses versus what this
/// repo's catalogs claim to know.
/// </summary>
public sealed class CatalogCrossCheckTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"crosscheck-{Guid.NewGuid():N}.sqlite");
    private readonly string _packages = Path.Combine(Path.GetTempPath(), $"crosscheck-pkg-{Guid.NewGuid():N}");
    private readonly MetadataRepository _repo;

    public CatalogCrossCheckTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        Directory.CreateDirectory(_packages);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
        try { Directory.Delete(_packages, recursive: true); } catch { }
    }

    private void SeedForms(params (string Name, string? Pattern)[] forms) =>
        _repo.ApplyExtract(new ExtractBatch(
            Model: "ConFleet", Publisher: "Contoso", Layer: "isv", IsCustom: true,
            Tables: [], Classes: [], Edts: [], Enums: [], MenuItems: [], CocExtensions: [], Labels: [])
        {
            Forms = forms.Select(f => new ExtractedForm(f.Name, null, Array.Empty<ExtractedFormDataSource>())
            {
                Pattern = f.Pattern,
                PatternVersion = f.Pattern is null ? null : "1.0",
            }).ToArray(),
        });

    /// <summary>Create <c>&lt;packages&gt;/&lt;Package&gt;/&lt;Model&gt;/&lt;folder&gt;</c>.</summary>
    private void SeedAotFolder(string package, string folder) =>
        Directory.CreateDirectory(Path.Combine(_packages, package, package, folder));

    // ── form patterns ────────────────────────────────────────────────────────

    [Fact]
    public void A_pattern_the_registry_knows_is_not_a_gap()
    {
        SeedForms(("ConVehicleListPage", "SimpleList"));

        var report = CatalogCrossCheck.Run(_repo);

        Assert.True(report.Clean);
        Assert.Empty(report.Gaps);
    }

    [Fact]
    public void A_pattern_in_use_that_the_registry_has_never_heard_of_is_a_gap()
    {
        SeedForms(("ConVehicleHub", "ConInventedPattern"), ("ConVehicleHub2", "ConInventedPattern"));

        var report = CatalogCrossCheck.Run(_repo);

        var gap = Assert.Single(report.Gaps);
        Assert.Equal(CatalogCrossCheck.FormPatternCatalog, gap.Catalog);
        Assert.Equal("ConInventedPattern", gap.Item);
        Assert.Equal(2, gap.Observed);
        Assert.Contains("emit-form-patterns.ps1", gap.Detail);
        Assert.False(report.Clean);
    }

    [Fact]
    public void Custom_is_the_AOT_marker_for_no_pattern_and_is_never_a_gap()
    {
        // The most important false positive to suppress: Custom is the fourth most common
        // <Pattern> value on a real installation, and every form carrying it has no
        // PatternVersion. Treating it as a pattern reports the largest "gap" in the report and
        // it is not a gap at all.
        SeedForms(("ConVehicleCustom", "Custom"), ("ConVehicleNone", null));

        var report = CatalogCrossCheck.Run(_repo);

        Assert.True(report.Clean);
    }

    [Fact]
    public void A_pattern_the_registry_knows_but_nothing_uses_is_reported_separately()
    {
        SeedForms(("ConVehicleListPage", "SimpleList"));

        var report = CatalogCrossCheck.Run(_repo);

        Assert.NotEmpty(report.Unused);
        Assert.All(report.Unused, u => Assert.Equal(CatalogCrossCheck.FormPatternCatalog, u.Catalog));
        // Unused entries say nothing about correctness.
        Assert.True(report.Clean);
    }

    // ── AOT folders ──────────────────────────────────────────────────────────

    [Fact]
    public void An_AOT_folder_the_registry_does_not_name_is_uncovered_not_a_gap()
    {
        // The tool covers what it was built to cover; a real installation has dozens of families
        // it does not. That is narrowness, not wrongness, and mixing the two buries the findings
        // that matter.
        SeedAotFolder("ConFleet", "AxKPI");
        SeedAotFolder("ConFleet2", "AxKPI");

        var report = CatalogCrossCheck.Run(_repo, _packages);

        var uncovered = Assert.Single(report.Uncovered);
        Assert.Equal("AxKPI", uncovered.Folder);
        Assert.Equal(2, uncovered.Models);
        Assert.True(report.Clean);
    }

    [Fact]
    public void A_folder_the_registry_names_is_neither()
    {
        SeedAotFolder("ConFleet", "AxTable");

        var report = CatalogCrossCheck.Run(_repo, _packages);

        Assert.Empty(report.Uncovered);
        Assert.Empty(report.Gaps);
    }

    [Fact]
    public void Non_Ax_folders_are_ignored()
    {
        SeedAotFolder("ConFleet", "Descriptor");
        SeedAotFolder("ConFleet", "XppMetadata");

        var report = CatalogCrossCheck.Run(_repo, _packages);

        Assert.Empty(report.Uncovered);
    }

    [Fact]
    public void A_missing_packages_path_skips_the_folder_half_rather_than_failing()
    {
        SeedForms(("ConVehicleListPage", "SimpleList"));

        var absent = CatalogCrossCheck.Run(_repo, Path.Combine(_packages, "does-not-exist"));
        var none = CatalogCrossCheck.Run(_repo, null);

        Assert.Empty(absent.Uncovered);
        Assert.Empty(none.Uncovered);
        Assert.True(absent.Clean);
    }

    // ── the check against the catalogs as shipped ────────────────────────────

    [Fact]
    public void Every_registry_pattern_name_resolves_in_the_registry_itself()
    {
        // Cheap self-consistency: the check trusts VersionsOf() to answer for any name the
        // registry lists, so a name that does not resolve would make the cross-check report
        // the catalog as short of itself.
        foreach (var name in FormPatternRegistry.All.Where(p => p.Active).Select(p => p.Name).Distinct())
            Assert.NotEmpty(FormPatternRegistry.VersionsOf(name));
    }
}
