using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Where a model is looked for and where a write may land, when the model store is not
/// <c>PackagesLocalDirectory</c> (a unified developer environment, a repo checkout).
/// </summary>
public sealed class ModelFolderRootsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-modelroots-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static D365FoSettings Settings(string? packages, string? workspace, params string[] custom)
        => new(packages, workspace, "index.sqlite", [], ["en-us"], custom);

    private string MakeModel(string root, string model)
    {
        var dir = Path.Combine(root, model, model);
        Directory.CreateDirectory(Path.Combine(dir, "AxClass"));
        return dir;
    }

    [Fact]
    public void FindModelDir_prefers_the_custom_model_store_over_the_standard_root()
    {
        var standard = Path.Combine(_root, "PackagesLocalDirectory");
        var store = Path.Combine(_root, "Metadata");
        MakeModel(standard, "ConFleet");
        var source = MakeModel(store, "ConFleet");

        var found = IndexSync.FindModelDir(Settings(standard, null, store).ModelRoots, "ConFleet");

        Assert.Equal(source, found);
    }

    [Fact]
    public void FindModelDir_skips_roots_that_are_not_there()
    {
        var store = Path.Combine(_root, "Metadata");
        var source = MakeModel(store, "ConFleet");

        var found = IndexSync.FindModelDir([Path.Combine(_root, "gone"), null, store], "ConFleet");

        Assert.Equal(source, found);
        Assert.Null(IndexSync.FindModelDir([store], "NoSuchModel"));
    }

    [Fact]
    public void Write_roots_include_the_custom_model_store_and_the_workspace()
    {
        var cfg = Settings(@"C:\Packages", @"C:\Repo\Metadata", @"C:\Store1", @"C:\Store2");

        Assert.Equal(new[] { @"C:\Packages", @"C:\Repo\Metadata", @"C:\Store1", @"C:\Store2" }, cfg.WriteRoots);
        Assert.Equal(new[] { @"C:\Store1", @"C:\Store2", @"C:\Packages" }, cfg.ModelRoots);
    }

    [Fact]
    public void A_scaffold_write_with_a_project_lists_the_object_and_journals_the_entry()
    {
        var modelDir = MakeModel(Path.Combine(_root, "Metadata"), "ConFleet");
        var project = Path.Combine(_root, "Projects", "ConFleet.rnrproj");
        Directory.CreateDirectory(Path.GetDirectoryName(project)!);
        File.WriteAllText(project,
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
            "  <PropertyGroup>\n    <Model>ConFleet</Model>\n  </PropertyGroup>\n</Project>\n");

        var result = Scaffolding.ScaffoldFileWriter.Write(
            Scaffolding.XppScaffolder.Class("ConFleetPosting"),
            Path.Combine(modelDir, "AxClass", "ConFleetPosting.xml"),
            project: project);

        Assert.Null(result.ProjectError);
        Assert.True(result.Project!.Added);
        Assert.NotNull(result.Project.Delta);
        Assert.Contains("<Content Include=\"AxClass\\ConFleetPosting\">", File.ReadAllText(project));
    }

    [Fact]
    public void A_refused_write_says_which_setting_admits_its_root()
    {
        var ex = Assert.Throws<IOException>(() => Guardrails.PathGuard.EnsureWithinBoundary(
            Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "elsewhere-" + Guid.NewGuid().ToString("N"), "X.xml"),
            Path.Combine(_root, "Packages")));

        Assert.Contains("D365FO_CUSTOM_PACKAGES_PATH", ex.Message);
    }
}
