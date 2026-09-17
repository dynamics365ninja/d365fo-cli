using System.Text.Json;
using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Cover for issue #211: an <c>.rnrproj</c> is compiled with LabelC/xppc because MSBuild cannot
/// run its tasks outside Visual Studio, and the Visual Studio install judged for the build is
/// one that carries the Dynamics 365 dev tools rather than whatever vswhere lists first.
/// </summary>
public class XppModelBuildTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("d365fo-xppbuild-");

    public void Dispose() => root.Delete(recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(root.FullName, relative.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Project(string model) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Project ToolsVersion="14.0" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
          <PropertyGroup><Model>{model}</Model></PropertyGroup>
        </Project>
        """;

    [Fact]
    public void A_solution_yields_the_xpp_projects_it_lists_and_nothing_else()
    {
        Write(@"src\A\A.rnrproj", Project("ModelA"));
        Write(@"src\B\B.rnrproj", Project("ModelB"));
        var sln = Write("Customer.sln",
            """
            Project("{FC65038C-1B2F-41E1-A629-BED71D161FFF}") = "A", "src\A\A.rnrproj", "{1}"
            EndProject
            Project("{FC65038C-1B2F-41E1-A629-BED71D161FFF}") = "B", "src\B\B.rnrproj", "{2}"
            EndProject
            Project("{9A19103F-16F7-4668-BE54-9A1E7A4F7556}") = "Tools", "tools\Tools.csproj", "{3}"
            EndProject
            """);

        var projects = XppModelBuild.ProjectFiles(sln);

        Assert.Equal(
            [Path.Combine(root.FullName, "src", "A", "A.rnrproj"), Path.Combine(root.FullName, "src", "B", "B.rnrproj")],
            projects);
        // A folder with one solution builds that solution, as MSBuild would.
        Assert.Equal(projects, XppModelBuild.ProjectFiles(root.FullName));
    }

    [Fact]
    public void A_non_xpp_project_is_left_to_msbuild()
    {
        var csproj = Write("Tool.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        Assert.Empty(XppModelBuild.ProjectFiles(csproj));
        Assert.Empty(XppModelBuild.ProjectFiles(Path.Combine(root.FullName, "missing.rnrproj")));
    }

    [Fact]
    public void The_model_is_read_from_the_project_and_located_by_its_descriptor()
    {
        var project = Write("P.rnrproj", Project("ContosoCore"));
        Write(@"custom\ContosoPackage\Descriptor\ContosoCore.xml", "<AxModelInfo />");
        Write(@"platform\ApplicationSuite\Descriptor\Foundation.xml", "<AxModelInfo />");

        Assert.Equal("ContosoCore", XppModelBuild.ReadModel(project));

        var target = XppModelBuild.Locate("ContosoCore",
            [Path.Combine(root.FullName, "custom"), Path.Combine(root.FullName, "platform")], project);

        Assert.NotNull(target);
        Assert.Equal("ContosoPackage", target.Module);
        Assert.Equal(Path.Combine(root.FullName, "custom"), target.MetadataRoot);
        Assert.Equal(project, target.Project);
        Assert.Null(XppModelBuild.Locate("NoSuchModel", [Path.Combine(root.FullName, "custom")]));
    }

    [Fact]
    public void Xppc_arguments_follow_what_visual_studio_logs()
    {
        // K:\AosService\PackagesLocalDirectory on a VM; built with the host's separators so the
        // test runs on every CI leg.
        var pld = Path.Combine(root.FullName, "PackagesLocalDirectory");
        var pkg = Path.Combine(pld, "Pkg");
        var target = new XppModelBuild.Target("M", "Pkg", pld, null);

        var args = XppModelBuild.XppcArgs(target, pld, incremental: false);

        Assert.Equal(
        [
            $"-metadata={pld}",
            $"-compilermetadata={pld}",
            "-modelmodule=Pkg",
            $"-output={Path.Combine(pkg, "bin")}",
            $"-referencefolder={pld}",
            $"-refPath={Path.Combine(pkg, "bin")}",
            $"-log={Path.Combine(pkg, "BuildModelResult.log")}",
            $"-xmlLog={Path.Combine(pkg, "BuildModelResult.xml")}",
        ], args);
        Assert.Equal(
            [$"-metadata={pld}", $"-output={Path.Combine(pkg, "Resources")}", "-modelmodule=Pkg"],
            XppModelBuild.LabelcArgs(target).Take(3));
    }

    [Fact]
    public void On_a_ude_layout_both_roots_are_reference_folders()
    {
        var custom = Path.Combine(root.FullName, "UDE", "Custom");
        var framework = Path.Combine(root.FullName, "Dynamics365", "PackagesLocalDirectory");
        var target = new XppModelBuild.Target("M", "Pkg", custom, null);

        var args = XppModelBuild.XppcArgs(target, framework, incremental: true);

        Assert.Contains($"-metadata={custom}", args);
        Assert.Contains($"-compilermetadata={framework}", args);
        Assert.Contains($"-referencefolder={framework}", args);
        Assert.Contains($"-referencefolder={custom}", args);
        Assert.Contains($"-output={Path.Combine(custom, "Pkg", "bin")}", args);
        Assert.Equal("-incremental", args[^1]);
    }

    [Fact]
    public void An_rnrproj_is_planned_for_xppc_and_anything_else_for_msbuild()
    {
        var packages = Path.Combine(root.FullName, "packages");
        Write(@"packages\ContosoPackage\Descriptor\ContosoCore.xml", "<AxModelInfo />");
        var project = Write("P.rnrproj", Project("ContosoCore"));
        var csproj = Write("Tool.csproj", "<Project />");

        var (targets, failure, reason) = SdlcRunner.PlanXppBuild(project, null, packages);
        Assert.Null(failure);
        Assert.Equal("ContosoPackage", Assert.Single(targets).Module);
        Assert.Contains("#211", reason);

        var (none, noFailure, _) = SdlcRunner.PlanXppBuild(csproj, null, packages);
        Assert.Empty(none);
        Assert.Null(noFailure);
    }

    [Fact]
    public void An_unresolvable_xpp_build_fails_with_a_code_instead_of_falling_back_to_msbuild()
    {
        var packages = Path.Combine(root.FullName, "packages");
        Directory.CreateDirectory(packages);
        var noModel = Write("NoModel.rnrproj", "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />");

        var (_, missingModel, _) = SdlcRunner.PlanXppBuild(noModel, null, packages);
        Assert.Equal("PROJECT_HAS_NO_MODEL", Code(missingModel));

        var (_, unknown, _) = SdlcRunner.PlanXppBuild(null, "NoSuchModel", packages);
        Assert.Equal("MODEL_NOT_FOUND", Code(unknown));
    }

    [Fact]
    public void An_unknown_engine_is_rejected() =>
        Assert.Equal("BAD_INPUT", Code(SdlcRunner.Build(null, null, engine: "devenv")));

    private static string? Code(ToolResult<object>? result) =>
        JsonDocument.Parse(JsonSerializer.Serialize(result, D365Json.Options))
            .RootElement.GetProperty("error").GetProperty("code").GetString();

    [Fact]
    public void Vswhere_output_is_read_and_ranked_dev_tools_first_and_ssms_last()
    {
        var vs2022 = Directory.CreateDirectory(Path.Combine(root.FullName, "VS2022")).FullName;
        var ssms = Directory.CreateDirectory(Path.Combine(root.FullName, "SQL Server Management Studio 22")).FullName;
        Directory.CreateDirectory(Path.Combine(vs2022, "Common7", "IDE", "Extensions", "abc"));
        File.WriteAllText(Path.Combine(vs2022, "Common7", "IDE", "Extensions", "abc", BuildTooling.DynamicsBuildTasksAssembly), "");

        var json = JsonSerializer.Serialize(new object[]
        {
            new { installationPath = ssms, installationVersion = "22.0.1", productId = "Microsoft.VisualStudio.Product.Ssms", instanceId = "s" },
            new { installationPath = vs2022, installationVersion = "17.14.0", productId = "Microsoft.VisualStudio.Product.Professional", instanceId = "v" },
            new { installationPath = Path.Combine(root.FullName, "gone"), installationVersion = "18.0", productId = "x", instanceId = "g" },
        });

        var installs = BuildTooling.ParseVswhere(json);

        Assert.Equal(2, installs.Count); // an install whose folder is gone is not one
        var ranked = BuildTooling.RankInstalls(installs).ToList();
        Assert.Equal(vs2022, ranked[0].InstallPath);
        Assert.NotNull(ranked[0].DynamicsTools);
        Assert.True(ranked[1].IsSsms);
        Assert.Empty(BuildTooling.ParseVswhere("not json"));
    }

    [Fact]
    public void Without_dev_tools_anywhere_a_visual_studio_still_ranks_above_ssms()
    {
        var ranked = BuildTooling.RankInstalls(
        [
            new BuildTooling.VisualStudioInstall(@"C:\Program Files\Microsoft SQL Server Management Studio 22\Release", new Version(22, 0), "Microsoft.VisualStudio.Product.Ssms", null),
            new BuildTooling.VisualStudioInstall(@"C:\Program Files\Microsoft Visual Studio\18\Professional", new Version(18, 0), "Microsoft.VisualStudio.Product.Professional", null),
        ]).ToList();

        Assert.False(ranked[0].IsSsms);
    }

    [Theory]
    [InlineData("2019", 16)]
    [InlineData("2022", 17)]
    [InlineData("18", 18)]
    public void Install_folder_names_map_to_major_versions(string folder, int major) =>
        Assert.Equal(major, BuildTooling.FolderVersion(folder)!.Major);
}
