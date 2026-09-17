// <copyright file="XppModelBuild.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core.Validation;

namespace D365FO.Core.Ops;

/// <summary>
/// Builds X++ models the way Visual Studio's "Build models" does — <c>LabelC.exe</c>, then
/// <c>xppc.exe</c> — which is how <c>d365fo build</c> builds an <c>.rnrproj</c>.
/// </summary>
/// <remarks>
/// <para>
/// Issue #211. An <c>.rnrproj</c> cannot be built by a standalone MSBuild, whichever one is
/// picked. Its targets declare <c>CopyReferencesTask</c> and <c>BuildTask</c> by strong name,
/// and the assembly and its dependencies (<c>Microsoft.VisualStudio.Shell.15.0</c>,
/// <c>Microsoft.VisualStudio.Interop</c>, …) are only resolvable inside <c>devenv</c> — hence
/// <c>MSB4062</c> from every command-line MSBuild, Visual Studio's own included. Supplying that
/// probing context gets the task loaded, and then <c>BuildTask.ValidateAndGetModel</c> throws
/// a <c>NullReferenceException</c>: it takes its metadata services from
/// <c>AxServiceProvider</c>, which only the Visual Studio package populates. Checked on a
/// D365FO VM against VS 2022 and VS 2026 (x86 and amd64 MSBuild); the task assembly has no
/// code path that initialises those services outside the IDE.
/// </para>
/// <para>
/// The compilers underneath are standalone. The argument lists here are the ones Visual Studio
/// logs for its own builds (<c>CompileLabels.xml</c>, <c>BuildModelResult.*</c> in the package
/// folder), and write to the same places, so a CLI build leaves the package exactly as an IDE
/// build of the model would.
/// </para>
/// </remarks>
public static class XppModelBuild
{
    /// <summary>One model to build, located on disk.</summary>
    /// <param name="Model">Model name (the <c>&lt;Model&gt;</c> of an <c>.rnrproj</c>).</param>
    /// <param name="Module">Package (module) that holds the model — what the compilers take.</param>
    /// <param name="MetadataRoot">Root the package folder sits under.</param>
    /// <param name="Project">The <c>.rnrproj</c> it came from, when it came from one.</param>
    public sealed record Target(string Model, string Module, string MetadataRoot, string? Project);

