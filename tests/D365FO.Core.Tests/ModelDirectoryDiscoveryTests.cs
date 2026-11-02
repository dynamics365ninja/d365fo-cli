using D365FO.Core.Index;
using D365FO.Core.ObjectTypes;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Model-folder discovery lists each candidate folder once instead of probing it for every
/// marker folder. On a real packages root the probing cost about 100,000 file-system calls per
/// walk, which every <c>index status</c>, <c>doctor</c> and <c>index sync</c> paid. These tests pin
/// that the cheaper check still answers the same question.
/// </summary>
public sealed class ModelDirectoryDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "d365fo-modeldirs-" + Guid.NewGuid().ToString("N"));

    public ModelDirectoryDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Package_subfolders_without_a_marker_folder_are_not_models()
    {
        Dir("PkgA", "bin");
        Dir("PkgA", "Descriptor");

        Assert.False(ObjectTypeRegistry.IsModelDirectory(Path.Combine(_root, "PkgA", "bin")));
        Assert.False(ObjectTypeRegistry.IsModelDirectory(Path.Combine(_root, "PkgA", "Descriptor")));
        Assert.Empty(IndexSync.EnumerateModelDirs(_root, onlyModel: null));
    }

    [Fact]
    public void A_folder_with_any_marker_folder_is_a_model_whatever_the_marker()
    {
        foreach (var marker in ObjectTypeRegistry.ModelMarkerFolders())
        {
            var model = Dir("Pkg_" + marker, "Model_" + marker, marker);
            Assert.True(ObjectTypeRegistry.IsModelDirectory(Path.GetDirectoryName(model)!), marker);
        }

        Assert.Equal(ObjectTypeRegistry.ModelMarkerFolders().Length,
            IndexSync.EnumerateModelDirs(_root, onlyModel: null).Count());
    }

    [Fact]
    public void Marker_folder_name_follows_the_file_system_case_rule_as_the_old_probe_did()
    {
        Dir("Pkg", "Model", "axclass");

        // The old Directory.Exists probe was case-insensitive on Windows/macOS and exact on Linux,
        // where the extractor then reads AxClass by its exact name and would find nothing.
        Assert.Equal(!OperatingSystem.IsLinux(),
            ObjectTypeRegistry.IsModelDirectory(Path.Combine(_root, "Pkg", "Model")));
    }

    [Fact]
    public void A_folder_that_cannot_be_listed_but_can_be_traversed_is_still_probed()
    {
        if (OperatingSystem.IsWindows()) return; // needs Unix modes; the probe fallback is OS-agnostic
        var model = Path.Combine(_root, "Pkg", "Model");
        Dir("Pkg", "Model", "AxClass");
        File.SetUnixFileMode(model, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Assert.True(ObjectTypeRegistry.IsModelDirectory(model));
        }
        finally
        {
            File.SetUnixFileMode(model, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void One_model_lookup_returns_only_that_model()
    {
        Dir("Pkg", "Wanted", "AxClass");
        Dir("Pkg", "Other", "AxTable");

        var found = IndexSync.EnumerateModelDirs(_root, onlyModel: "wanted").ToList();

        Assert.Single(found);
        Assert.Equal("Wanted", Path.GetFileName(found[0]));
    }

    [Fact]
    public void A_folder_that_does_not_exist_is_not_a_model_and_does_not_throw()
    {
        Assert.False(ObjectTypeRegistry.IsModelDirectory(Path.Combine(_root, "missing")));
    }
}
