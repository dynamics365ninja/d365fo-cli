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
    private const string VisualStudioProject =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <PropertyGroup>\r\n    <Model>MyModel</Model>\r\n  </PropertyGroup>\r\n" +
        "  <ItemGroup>\r\n    <Folder Include=\"Classes\\\"/>\r\n  </ItemGroup>\r\n" +
        "  <ItemGroup>\r\n    <Content Include=\"AxClass\\Existing\">\r\n      <SubType>Content</SubType>\r\n" +
        "      <Name>Existing</Name>\r\n      <Link>Classes\\Existing</Link>\r\n    </Content>\r\n  </ItemGroup>\r\n" +
        "</Project>\r\n";

    [Fact]
    public void RegisterInProject_writes_the_item_shape_visual_studio_writes()
    {
        var rnrproj = Path.Combine(_dir, "Projects", "MyProject.rnrproj");
        Directory.CreateDirectory(Path.GetDirectoryName(rnrproj)!);
        File.WriteAllText(rnrproj, VisualStudioProject);

        var reg = RnrProjRegistry.RegisterInProject(rnrproj, "AxWorkflowTemplate", "MyWorkflow", "MyModel");

        Assert.True(reg.Added);
        Assert.Equal(@"AxWorkflowTemplate\MyWorkflow", reg.Include);
        var doc = System.Xml.Linq.XDocument.Load(rnrproj);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var item = doc.Descendants(ns + "Content").Single(e => (string?)e.Attribute("Include") == reg.Include);
        Assert.Equal(@"Workflow Types\MyWorkflow", item.Element(ns + "Link")?.Value);
        // Listed beside the existing objects, not among the <Folder> entries.
        Assert.Same(doc.Descendants(ns + "Content").First().Parent, item.Parent);
        Assert.Contains("\r\n    <Content Include=\"AxWorkflowTemplate\\MyWorkflow\">", File.ReadAllText(rnrproj));
    }

    [Fact]
    public void RegisterInProject_does_not_list_an_object_twice()
    {
        var rnrproj = Path.Combine(_dir, "MyProject.rnrproj");
        File.WriteAllText(rnrproj, VisualStudioProject);

        var reg = RnrProjRegistry.RegisterInProject(rnrproj, "AxClass", "Existing", "MyModel");

        Assert.False(reg.Added);
        Assert.Null(reg.Delta);
        Assert.Equal(VisualStudioProject, File.ReadAllText(rnrproj));
    }

    [Fact]
    public void RegisterInProject_refuses_an_object_of_another_model()
    {
        var rnrproj = Path.Combine(_dir, "MyProject.rnrproj");
        File.WriteAllText(rnrproj, VisualStudioProject);

        var ex = Assert.Throws<InvalidOperationException>(
            () => RnrProjRegistry.RegisterInProject(rnrproj, "AxClass", "Other", "SomeOtherModel"));

        Assert.Contains("MyModel", ex.Message);
        Assert.Equal(VisualStudioProject, File.ReadAllText(rnrproj));
    }

    [Fact]
    public void RegisterInProject_keeps_the_compile_shape_of_a_project_that_uses_it()
    {
        var rnrproj = Path.Combine(_dir, "MyProject.rnrproj");
        File.WriteAllText(rnrproj, ProjectXml);

        var reg = RnrProjRegistry.RegisterInProject(rnrproj, "AxClass", "NewClass");

        Assert.Equal(@"AxClass\NewClass.xml", reg.Include);
        Assert.Contains("<Compile Include=\"AxClass\\NewClass.xml\"", File.ReadAllText(rnrproj));
        // ProjectXml keeps its item group on one line: the entry must still land inside that
        // group, beside the existing one, and leave a well-formed project.
        var doc = System.Xml.Linq.XDocument.Load(rnrproj);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var items = doc.Root!.Elements(ns + "ItemGroup").Single().Elements(ns + "Compile")
            .Select(e => (string?)e.Attribute("Include")).ToList();
        Assert.Equal([@"AxTable\Existing.xml", @"AxClass\NewClass.xml"], items);

        Assert.True(RnrProjRegistry.Invert(reg.Delta!));
        var after = System.Xml.Linq.XDocument.Load(rnrproj);
        Assert.Equal(@"AxTable\Existing.xml",
            (string?)after.Descendants(ns + "Compile").Single().Attribute("Include"));
    }

    [Fact]
    public void Undo_of_a_project_registration_leaves_the_project_as_it_was()
    {
        var rnrproj = Path.Combine(_dir, "MyProject.rnrproj");
        File.WriteAllText(rnrproj, VisualStudioProject);

        var reg = RnrProjRegistry.RegisterInProject(rnrproj, "AxClass", "NewClass", "MyModel");
        Assert.True(RnrProjRegistry.Invert(reg.Delta!));

        Assert.Equal(VisualStudioProject, File.ReadAllText(rnrproj));
    }

    [Theory]
    [InlineData("MyProject.csproj", true)]
    [InlineData("Missing.rnrproj", false)]
    public void RegisterInProject_refuses_a_target_that_is_not_an_existing_rnrproj(string fileName, bool create)
    {
        var path = Path.Combine(_dir, fileName);
        if (create) File.WriteAllText(path, VisualStudioProject);

        var ex = Assert.Throws<ArgumentException>(
            () => RnrProjRegistry.RegisterInProject(path, "AxClass", "NewClass", "MyModel"));

        Assert.Contains("not an existing .rnrproj file", ex.Message);
        if (create) Assert.Equal(VisualStudioProject, File.ReadAllText(path));
    }

    [Fact]
    public void RegisterInProject_accepts_the_extension_in_any_case()
    {
        var rnrproj = Path.Combine(_dir, "MyProject.RnrProj");
        File.WriteAllText(rnrproj, VisualStudioProject);

        Assert.True(RnrProjRegistry.RegisterInProject(rnrproj, "AxClass", "NewClass", "MyModel").Added);
    }

    [Fact]
    public void Invert_does_not_touch_a_file_that_is_not_an_rnrproj()
    {
        var other = Path.Combine(_dir, "Other.xml");
        File.WriteAllText(other, ProjectXml);

        Assert.False(RnrProjRegistry.Invert(new RnrProjDelta(other, "Compile", @"AxTable\Existing.xml", WasAdded: true)));
        Assert.Equal(ProjectXml, File.ReadAllText(other));
    }

    [Fact]
    public void RegisterInProject_escapes_markup_in_the_name_and_undo_finds_the_escaped_item()
    {
        var rnrproj = Path.Combine(_dir, "MyProject.rnrproj");
        File.WriteAllText(rnrproj, VisualStudioProject);
        const string name = "A&B<\"C\">";

        var reg = RnrProjRegistry.RegisterInProject(rnrproj, "AxClass", name, "MyModel");

        // Still a well-formed project, and the item reads back as the literal name.
        var doc = System.Xml.Linq.XDocument.Load(rnrproj);
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";
        var item = doc.Descendants(ns + "Content").Single(e => (string?)e.Attribute("Include") == @"AxClass\" + name);
        Assert.Equal(name, item.Element(ns + "Name")?.Value);
        Assert.Equal(@"Classes\" + name, item.Element(ns + "Link")?.Value);
        Assert.Equal(@"AxClass\" + name, reg.Include);

        Assert.True(RnrProjRegistry.Invert(reg.Delta!));
        Assert.Equal(VisualStudioProject, File.ReadAllText(rnrproj));
    }
}
