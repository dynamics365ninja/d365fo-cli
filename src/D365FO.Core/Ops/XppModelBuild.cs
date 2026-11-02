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

    /// <summary>Where xppc writes cross-references (<c>build --xref</c>).</summary>
    /// <param name="Server">SQL Server instance, or null for xppc's default (the local server).</param>
    /// <param name="Database">Database name, or null for xppc's default (<c>DYNAMICSXREFDB</c>).</param>
    /// <param name="Source">Where the two came from, for the payload.</param>
    public sealed record XrefTarget(string? Server, string? Database, string Source);

    /// <summary>
    /// Read the server and database xppc should write cross-references to out of
    /// <c>D365FO_XREF_CONNECTIONSTRING</c>, the connection string the <c>find refs --xref</c>
    /// reader uses, so the build writes where the reader looks.
    /// </summary>
    /// <remarks>
    /// xppc takes the target as two switches, <c>-xrefSqlServer=</c> and <c>-xrefDbName=</c>
    /// (from <c>xppc.exe -?</c>, section CROSSREFERENCE), and connects with Windows
    /// authentication. A connection string that carries a SQL login cannot be honoured, so it is
    /// refused rather than quietly ignored. No connection string means xppc's own defaults:
    /// the local server and <c>DYNAMICSXREFDB</c>, which is also the reader's default.
    /// </remarks>
    public static (XrefTarget? Target, string? Problem) ReadXrefTarget(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return (new XrefTarget(null, null, "xppc default (local server, DYNAMICSXREFDB)"), null);

        var builder = new System.Data.Common.DbConnectionStringBuilder();
        try { builder.ConnectionString = connectionString; }
        catch (ArgumentException ex)
        {
            return (null, "D365FO_XREF_CONNECTIONSTRING is not a valid connection string: " + ex.Message);
        }

        static string? Value(System.Data.Common.DbConnectionStringBuilder b, params string[] keys)
        {
            foreach (var key in keys)
                if (b.TryGetValue(key, out var v) && v?.ToString() is { Length: > 0 } s) return s.Trim();
            return null;
        }

        if (Value(builder, "User ID", "UID", "User") is not null)
        {
            return (null,
                "D365FO_XREF_CONNECTIONSTRING carries a SQL login, and xppc writes cross-references with " +
                "Windows authentication only (it takes a server and a database name, no credentials).");
        }

        var server = Value(builder, "Server", "Data Source", "Address", "Addr", "Network Address");
        var database = Value(builder, "Database", "Initial Catalog");
        return (new XrefTarget(server, database, "D365FO_XREF_CONNECTIONSTRING"), null);
    }

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
    /// <param name="target">The model to compile.</param>
    /// <param name="packagesRoot">The platform packages root.</param>
    /// <param name="incremental">Compile only what changed since the last full build.</param>
    /// <param name="xref">Also update the cross-reference database, or null to leave it alone.</param>
    /// <param name="lastXrefModel">
    /// False for every model of a multi-model build but the last: xppc then skips rebuilding the
    /// cross-reference indexes (<c>-norecreatecrossrefindexes</c>), which its own help asks for
    /// when a build system compiles several models, and the last model rebuilds them once.
    /// </param>
    public static IReadOnlyList<string> XppcArgs(
        Target target, string packagesRoot, bool incremental, XrefTarget? xref = null, bool lastXrefModel = true)
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
        if (xref is not null)
        {
            args.Add("-xref");
            if (!string.IsNullOrWhiteSpace(xref.Server)) args.Add($"-xrefSqlServer={xref.Server}");
            if (!string.IsNullOrWhiteSpace(xref.Database)) args.Add($"-xrefDbName={xref.Database}");
            if (!lastXrefModel) args.Add("-norecreatecrossrefindexes");
        }
        return args;
    }

    /// <summary>
    /// The model whose <c>-xrefonly</c> run recreates the cross-reference indexes when an
    /// <c>--xref</c> build stopped before its last model, or null when nothing was deferred.
    /// </summary>
    /// <remarks>
    /// Every model but the last runs with <c>-norecreatecrossrefindexes</c>, and xppc's help says
    /// the last one must run without it so the indexes are recreated. A failure earlier stops the
    /// build, so that run never happens. The last model that compiled is the one to repeat, as
    /// <c>-xrefonly</c>; when none did, the one that failed is the only candidate.
    /// </remarks>
    /// <param name="targetCount">Models in the build.</param>
    /// <param name="lastXppcRun">Index of the last model xppc ran for, or -1.</param>
    /// <param name="lastSucceeded">Index of the last model xppc compiled, or -1.</param>
    public static int? XrefIndexRecreateTarget(int targetCount, int lastXppcRun, int lastSucceeded)
    {
        if (lastXppcRun < 0 || lastXppcRun >= targetCount - 1) return null;
        return lastSucceeded >= 0 ? lastSucceeded : lastXppcRun;
    }

    /// <summary>
    /// xppc's arguments for the <c>-xrefonly</c> run that recreates the cross-reference indexes
    /// (<see cref="XrefIndexRecreateTarget"/>). It writes its own logs so the build's logs stay.
    /// </summary>
    public static IReadOnlyList<string> XrefIndexRecreateArgs(Target target, string packagesRoot, XrefTarget xref)
    {
        var package = Path.Combine(target.MetadataRoot, target.Module);
        var args = XppcArgs(target, packagesRoot, incremental: false, xref, lastXrefModel: true)
            .Where(a => !a.StartsWith("-log=", StringComparison.OrdinalIgnoreCase)
                        && !a.StartsWith("-xmlLog=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        args.Add($"-log={Path.Combine(package, "BuildModelResult.xrefindexes.log")}");
        args.Add($"-xmlLog={Path.Combine(package, "BuildModelResult.xrefindexes.xml")}");
        args.Add("-xrefonly");
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
    /// <param name="targets">Models to build, in order.</param>
    /// <param name="packagesRoot">The platform packages root.</param>
    /// <param name="incremental">Compile only what changed since the last full build.</param>
    /// <param name="reason">Why the xppc route was chosen, for the payload.</param>
    /// <param name="xref">Also update the cross-reference database (<c>build --xref</c>), or null.</param>
    public static ToolResult<object> Build(
        IReadOnlyList<Target> targets, string packagesRoot, bool incremental, string reason, XrefTarget? xref = null)
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
        var lastXppcRun = -1;
        var lastSucceeded = -1;

        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
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

            var args = XppcArgs(target, packagesRoot, incremental, xref, lastXrefModel: i == targets.Count - 1);
            var started = DateTime.UtcNow;
            var (xExit, xOut, xErr, xElapsed) = SdlcRunner.Run(xppc.Path!, args);
            lastXppcRun = i;
            if (xExit == 0) lastSucceeded = i;
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

        object? xrefIndexes = null;
        if (xref is not null && XrefIndexRecreateTarget(targets.Count, lastXppcRun, lastSucceeded) is { } r)
        {
            var recreateArgs = XrefIndexRecreateArgs(targets[r], packagesRoot, xref);
            var (rExit, _, _, rElapsed) = SdlcRunner.Run(xppc.Path!, recreateArgs);
            xrefIndexes = new
            {
                recreatedBy = targets[r].Model,
                reason = "the build stopped before its last model, which recreates the cross-reference indexes",
                exitCode = rExit,
                elapsedMs = (long)rElapsed.TotalMilliseconds,
                args = recreateArgs,
            };
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
            xref = xref is null ? null : new
            {
                server = xref.Server ?? "(local)",
                database = xref.Database ?? "DYNAMICSXREFDB",
                source = xref.Source,
            },
            xrefIndexes,
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
