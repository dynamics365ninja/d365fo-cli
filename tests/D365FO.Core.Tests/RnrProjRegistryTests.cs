using D365FO.Core.Journal;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class RnrProjRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"rnrproj-{Guid.NewGuid():N}");

    public RnrProjRegistryTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private const string ProjectXml =
        "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
        "<ItemGroup><Compile Include=\"AxTable\\Existing.xml\" /></ItemGroup></Project>";

    [Fact]
    public void TryRegisterCreate_adds_an_entry_when_project_file_has_an_item_list()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        Directory.CreateDirectory(modelFolder);
        var rnrproj = Path.Combine(modelFolder, "MyModel.rnrproj");
        File.WriteAllText(rnrproj, ProjectXml);

        var delta = RnrProjRegistry.TryRegisterCreate(modelFolder, "AxTable", "NewTable");

        Assert.NotNull(delta);
        Assert.True(delta!.WasAdded);
        // .rnrproj Include paths are always Windows/Visual-Studio-style (backslash), regardless
        // of the host OS this test runs on — not Path.Combine, which would use '/' on Linux/macOS.
        Assert.Equal(@"AxTable\NewTable.xml", delta.Include);
        var xml = File.ReadAllText(rnrproj);
        Assert.Contains("NewTable.xml", xml);
        Assert.Contains("Existing.xml", xml); // pre-existing entry untouched
    }

    [Fact]
    public void TryRegisterCreate_is_a_noop_when_entry_already_exists()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        Directory.CreateDirectory(modelFolder);
        var rnrproj = Path.Combine(modelFolder, "MyModel.rnrproj");
        File.WriteAllText(rnrproj, ProjectXml);

        var delta = RnrProjRegistry.TryRegisterCreate(modelFolder, "AxTable", "Existing");

        Assert.Null(delta);
    }

    [Fact]
    public void TryRegisterCreate_is_a_noop_when_no_rnrproj_exists()
    {
        var modelFolder = Path.Combine(_dir, "Headless", "Headless");
        Directory.CreateDirectory(modelFolder);

        var delta = RnrProjRegistry.TryRegisterCreate(modelFolder, "AxTable", "NewTable");

        Assert.Null(delta);
    }

    [Fact]
    public void TryRegisterDelete_removes_an_existing_entry()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        Directory.CreateDirectory(modelFolder);
        var rnrproj = Path.Combine(modelFolder, "MyModel.rnrproj");
        File.WriteAllText(rnrproj, ProjectXml);

        var delta = RnrProjRegistry.TryRegisterDelete(modelFolder, "AxTable", "Existing");

        Assert.NotNull(delta);
        Assert.False(delta!.WasAdded);
        var xml = File.ReadAllText(rnrproj);
        Assert.DoesNotContain("Existing.xml", xml);
    }

    [Fact]
    public void Invert_of_a_create_delta_removes_the_entry_it_added()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        Directory.CreateDirectory(modelFolder);
        var rnrproj = Path.Combine(modelFolder, "MyModel.rnrproj");
        File.WriteAllText(rnrproj, ProjectXml);

        var delta = RnrProjRegistry.TryRegisterCreate(modelFolder, "AxTable", "NewTable");
        Assert.NotNull(delta);
        Assert.Contains("NewTable.xml", File.ReadAllText(rnrproj));

        Assert.True(RnrProjRegistry.Invert(delta!));
        Assert.DoesNotContain("NewTable.xml", File.ReadAllText(rnrproj));
    }

    [Fact]
    public void Invert_of_a_delete_delta_re_adds_the_entry_it_removed()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        Directory.CreateDirectory(modelFolder);
        var rnrproj = Path.Combine(modelFolder, "MyModel.rnrproj");
        File.WriteAllText(rnrproj, ProjectXml);

        var delta = RnrProjRegistry.TryRegisterDelete(modelFolder, "AxTable", "Existing");
        Assert.NotNull(delta);
        Assert.DoesNotContain("Existing.xml", File.ReadAllText(rnrproj));

        Assert.True(RnrProjRegistry.Invert(delta!));
        Assert.Contains("Existing.xml", File.ReadAllText(rnrproj));
    }

    [Fact]
    public void FindRnrProj_checks_the_model_folder_then_its_parent()
    {
        var modelFolder = Path.Combine(_dir, "MyModel", "MyModel");
        Directory.CreateDirectory(modelFolder);
        var rnrproj = Path.Combine(_dir, "MyModel", "MyModel.rnrproj"); // one level up
        File.WriteAllText(rnrproj, ProjectXml);

        var found = RnrProjRegistry.FindRnrProj(modelFolder);

        Assert.Equal(rnrproj, found);
    }
}
