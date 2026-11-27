// <copyright file="Installation.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

namespace D365FO.Core.Ops;

/// <summary>How the running <c>d365fo</c> got onto the machine, which decides how it is updated.</summary>
public enum InstallKind
{
    /// <summary>The binaries of an npm platform package.</summary>
    Npm,

    /// <summary>Published by <c>install.ps1</c> / <c>install.sh</c> from a checkout.</summary>
    Installer,

    /// <summary>Built and run inside a source checkout.</summary>
    Source,

    /// <summary>Anything else: normally an extracted release archive.</summary>
    Archive,
}

/// <summary>A <c>d365fo</c> command found on <c>PATH</c>.</summary>
/// <param name="Command">The file a shell would run.</param>
/// <param name="Npm">True for an npm shim, which starts the binaries of its own package.</param>
/// <param name="Home">The npm prefix of a shim, otherwise the folder of the binary.</param>
public sealed record PathCommand(string Command, bool Npm, string Home);

/// <summary>Where the running build lives, and which other builds a shell could pick instead.</summary>
public static class Installation
{
    /// <summary>The npm scope the launcher and the platform packages are published under.</summary>
    public const string NpmScope = "@dynamics365ninja";

    /// <summary>The npm package a user installs: <c>npm install -g @dynamics365ninja/d365fo-cli</c>.</summary>
    public const string NpmPackage = NpmScope + "/d365fo-cli";

    /// <summary>Classify an installation by the folder its binaries run from.</summary>
    public static InstallKind Detect(string baseDirectory, string? localApplicationData = null)
    {
        var directory = Trim(baseDirectory);
        var parts = directory.Split('/', '\\');

        // node_modules/@dynamics365ninja/d365fo-cli-<platform>, the package that holds the binaries.
        for (var i = 0; i + 2 < parts.Length; i++)
        {
            if (parts[i] == "node_modules" && parts[i + 1] == NpmScope
                && parts[i + 2].StartsWith("d365fo-cli", StringComparison.Ordinal))
                return InstallKind.Npm;
        }

        // install.ps1 publishes to %LOCALAPPDATA%\d365fo-cli\bin, install.sh to
        // ~/.local/share/d365fo-cli/bin: both are this machine's local application data.
        localApplicationData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApplicationData)
            && SamePath(directory, Path.Combine(localApplicationData, "d365fo-cli", "bin")))
            return InstallKind.Installer;

        for (var folder = new DirectoryInfo(directory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "d365fo-cli.slnx"))) return InstallKind.Source;
        }

        return InstallKind.Archive;
    }

    /// <summary>
    /// Every <c>d365fo</c> on a <c>PATH</c> value, in the order a shell searches it, one per folder.
    /// </summary>
    public static IReadOnlyList<PathCommand> FindOnPath(string? path, bool windows)
    {
        var found = new List<PathCommand>();
        var seen = new HashSet<string>(windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var names = windows ? new[] { "d365fo.exe", "d365fo.cmd" } : ["d365fo"];

        foreach (var entry in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = Trim(entry.Trim().Trim('"'));
            if (directory.Length == 0 || !seen.Add(directory)) continue;

            var command = names.Select(n => Path.Combine(directory, n)).FirstOrDefault(File.Exists);
            if (command is null) continue;

            found.Add(Classify(command, directory, windows));
        }

        return found;
    }

    /// <summary>True when running <paramref name="command"/> starts the build in <paramref name="baseDirectory"/>.</summary>
    public static bool Starts(PathCommand command, string baseDirectory)
    {
        if (!command.Npm) return SamePath(command.Home, Trim(baseDirectory));

        // A global prefix holds node_modules itself on Windows and under lib/ elsewhere.
        var directory = Trim(baseDirectory).Replace('\\', '/') + "/";
        var home = command.Home.Replace('\\', '/');
        return directory.StartsWith($"{home}/node_modules/", PathComparison)
               || directory.StartsWith($"{home}/lib/node_modules/", PathComparison);
    }

    private static PathCommand Classify(string command, string directory, bool windows)
    {
        if (windows)
        {
            var shim = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                       && Directory.Exists(Path.Combine(directory, "node_modules", NpmScope, "d365fo-cli"));
            return new PathCommand(command, shim, directory);
        }

        // Elsewhere both npm and a hand-made install link into bin: follow the link.
        var target = File.ResolveLinkTarget(command, returnFinalTarget: true)?.FullName;
        if (target is null) return new PathCommand(command, false, directory);

        const string npmPackage = "/lib/node_modules/" + NpmPackage + "/";
        var at = target.IndexOf(npmPackage, StringComparison.Ordinal);
        return at >= 0
            ? new PathCommand(command, true, target[..at])
            : new PathCommand(command, false, Trim(Path.GetDirectoryName(target)!));
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool SamePath(string first, string second) =>
        string.Equals(Trim(first), Trim(second), PathComparison);

    private static string Trim(string path) => Path.TrimEndingDirectorySeparator(path);
}
