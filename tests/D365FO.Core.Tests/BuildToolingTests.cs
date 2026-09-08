using System.Text.Json;
using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Cover for the build-tooling probe added for issue #207: a build that fails because the
/// environment cannot host the X++ MSBuild tasks used to look exactly like a compiler error.
/// </summary>
public class BuildToolingTests
{
    [Theory]
    [InlineData(@"C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe", true)]
    [InlineData(@"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe", true)]
    [InlineData(@"C:/Windows/Microsoft.NET/Framework/v4.0.30319/MSBuild.exe", true)]
    [InlineData(@"C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe", false)]
    public void Framework_msbuild_is_recognised_by_its_path(string path, bool expected) =>
        Assert.Equal(expected, BuildTooling.IsFrameworkMsBuild(path));

    [Fact]
    public void Null_path_is_not_a_framework_msbuild() =>
        Assert.False(BuildTooling.IsFrameworkMsBuild(null));

    [Fact]
    public void An_explicit_msbuild_path_wins_and_is_reported_as_such()
    {
        var dir = Directory.CreateTempSubdirectory("d365fo-msbuild-");
        try
        {
            var exe = Path.Combine(dir.FullName, "MSBuild.exe");
            File.WriteAllText(exe, string.Empty);

            var resolved = BuildTooling.ResolveMsBuild(exe);

            Assert.True(resolved.Ok);
            Assert.Equal(exe, resolved.Path);
            Assert.Equal("--msbuild", resolved.Source);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void An_explicit_path_that_does_not_exist_is_reported_rather_than_silently_ignored()
    {
        var missing = Path.Combine(Path.GetTempPath(), "d365fo-no-such-msbuild", "MSBuild.exe");

        var resolved = BuildTooling.ResolveMsBuild(missing);

        Assert.False(resolved.Ok);
        Assert.Equal(missing, resolved.Path);
        Assert.Contains("Not found", resolved.Problem);
    }

    [Fact]
    public void A_framework_msbuild_is_returned_with_the_reason_it_cannot_build_xpp()
    {
        // The path is what matters, not the file: an absent one already fails earlier.
        var dir = Directory.CreateTempSubdirectory("Microsoft.NET-");
        try
        {
            var nested = Path.Combine(dir.FullName, "Microsoft.NET", "Framework", "v4.0.30319");
            Directory.CreateDirectory(nested);
            var exe = Path.Combine(nested, "MSBuild.exe");
            File.WriteAllText(exe, string.Empty);

            var resolved = BuildTooling.ResolveMsBuild(exe);

            Assert.False(resolved.Ok);
            Assert.Equal(exe, resolved.Path);
            Assert.Contains("MSB4062", resolved.Problem);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Package_bin_tools_are_probed_under_the_packages_root()
    {
        var dir = Directory.CreateTempSubdirectory("d365fo-packages-");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir.FullName, "bin"));
            var xppc = Path.Combine(dir.FullName, "bin", "xppc.exe");
            File.WriteAllText(xppc, string.Empty);

            var found = BuildTooling.ResolvePackageBinTool("xppc.exe", dir.FullName);
            Assert.True(found.Ok);
            Assert.Equal(xppc, found.Path);

            var missing = BuildTooling.ResolvePackageBinTool("SyncEngine.exe", dir.FullName);
            Assert.False(missing.Ok);
            Assert.Contains("SyncEngine.exe", missing.Problem);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Without_a_packages_path_the_probe_says_so_instead_of_throwing()
    {
        var resolved = BuildTooling.ResolvePackageBinTool("xppbp.exe", null);

        Assert.False(resolved.Ok);
        Assert.Contains("D365FO_PACKAGES_PATH", resolved.Problem);
    }

    [Fact]
    public void A_toolchain_failure_without_a_file_position_is_still_reported_as_an_error()
    {
        // "MSBUILD : error MSB1009: …" carries no (line,col), so the positional parser
        // dropped it and the build reported zero errors for a build that never started.
        var output = string.Join('\n',
            "Microsoft (R) Build Engine version 17.0",
            "MSBUILD : error MSB1009: Project file does not exist.",
            "Switch: MyModel.rnrproj");

        var errors = SdlcRunner.ParseMsBuildDiagnostics(output, "error");

        var json = JsonSerializer.Serialize(errors);
        Assert.Single(errors);
        Assert.Contains("MSB1009", json);
        Assert.Contains("ENV-PROJECT-NOT-FOUND", json);
    }

    [Fact]
    public void The_same_error_reported_twice_is_listed_once()
    {
        var output = string.Join('\n',
            @"C:\Proj\MyModel.rnrproj(4,5): error MSB4062: The ""BuildTask"" task could not be loaded from the assembly.",
            @"MSBUILD : error MSB4062: The ""BuildTask"" task could not be loaded from the assembly.");

        var errors = SdlcRunner.ParseMsBuildDiagnostics(output, "error");

        var single = Assert.Single(errors);
        Assert.Contains("ENV-MSBUILD-TASK-MISSING", JsonSerializer.Serialize(single));
    }
}
