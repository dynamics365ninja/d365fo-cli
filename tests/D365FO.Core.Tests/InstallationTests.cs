using D365FO.Core.Ops;
using Xunit;

namespace D365FO.Core.Tests;

public sealed class InstallationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("d365fo-install-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Folder(params string[] parts) =>
        Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static void Touch(string folder, string name) => File.WriteAllText(Path.Combine(folder, name), "");

    [Fact]
    public void Classifies_an_installation_by_where_its_binaries_run()
    {
        var npm = Folder("prefix", "node_modules", "d365fo-cli", "node_modules", "@dynamics365ninja", "d365fo-cli-win32-x64", "bin");
        var unscoped = Folder("other", "node_modules", "d365fo-cli", "node_modules", "d365fo-cli-win32-x64", "bin");
        var installer = Folder("local", "d365fo-cli", "bin");
        var archive = Folder("tools", "d365fo-cli-0.3.0", "bin");
        var source = Folder("checkout", "src", "bin", "Release");
        Touch(Folder("checkout"), "d365fo-cli.slnx");
        var local = Folder("local");

        Assert.Equal(InstallKind.Npm, Installation.Detect(npm + Path.DirectorySeparatorChar, local));
        // Only the scoped platform packages are this project's: an unscoped namesake is someone else's.
        Assert.Equal(InstallKind.Archive, Installation.Detect(unscoped, local));
        Assert.Equal(InstallKind.Installer, Installation.Detect(installer + Path.DirectorySeparatorChar, local));
        Assert.Equal(InstallKind.Archive, Installation.Detect(archive, local));
        Assert.Equal(InstallKind.Source, Installation.Detect(source, local));
    }

    [Fact]
    public void Lists_every_command_on_the_path_in_search_order()
    {
        var windows = OperatingSystem.IsWindows();
        var binary = windows ? "d365fo.exe" : "d365fo";
        var first = Folder("first");
        var second = Folder("second");
        Touch(first, binary);
        Touch(second, binary);
        var path = string.Join(Path.PathSeparator, first, Folder("empty"), second, first + Path.DirectorySeparatorChar);

        var found = Installation.FindOnPath(path, windows);

        Assert.Equal([first, second], found.Select(c => c.Home));
        Assert.True(Installation.Starts(found[0], first + Path.DirectorySeparatorChar));
        Assert.False(Installation.Starts(found[1], first));
    }

    [Fact]
    public void A_windows_npm_shim_starts_the_binaries_below_its_prefix()
    {
        var prefix = Folder("npm");
        Touch(prefix, "d365fo.cmd");
        var binaries = Folder("npm", "node_modules", "d365fo-cli", "node_modules", "@dynamics365ninja", "d365fo-cli-win32-x64", "bin");

        var shim = Assert.Single(Installation.FindOnPath(prefix, windows: true));

        Assert.True(shim.Npm);
        Assert.True(Installation.Starts(shim, binaries));
        Assert.False(Installation.Starts(shim, Folder("elsewhere")));
    }

    [Fact]
    public void A_missing_path_finds_nothing() => Assert.Empty(Installation.FindOnPath(null, windows: true));
}
