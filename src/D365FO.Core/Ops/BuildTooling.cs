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
/// <para>
/// Neither half makes an <c>.rnrproj</c> buildable from the command line: the tasks also need
/// metadata services that only the Visual Studio package provides (issue #211), so
/// <c>d365fo build</c> compiles X++ projects with <see cref="XppModelBuild"/> and keeps MSBuild
/// for everything else. What is probed here still decides whether that MSBuild route can work,
/// and which install a developer would open the project in.
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

    /// <summary>The tools <c>build</c>, <c>sync</c> and <c>bp check</c> invoke, under <c>&lt;packages&gt;\bin</c>.</summary>
    public static readonly string[] PackageBinTools =
    [
        "xppc.exe",
        "LabelC.exe",
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

        if (IsFrameworkMsBuild(path))
            return new ToolPath(name, path, source,
                "This is the .NET Framework MSBuild, which cannot load Microsoft.Dynamics.Framework.Tools.BuildTasks — an X++ build against it fails with MSB4062. Use the MSBuild from the Visual Studio install that has the Dynamics 365 dev tools extension.");

        var owner = OwningInstall(path);
        if (owner is { IsSsms: true })
            return new ToolPath(name, path, source,
                "This is SQL Server Management Studio's MSBuild, not a Visual Studio one. Point D365FO_MSBUILD_PATH (or --msbuild) at the Visual Studio install that has the Dynamics 365 dev tools.");
        if (owner is { DynamicsTools: null })
            return new ToolPath(name, path, source,
                $"The install at {owner.InstallPath} has no Dynamics 365 dev tools extension ({DynamicsBuildTasksAssembly} not found), so it cannot run the X++ build tasks.");

        return new ToolPath(name, path, source);
    }

    private static VisualStudioInstall? OwningInstall(string path)
    {
        var full = Path.GetFullPath(path);
        return VisualStudioInstalls().FirstOrDefault(i =>
            full.StartsWith(Path.GetFullPath(i.InstallPath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The build-task assembly the <c>.17.0</c> targets name, shipped inside the dev tools extension.</summary>
    public const string DynamicsBuildTasksAssembly = "Microsoft.Dynamics.Framework.Tools.BuildTasks.17.0.dll";

    /// <summary>One Visual Studio-family install: where it is and whether it carries the Dynamics 365 dev tools.</summary>
    /// <param name="InstallPath">Installation root (holds <c>Common7</c> and <c>MSBuild</c>).</param>
    /// <param name="Version">Installation version, for ordering; <c>null</c> when only the folder is known.</param>
    /// <param name="ProductId">vswhere's product id — SQL Server Management Studio registers as one too.</param>
    /// <param name="DynamicsTools">Folder of the Dynamics 365 dev tools extension, or <c>null</c>.</param>
    public sealed record VisualStudioInstall(string InstallPath, Version? Version, string? ProductId, string? DynamicsTools)
    {
        /// <summary><c>&lt;install&gt;\MSBuild\Current\Bin\MSBuild.exe</c>, when present.</summary>
        public string? MsBuild
        {
            get
            {
                var p = Path.Combine(InstallPath, "MSBuild", "Current", "Bin", "MSBuild.exe");
                return File.Exists(p) ? p : null;
            }
        }

        /// <summary>SQL Server Management Studio ships an MSBuild of its own that has nothing to do with X++.</summary>
        public bool IsSsms =>
            (ProductId?.Contains("Ssms", StringComparison.OrdinalIgnoreCase) ?? false) ||
            InstallPath.Contains("SQL Server Management Studio", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The newest Visual Studio MSBuild, preferring an install that carries the Dynamics 365 dev
    /// tools and never choosing SQL Server Management Studio's over a Visual Studio one — vswhere
    /// lists SSMS as a product, and "the latest" was sometimes SSMS (issue #211).
    /// </summary>
    private static string? VisualStudioMsBuild() =>
        RankInstalls(VisualStudioInstalls()).Select(i => i.MsBuild).FirstOrDefault(p => p is not null);

    /// <summary>Best candidate first: has the dev tools, is not SSMS, is newest.</summary>
    public static IEnumerable<VisualStudioInstall> RankInstalls(IEnumerable<VisualStudioInstall> installs) =>
        installs
            .OrderByDescending(i => i.DynamicsTools is not null)
            .ThenBy(i => i.IsSsms)
            .ThenByDescending(i => i.Version ?? new Version(0, 0));

    /// <summary>
    /// The install that <c>d365fo doctor</c> should judge the D365FO build tooling by: the one the
    /// resolved MSBuild belongs to, or the best-ranked one.
    /// </summary>
    public static VisualStudioInstall? InstallFor(string? msbuildPath) =>
        (msbuildPath is null ? null : OwningInstall(msbuildPath)) ?? RankInstalls(VisualStudioInstalls()).FirstOrDefault();

    /// <summary>
    /// Every install vswhere reports (which ships with every VS installer since 2017) and, when
    /// that is unavailable, those found by walking the known install roots. Empty off Windows.
    /// </summary>
    public static IReadOnlyList<VisualStudioInstall> VisualStudioInstalls() => Installs.Value;

    // Installs do not change while the process runs; doctor asks several times.
    private static readonly Lazy<IReadOnlyList<VisualStudioInstall>> Installs = new(DiscoverInstalls);

    private static IReadOnlyList<VisualStudioInstall> DiscoverInstalls()
    {
        if (!OperatingSystem.IsWindows()) return [];

        var fromVswhere = QueryVswhere();
        if (fromVswhere.Count > 0) return fromVswhere;

        var found = new List<VisualStudioInstall>();
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            var vsRoot = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vsRoot)) continue;

            // <root>\<version>\<edition>\ — the version folder is "2022" or "18".
            foreach (var version in SafeDirectories(vsRoot))
            {
                foreach (var edition in SafeDirectories(version))
                {
                    if (!Directory.Exists(Path.Combine(edition, "MSBuild"))) continue;
                    var v = FolderVersion(Path.GetFileName(version));
                    found.Add(new VisualStudioInstall(edition, v, null, FindDynamicsTools(edition, null, v)));
                }
            }
        }
        return found;
    }

    /// <summary>The version folder is the marketing year up to 2022 ("2022" is 17) and the major version since ("18").</summary>
    internal static Version? FolderVersion(string folder) => folder switch
    {
        "2017" => new Version(15, 0),
        "2019" => new Version(16, 0),
        "2022" => new Version(17, 0),
        _ => int.TryParse(folder, out var major) && major is > 17 and < 100 ? new Version(major, 0) : null,
    };

    private static IReadOnlyList<VisualStudioInstall> QueryVswhere()
    {
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return [];

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
            foreach (var a in new[] { "-all", "-prerelease", "-products", "*", "-format", "json", "-utf8" })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return [];
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15_000);
            return ParseVswhere(stdout);
        }
        catch
        {
            // vswhere is a convenience, never a requirement — the caller walks the roots instead.
            return [];
        }
    }

    /// <summary>Read <c>vswhere -format json</c> output.</summary>
    internal static IReadOnlyList<VisualStudioInstall> ParseVswhere(string json)
    {
        var list = new List<VisualStudioInstall>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var path = Str(e, "installationPath");
                if (path is null || !Directory.Exists(path)) continue;
                var version = System.Version.TryParse(Str(e, "installationVersion"), out var v) ? v : null;
                list.Add(new VisualStudioInstall(path, version, Str(e, "productId"),
                    FindDynamicsTools(path, Str(e, "instanceId"), version)));
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable output is the same as no vswhere.
        }
        return list;

        static string? Str(System.Text.Json.JsonElement e, string name) =>
            e.TryGetProperty(name, out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String ? p.GetString() : null;
    }

    /// <summary>
    /// The Dynamics 365 dev tools extension folder of an install: the one holding
    /// <see cref="DynamicsBuildTasksAssembly"/>, installed per machine under
    /// <c>Common7\IDE\Extensions\&lt;random&gt;\</c> or per user under
    /// <c>%LOCALAPPDATA%\Microsoft\VisualStudio\&lt;major&gt;.0_&lt;instanceId&gt;\Extensions\&lt;random&gt;\</c>.
    /// </summary>
    internal static string? FindDynamicsTools(string installPath, string? instanceId, Version? version)
    {
        var roots = new List<string> { Path.Combine(installPath, "Common7", "IDE", "Extensions") };
        if (instanceId is not null && version is not null)
        {
            roots.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "VisualStudio", $"{version.Major}.0_{instanceId}", "Extensions"));
        }

        foreach (var root in roots)
        {
            foreach (var dir in SafeDirectories(root))
            {
                if (File.Exists(Path.Combine(dir, DynamicsBuildTasksAssembly))) return dir;
            }
        }
        return null;
    }

    private static string[] SafeDirectories(string path)
    {
        try { return Directory.Exists(path) ? Directory.GetDirectories(path) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
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
