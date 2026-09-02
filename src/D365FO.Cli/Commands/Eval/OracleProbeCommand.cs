using D365FO.Core;
using D365FO.Core.Eval;
using D365FO.Core.Ops;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

/// <summary>
/// <c>d365fo oracle probe</c> — compile one artefact (or a handful) with the real X++ compiler.
/// </summary>
/// <remarks>
/// <para>
/// The offline validator says whether an artefact looks right. This says whether it compiles,
/// which is the only claim that settles an argument. It exists because the eval goldens cover
/// only what the case catalog covers: everything a new <c>generate</c> sub-command or form
/// pattern produces had never met the compiler, and the first hand-run of this recipe turned up
/// uncompilable output from three of them.
/// </para>
/// <para>
/// A clean run is only evidence when the compiler actually ran. If xppc rejects the argument
/// list it prints its own usage, which parses as "no diagnostics" — so that case is reported as
/// a failure of the probe, never as a pass.
/// </para>
/// </remarks>
public sealed class OracleProbeCommand : Command<OracleProbeCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<ARTIFACT>")]
        [System.ComponentModel.Description("AOT XML to compile. Repeat the argument to probe several together.")]
        public string[] Artifacts { get; init; } = Array.Empty<string>();

        [CommandOption("--work-dir <PATH>")]
        [System.ComponentModel.Description("Where to build the throwaway metadata store (default: a temp directory, deleted afterwards).")]
        public string? WorkDir { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Throwaway model name (default: D365FoCliProbe).")]
        public string? Model { get; init; }

        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("PackagesLocalDirectory to resolve references against (default: D365FO_PACKAGES_PATH).")]
        public string? PackagesPath { get; init; }

        [CommandOption("--compiler <PATH>")]
        [System.ComponentModel.Description("xppc.exe (default: <packages>\\Bin\\xppc.exe).")]
        public string? CompilerPath { get; init; }

        [CommandOption("--compiler-args <TEMPLATE>")]
        [System.ComponentModel.Description("Override the argument template. Placeholders: {metadata} {packages} {model} {output} {log}.")]
        public string? CompilerArgs { get; init; }

        [CommandOption("--no-fixture")]
        [System.ComponentModel.Description("Do not lay down the MiniAot fixture; probe the artefact against the installation alone.")]
        public bool NoFixture { get; init; }

        [CommandOption("--keep")]
        [System.ComponentModel.Description("Keep the work directory after the run, to inspect what was compiled.")]
        public bool Keep { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var guard = SdlcRunner.WindowsGuard("d365fo oracle probe");
        if (guard is not null) return RenderHelpers.Render(kind, guard);

        if (settings.Artifacts.Length == 0)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "At least one artefact is required."));

        var packagesRoot = settings.PackagesPath ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH");
        if (string.IsNullOrWhiteSpace(packagesRoot) || !Directory.Exists(packagesRoot))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("PACKAGES_NOT_FOUND",
                $"No PackagesLocalDirectory at: {packagesRoot ?? "(unset)"}",
                "Set D365FO_PACKAGES_PATH (or pass --packages) to the installation to compile against."));

        var compiler = settings.CompilerPath ?? Path.Combine(packagesRoot!, "Bin", "xppc.exe");
        if (!File.Exists(compiler))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("XPPC_NOT_FOUND",
                $"xppc.exe not found at: {compiler}",
                "Pass --compiler, or point --packages at the FrameworkDirectory that contains Bin\\xppc.exe."));

        var modelName = string.IsNullOrWhiteSpace(settings.Model) ? "D365FoCliProbe" : settings.Model!;
        var workDir = settings.WorkDir ?? Path.Combine(Path.GetTempPath(), "d365fo-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var ephemeral = settings.WorkDir is null && !settings.Keep;

        try
        {
            Directory.CreateDirectory(workDir);
            var prep = OracleProbe.Prepare(
                settings.Artifacts, workDir, modelName, !settings.NoFixture, packagesRoot);

            if (prep.Placed.Count == 0)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("NOTHING_TO_COMPILE",
                    "None of the given files is a usable AOT document, so the compiler was not run.",
                    string.Join("; ", prep.Rejected.Select(r => $"{Path.GetFileName(r.File)}: {r.Reason}"))));

            var result = XppcRunner.Compile(workDir, packagesRoot!, compiler, modelName, settings.CompilerArgs);

            if (result.InvocationRejected)
                return RenderHelpers.Render(kind, ToolResult<object>.Fail("PROBE_INVOCATION",
                    "The compiler rejected the argument list, so nothing was compiled — this run says nothing about the artefacts.",
                    $"Args: {string.Join(' ', result.Args)}\n\n{result.LogTail}"));

            var (attributed, unattributed) = OracleProbe.Attribute(prep, result.Diagnostics);
            var errors = result.Diagnostics.Where(d => d.Severity == "error").ToList();

            var warnings = new List<string>();
            if (prep.Rejected.Count > 0)
                warnings.Add($"{prep.Rejected.Count} file(s) were not compiled: "
                           + string.Join("; ", prep.Rejected.Select(r => $"{Path.GetFileName(r.File)} — {r.Reason}")));
            if (unattributed.Count > 0)
                warnings.Add($"{unattributed.Count} diagnostic(s) name no probed artefact — they belong to the fixture "
                           + "or to a reference, not to what you asked about.");

            var payload = ToolResult<object>.Success(new
            {
                compiled = prep.Placed.Select(p => new { p.ObjectName, p.AxFolder, source = p.Source }),
                clean = errors.Count == 0,
                errorCount = errors.Count,
                diagnosticCount = result.Diagnostics.Count,
                elapsedMs = result.ElapsedMs,
                findings = attributed.Select(a => new
                {
                    artifact = a.Artifact,
                    severity = a.Diagnostic.Severity,
                    member = a.Diagnostic.Member,
                    line = a.Diagnostic.Line,
                    message = a.Diagnostic.Message,
                    hint = a.Diagnostic.Hint,
                }),
                unattributed = unattributed.Select(d => new { d.Severity, d.Object, d.Message }),
                workDir = settings.Keep || settings.WorkDir is not null ? workDir : null,
                compilerArgs = string.Join(' ', result.Args),
            }, warnings.Count > 0 ? warnings : null);

            var rc = RenderHelpers.Render(kind, payload);
            return rc != 0 ? rc : errors.Count == 0 ? 0 : 2;
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail("PROBE_FAILED", ex.Message));
        }
        finally
        {
            if (ephemeral)
            {
                try { Directory.Delete(workDir, recursive: true); }
                catch { /* a temp directory the compiler still holds is not worth failing over */ }
            }
        }
    }
}
