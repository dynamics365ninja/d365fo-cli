// <copyright file="BuildTooling.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Diagnostics;

namespace D365FO.Core.Ops;

/// <summary>One resolved developer tool: where it is, how it was found, what is wrong with it.</summary>
/// <param name="Name">Display name of the tool (<c>msbuild</c>, <c>xppc.exe</c>, …).</param>
/// <param name="Path">Absolute path when found, otherwise <c>null</c>.</param>
/// <param name="Source">How the path was arrived at — <c>--msbuild</c>, <c>D365FO_MSBUILD_PATH</c>, <c>vswhere</c>, <c>PATH</c>, <c>packages\bin</c>.</param>
/// <param name="Problem">
/// Non-null when the path is unusable for X++ (missing, or an MSBuild that cannot host the
/// Dynamics build tasks). A path plus a problem is still returned so the caller can say
/// <i>which</i> wrong tool it found rather than only that something is missing.
/// </param>
public sealed record ToolPath(string Name, string? Path, string Source, string? Problem = null)
{
    /// <summary>True when the tool was found and nothing is wrong with it.</summary>
    public bool Ok => Path is not null && Problem is null;
}

/// <summary>
/// Where the Windows-only D365FO build tools actually are, and whether the MSBuild that
/// <c>d365fo build</c> would run can build X++ at all.
/// </summary>
/// <remarks>
/// <para>
/// <c>SdlcRunner.Build</c> used to shell out to a bare <c>msbuild.exe</c> and let PATH decide.
/// On a D365FO developer VM PATH resolves that to
/// <c>C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe</c> — the .NET Framework MSBuild,
/// which cannot load <c>Microsoft.Dynamics.Framework.Tools.BuildTasks</c>. The build then fails
/// with <c>MSB4062</c> ("task could not be loaded from the assembly"), which reads like a
/// compiler error and is not one. That is the report behind issue #207: the environment is
/// broken in a way nothing in the CLI could see, so the agent had to guess.
/// </para>
/// <para>
/// The X++ project (<c>.rnrproj</c>) imports
/// <c>$(BuildTasksDirectory)\Microsoft.Dynamics.Framework.Tools.BuildTasks[.17.0].targets</c>,
/// defaulting <c>BuildTasksDirectory</c> to <c>%ProgramFiles(x86)%\MSBuild\Microsoft\Dynamics\AX</c>,
/// and that targets file declares its tasks by <c>AssemblyName</c> — so MSBuild has to resolve
/// the build-task assembly itself, which only the Visual Studio install carrying the
/// Dynamics 365 dev tools extension can do. Both halves are probed here, separately, because
/// they fail separately.
/// </para>
/// </remarks>
public static class BuildTooling
{
    /// <summary>Override for the MSBuild executable (env var or settings.json).</summary>
    public const string MsBuildEnvVar = "D365FO_MSBUILD_PATH";

    /// <summary>The two names the Dynamics build targets ship under, newest first.</summary>
    public static readonly string[] DynamicsTargetsFileNames =
    [
        "Microsoft.Dynamics.Framework.Tools.BuildTasks.17.0.targets",
        "Microsoft.Dynamics.Framework.Tools.BuildTasks.targets",
    ];

    /// <summary>The tools <c>sync</c>, <c>test run</c> and <c>bp check</c> invoke, under <c>&lt;packages&gt;\bin</c>.</summary>
    public static readonly string[] PackageBinTools =
    [
        "xppc.exe",
        "xppbp.exe",
        "SyncEngine.exe",
    ];