    private static readonly Regex SolutionProjectRx = new(
        @"""(?<path>[^""]+\.rnrproj)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// The <c>.rnrproj</c> files behind <paramref name="path"/>: the file itself, the X++
    /// projects a <c>.sln</c>/<c>.slnx</c> lists, or those in a directory. Empty means the path
    /// holds no X++ project — MSBuild's business, not this class's.
    /// </summary>
    public static IReadOnlyList<string> ProjectFiles(string path)
    {
        if (Directory.Exists(path))
        {
            var solutions = Directory.GetFiles(path, "*.sln").Concat(Directory.GetFiles(path, "*.slnx")).ToList();
            // MSBuild's own rule: a folder builds its one project or solution file.
            if (solutions.Count == 1) return ProjectFiles(solutions[0]);
            if (solutions.Count > 1) return [];
            return Directory.GetFiles(path, "*.rnrproj");
        }

        if (!File.Exists(path)) return [];

        var ext = Path.GetExtension(path);
        if (ext.Equals(".rnrproj", StringComparison.OrdinalIgnoreCase)) return [Path.GetFullPath(path)];
        if (!ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)) return [];

        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        return SolutionProjectRx.Matches(File.ReadAllText(path))
            .Select(m => Path.GetFullPath(Path.Combine(dir, m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The <c>&lt;Model&gt;</c> an <c>.rnrproj</c> builds, or <c>null</c> when it names none.</summary>
    public static string? ReadModel(string rnrproj)
    {
        try
        {
            var model = XDocument.Load(rnrproj).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Model")?.Value.Trim();
            return string.IsNullOrEmpty(model) ? null : model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Find <paramref name="model"/>'s descriptor, <c>&lt;root&gt;\&lt;Module&gt;\Descriptor\&lt;Model&gt;.xml</c>,
    /// under the first root that has it. The package folder a descriptor sits in is the module.
    /// </summary>
    public static Target? Locate(string model, IEnumerable<string> roots, string? project = null)
    {
        foreach (var root in roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            IEnumerable<string> packages;
            try { packages = Directory.EnumerateDirectories(root); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var package in packages)
            {
                if (File.Exists(Path.Combine(package, "Descriptor", model + ".xml")))
                    return new Target(model, Path.GetFileName(package), Path.GetFullPath(root), project);
            }
        }
        return null;
    }

    /// <summary>
    /// <c>xppc.exe</c>'s arguments for <paramref name="target"/>. <paramref name="packagesRoot"/>
    /// is the platform (FrameworkDirectory on UDE); when the model lives elsewhere, both roots are
    /// reference folders.
    /// </summary>
    public static IReadOnlyList<string> XppcArgs(Target target, string packagesRoot, bool incremental)
    {
        var package = Path.Combine(target.MetadataRoot, target.Module);
        var output = Path.Combine(package, "bin");
        var args = new List<string>
        {
            $"-metadata={target.MetadataRoot}",
            $"-compilermetadata={packagesRoot}",
            $"-modelmodule={target.Module}",
            $"-output={output}",
            $"-referencefolder={packagesRoot}",
        };
        if (!SamePath(packagesRoot, target.MetadataRoot))
            args.Add($"-referencefolder={target.MetadataRoot}");
        args.Add($"-refPath={output}");
        args.Add($"-log={Path.Combine(package, "BuildModelResult.log")}");
        args.Add($"-xmlLog={Path.Combine(package, "BuildModelResult.xml")}");
        if (incremental) args.Add("-incremental");
        return args;
    }

    /// <summary><c>LabelC.exe</c>'s arguments, as Visual Studio logs them in <c>CompileLabels.xml</c>.</summary>
    public static IReadOnlyList<string> LabelcArgs(Target target)
    {
        var package = Path.Combine(target.MetadataRoot, target.Module);
        return
        [
            $"-metadata={target.MetadataRoot}",
            $"-output={Path.Combine(package, "Resources")}",
            $"-modelmodule={target.Module}",
            $"-OutLog={Path.Combine(package, "CompileLabels.log")}",
            $"-ErrLog={Path.Combine(package, "CompileLabels.err.log")}",
        ];
    }

    /// <summary>
    /// Build <paramref name="targets"/> in order, stopping at the first that fails — a model
    /// later in a solution usually references the one before it.
    /// </summary>
    public static ToolResult<object> Build(
        IReadOnlyList<Target> targets, string packagesRoot, bool incremental, string reason)
    {
        var xppc = BuildTooling.ResolvePackageBinTool("xppc.exe", packagesRoot);
        if (!xppc.Ok)
        {
            return ToolResult<object>.Fail("XPPC_NOT_FOUND",
                xppc.Problem ?? "xppc.exe could not be resolved.",
                "X++ projects are compiled with <packages>\\bin\\xppc.exe. Set D365FO_PACKAGES_PATH (or --packages) to the PackagesLocalDirectory / FrameworkDirectory that holds it.");
        }
        var labelc = BuildTooling.ResolvePackageBinTool("LabelC.exe", packagesRoot);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var models = new List<object>();
        var diagnostics = new List<XppcDiagnostic>();
        var stale = false;
        var exit = 0;
        var tail = string.Empty;

        foreach (var target in targets)
        {
            var package = Path.Combine(target.MetadataRoot, target.Module);

            object? labels = null;
            if (labelc.Ok && HasLabelFiles(package))
            {
                Directory.CreateDirectory(Path.Combine(package, "Resources"));
                var labelArgs = LabelcArgs(target);
                var (lExit, lOut, lErr, lElapsed) = SdlcRunner.Run(labelc.Path!, labelArgs);
                labels = new { exitCode = lExit, elapsedMs = (long)lElapsed.TotalMilliseconds, args = labelArgs };
                if (lExit != 0)
                {
                    exit = lExit;
                    tail = Tail(lOut + "\n" + lErr + "\n" + ReadOrEmpty(Path.Combine(package, "CompileLabels.err.log")), 20);
                    models.Add(new { target.Model, target.Module, target.MetadataRoot, target.Project, labels, xppc = (object?)null });
                    break;
                }
            }

            var args = XppcArgs(target, packagesRoot, incremental);
            var started = DateTime.UtcNow;
            var (xExit, xOut, xErr, xElapsed) = SdlcRunner.Run(xppc.Path!, args);
            // Only this run's log: a compiler that rejected its arguments writes none, and the
            // previous build's log would then be reported as this build's result.
            var log = xOut + "\n" + xErr + "\n" + ReadIfWrittenSince(Path.Combine(package, "BuildModelResult.log"), started);
            var found = XppcDiagnostics.Parse(log).Distinct().ToList();
            diagnostics.AddRange(found);
            stale |= XppcDiagnostics.IndicatesStaleSymbols(log);
            tail = Tail(log, 20);

            models.Add(new
            {
                target.Model,
                target.Module,
                target.MetadataRoot,
                target.Project,
                labels,
                xppc = new
                {
                    exitCode = xExit,
                    elapsedMs = (long)xElapsed.TotalMilliseconds,
                    errorCount = found.Count(d => d.Severity == "error"),
                    warningCount = found.Count(d => d.Severity == "warning"),
                    log = Path.Combine(package, "BuildModelResult.log"),
                    args,
                },
            });

            if (xExit != 0)
            {
                exit = xExit;
                break;
            }
        }
        sw.Stop();

        var errors = diagnostics.Where(d => d.Severity == "error").Select(Project).ToList();
        var warnings = diagnostics.Where(d => d.Severity == "warning").Select(Project).ToList();
        var payload = new
        {
            buildSucceeded = exit == 0,
            exitCode = exit,
            elapsedMs = sw.ElapsedMilliseconds,
            engine = "xppc",
            engineReason = reason,
            compiler = xppc.Path,
            incremental,
            models,
            errorCount = errors.Count,
            warningCount = warnings.Count,
            errors,
            warnings,
            xppcDiagnostics = diagnostics.Count == 0 ? null : diagnostics.Select(Project).ToList(),
            staleSymbols = stale
                ? "xppc reports stale symbols from a previous incremental build — build again without --incremental."
                : null,
            tail,
        };

        return ToolResult<object>.Success(payload, exit == 0 ? null : ["build-failed"]);
    }

    private static object Project(XppcDiagnostic d) => new
    {
        severity = d.Severity,
        kind = d.Kind,
        model = d.Model,
        @object = d.Object,
        member = d.Member,
        line = d.Line,
        column = d.Column,
        message = d.Message,
        hint = d.Hint,
    };

    private static bool HasLabelFiles(string package)
    {
        try
        {
            // <package>\<model>\AxLabelFile\*.xml, for any model the package holds.
            return Directory.EnumerateDirectories(package)
                .Select(d => Path.Combine(d, "AxLabelFile"))
                .Any(d => Directory.Exists(d) && Directory.EnumerateFiles(d, "*.xml").Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (IOException) { return string.Empty; }
    }

    private static string ReadIfWrittenSince(string path, DateTime utc) =>
        File.Exists(path) && File.GetLastWriteTimeUtc(path) >= utc.AddSeconds(-2) ? ReadOrEmpty(path) : string.Empty;

    private static string Tail(string text, int lines) =>
        string.Join('\n', text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(lines));
}