    /// <summary>
    /// The .NET Framework MSBuild ships with Windows and is always on PATH; it is not a
    /// Visual Studio MSBuild and cannot host the Dynamics build tasks.
    /// </summary>
    public static bool IsFrameworkMsBuild(string? path) =>
        path is not null &&
        path.Replace('/', '\\').Contains(@"\Microsoft.NET\Framework", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The MSBuild <c>d365fo build</c> will run: <c>--msbuild</c>, then
    /// <see cref="MsBuildEnvVar"/>, then the newest Visual Studio install (via <c>vswhere</c>,
    /// falling back to the well-known install roots), then PATH.
    /// </summary>
    public static ToolPath ResolveMsBuild(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Judge("msbuild", explicitPath!, "--msbuild");

        var configured = D365FoSettings.Resolve(MsBuildEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return Judge("msbuild", configured!, MsBuildEnvVar);

        var vs = VisualStudioMsBuild();
        if (vs is not null) return Judge("msbuild", vs, "vswhere");

        var onPath = FindOnPath("msbuild.exe");
        if (onPath is not null) return Judge("msbuild", onPath, "PATH");

        return new ToolPath("msbuild", null, "PATH",
            "msbuild.exe not found. Install the Visual Studio workload with the Dynamics 365 dev tools, or set D365FO_MSBUILD_PATH.");
    }

    /// <summary>
    /// The directory an <c>.rnrproj</c> imports its targets from — <c>BuildTasksDirectory</c>,
    /// which the project defaults to <c>%ProgramFiles(x86)%\MSBuild\Microsoft\Dynamics\AX</c>.
    /// Missing means the Dynamics 365 dev tools were never installed on this host.
    /// </summary>
    public static ToolPath ResolveDynamicsTargets()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "MSBuild", "Microsoft", "Dynamics", "AX");

        foreach (var name in DynamicsTargetsFileNames)
        {
            var candidate = Path.Combine(root, name);
            if (File.Exists(candidate))
                return new ToolPath("build targets", candidate, "BuildTasksDirectory default");
        }

        return new ToolPath("build targets", null, "BuildTasksDirectory default",
            $"No Microsoft.Dynamics.Framework.Tools.BuildTasks*.targets under {root} — the Dynamics 365 dev tools are not installed for MSBuild on this host. Building an .rnrproj will fail with MSB4019/MSB4062.");
    }

    /// <summary>A tool under <c>&lt;packagesPath&gt;\bin</c> (<c>xppc.exe</c>, <c>xppbp.exe</c>, <c>SyncEngine.exe</c>).</summary>
    public static ToolPath ResolvePackageBinTool(string exeName, string? packagesPath)
    {
        if (string.IsNullOrWhiteSpace(packagesPath))
            return new ToolPath(exeName, null, @"packages\bin",
                "D365FO_PACKAGES_PATH is not set, so <packages>\\bin cannot be probed.");

        var candidate = Path.Combine(packagesPath!, "bin", exeName);
        return File.Exists(candidate)
            ? new ToolPath(exeName, candidate, @"packages\bin")
            : new ToolPath(exeName, null, @"packages\bin", $"Not found at {candidate}.");
    }

    private static ToolPath Judge(string name, string path, string source)
    {
        if (!File.Exists(path))
            return new ToolPath(name, path, source, $"Not found: {path}");

        return IsFrameworkMsBuild(path)
            ? new ToolPath(name, path, source,
                "This is the .NET Framework MSBuild, which cannot load Microsoft.Dynamics.Framework.Tools.BuildTasks — an X++ build against it fails with MSB4062. Use the MSBuild from the Visual Studio install that has the Dynamics 365 dev tools extension.")
            : new ToolPath(name, path, source);
    }

    /// <summary>
    /// The newest Visual Studio MSBuild, asked of <c>vswhere</c> (which ships with every VS
    /// installer since 2017) and, when that is unavailable, found by walking the known install
    /// roots. Returns <c>null</c> off Windows or when no install is present.
    /// </summary>
    private static string? VisualStudioMsBuild()
    {
        if (!OperatingSystem.IsWindows()) return null;

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (File.Exists(vswhere))
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = vswhere,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                foreach (var a in new[] { "-latest", "-prerelease", "-products", "*", "-find", @"MSBuild\**\Bin\MSBuild.exe" })
                    psi.ArgumentList.Add(a);

                using var p = Process.Start(psi);
                if (p is not null)
                {
                    var stdout = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(15_000);
                    var hit = stdout
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault(File.Exists);
                    if (hit is not null) return hit;
                }
            }
            catch
            {
                // vswhere is a convenience, never a requirement — fall through to the roots below.
            }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            var vsRoot = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vsRoot)) continue;

            // <root>\<version>\<edition>\MSBuild\Current\Bin\MSBuild.exe — newest version first.
            foreach (var version in Directory.EnumerateDirectories(vsRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                foreach (var edition in SafeDirectories(version))
                {
                    var candidate = Path.Combine(edition, "MSBuild", "Current", "Bin", "MSBuild.exe");
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try { return Directory.EnumerateDirectories(path); }
        catch { return []; }
    }

    /// <summary>First match for <paramref name="exeName"/> on PATH, or <c>null</c>.</summary>
    internal static string? FindOnPath(string exeName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir, exeName); }
            catch { continue; } // an unparseable PATH entry is not a reason to fail resolution
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
