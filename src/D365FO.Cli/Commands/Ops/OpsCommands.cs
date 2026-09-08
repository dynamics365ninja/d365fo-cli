using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Ops;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

internal enum DoctorSeverity { Ok, Warn, Fail }

internal sealed record DoctorCheck(string Name, bool Ok, string? Detail, string Severity);

public sealed class DoctorCommand : Command<DoctorCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment();
        var checks = new List<DoctorCheck>();

        void Add(string name, DoctorSeverity severity, string? detail = null)
        {
            // `Ok` stays true for Ok + Warn to preserve back-compat with
            // callers that only key off the boolean. Hard failures (Fail)
            // are the only ones that flip the overall doctor result.
            var ok = severity != DoctorSeverity.Fail;
            checks.Add(new DoctorCheck(name, ok, detail, severity.ToString().ToLowerInvariant()));
        }

        Add("config.databasePath resolvable",
            string.IsNullOrEmpty(cfg.DatabasePath) ? DoctorSeverity.Fail : DoctorSeverity.Ok,
            cfg.DatabasePath);

        Add("config.packagesPath set",
            string.IsNullOrEmpty(cfg.PackagesPath) ? DoctorSeverity.Fail : DoctorSeverity.Ok,
            cfg.PackagesPath ?? "Set D365FO_PACKAGES_PATH or use --packages.");

        Add("config.customPackagesPaths",
            cfg.CustomPackagesPaths.Count == 0 ? DoctorSeverity.Warn : DoctorSeverity.Ok,
            cfg.CustomPackagesPaths.Count == 0
                ? "not set (optional — set D365FO_CUSTOM_PACKAGES_PATH to your git repo for `generate --install-to`)."
                : string.Join(", ", cfg.CustomPackagesPaths));

        // Surface usage of the deprecated pre-rename env var so users migrate.
        var legacyCustom = D365FoSettings.Resolve("D365FO_EXTRA_PACKAGES_PATH");
        var newCustom = D365FoSettings.Resolve("D365FO_CUSTOM_PACKAGES_PATH");
        if (!string.IsNullOrWhiteSpace(legacyCustom))
        {
            Add("config.customPackagesPaths (deprecated env var)",
                DoctorSeverity.Warn,
                string.IsNullOrWhiteSpace(newCustom)
                    ? "D365FO_EXTRA_PACKAGES_PATH is deprecated; rename it to D365FO_CUSTOM_PACKAGES_PATH (still honored as a fallback for now)."
                    : "D365FO_EXTRA_PACKAGES_PATH is set but ignored because D365FO_CUSTOM_PACKAGES_PATH takes precedence; remove the old variable.");
        }

        Add("config.workspacePath",
            DoctorSeverity.Ok,
            cfg.WorkspacePath ?? "not set (optional — only needed for `generate --out <PATH>` outside Packages).");

        Add("index db exists",
            File.Exists(cfg.DatabasePath) ? DoctorSeverity.Ok : DoctorSeverity.Fail,
            File.Exists(cfg.DatabasePath) ? null : "Run 'd365fo index build'.");

        Add("runtime", DoctorSeverity.Ok, $".NET {Environment.Version} on {Environment.OSVersion.Platform}");

        Add("platform.windows (build/sync/bp require it)",
            OperatingSystem.IsWindows() ? DoctorSeverity.Ok : DoctorSeverity.Warn,
            OperatingSystem.IsWindows() ? null : "Non-Windows host: write-ops (build, sync, bp, test) are unavailable.");

        // Being on Windows is not the same as having a working X++ toolchain, and answering
        // "is build tooling functional here?" with an OS check is how a broken MSBuild reached
        // the user as an unexplained MSB4062 (issue #207). Probe the tools themselves.
        if (OperatingSystem.IsWindows())
        {
            var msbuild = BuildTooling.ResolveMsBuild(null);
            Add("build.msbuild (d365fo build)",
                msbuild.Ok ? DoctorSeverity.Ok : DoctorSeverity.Warn,
                msbuild.Ok
                    ? $"{msbuild.Path} (from {msbuild.Source})"
                    : msbuild.Path is null
                        ? msbuild.Problem
                        : $"{msbuild.Path} (from {msbuild.Source}) — {msbuild.Problem}");

            var targets = BuildTooling.ResolveDynamicsTargets();
            Add("build.dynamicsTargets (X++ MSBuild tasks)",
                targets.Ok ? DoctorSeverity.Ok : DoctorSeverity.Warn,
                targets.Ok ? targets.Path : targets.Problem);

            foreach (var exe in BuildTooling.PackageBinTools)
            {
                var t = BuildTooling.ResolvePackageBinTool(exe, cfg.PackagesPath);
                Add($"build.tooling ({exe})",
                    t.Ok ? DoctorSeverity.Ok : DoctorSeverity.Warn,
                    t.Ok ? t.Path : t.Problem);
            }
        }

        // Bridge — required for `generate --install-to <Model>` and `find refs --xref`.
        // Reports Warn (not Fail) when missing because read-only operations work
        // without it via the SQLite index. Resolved via the unified config chain
        // (env var → settings.json) so a value set in settings.json is honored.
        var bridgeEnabled = D365FoSettings.ResolveFlag("D365FO_BRIDGE_ENABLED");
        Add("bridge.enabled (required for `generate --install-to`)",
            bridgeEnabled ? DoctorSeverity.Ok : DoctorSeverity.Warn,
            bridgeEnabled
                ? "D365FO_BRIDGE_ENABLED=1"
                : "Set D365FO_BRIDGE_ENABLED=\"1\" (quoted in settings.json) to enable model installs.");

        if (bridgeEnabled)
        {
            var bridgeExe = BridgeOptions.ResolveExecutable(
                D365FoSettings.Resolve("D365FO_BRIDGE_PATH"));
            Add("bridge.executable",
                bridgeExe is null
                    ? (OperatingSystem.IsWindows() ? DoctorSeverity.Fail : DoctorSeverity.Warn)
                    : DoctorSeverity.Ok,
                bridgeExe ?? "D365FO.Bridge.exe not found next to d365fo.exe; set D365FO_BRIDGE_PATH.");

            // Mirror the bridge's own resolution (MetadataBootstrap.ResolveBinPath):
            // D365FO_BIN_PATH when set, otherwise <D365FO_PACKAGES_PATH>\bin.
            var binPath = D365FoSettings.Resolve("D365FO_BIN_PATH");
            var binSource = "D365FO_BIN_PATH";
            if (string.IsNullOrWhiteSpace(binPath) && !string.IsNullOrEmpty(cfg.PackagesPath))
            {
                binPath = Path.Combine(cfg.PackagesPath, "bin");
                binSource = "D365FO_PACKAGES_PATH\\bin";
            }
            var binOk = !string.IsNullOrWhiteSpace(binPath) && Directory.Exists(binPath);
            Add("bridge.binPath",
                binOk ? DoctorSeverity.Ok : DoctorSeverity.Warn,
                binOk
                    ? $"{binPath} (from {binSource})"
                    : "Set D365FO_BIN_PATH to the folder containing Microsoft.Dynamics.AX.Metadata.*.dll (e.g. <PackagesLocalDirectory>\\bin).");
        }

        // Index freshness — the index is the single source of truth for
        // grounding, but only while it reflects the current workspace.
        if (File.Exists(cfg.DatabasePath) && !string.IsNullOrEmpty(cfg.PackagesPath))
        {
            try
            {
                var repo = RepoFactory.Create();
                var roots = new List<string> { cfg.PackagesPath! };
                roots.AddRange(cfg.CustomPackagesPaths);
                var staleness = D365FO.Core.Index.IndexStaleness.Check(repo, roots);
                Add("index freshness (stale-index)",
                    staleness.IsStale ? DoctorSeverity.Fail : DoctorSeverity.Ok,
                    staleness.IsStale ? staleness.Detail : $"index extracted {staleness.LastExtractedUtc:O}");
            }
            catch (Exception ex)
            {
                Add("index freshness (stale-index)", DoctorSeverity.Ok, $"check skipped: {ex.Message}");
            }
        }

        var allOk = checks.All(c => c.Ok);
        var hasWarnings = checks.Any(c => c.Severity == "warn");
        var payload = allOk
            ? ToolResult<object>.Success(new { allOk, hasWarnings, checks })
            : ToolResult<object>.Fail("DOCTOR_FAILED", "One or more checks failed.",
                "Re-run with --output json and inspect 'error.hint' or 'data.checks'.");

        return RenderHelpers.Render(kind,
            allOk ? payload : ToolResult<object>.Success(new { allOk, hasWarnings, checks }), _ =>
        {
            foreach (var c in checks)
            {
                var tick = c.Severity switch
                {
                    "fail" => "[red]✗[/]",
                    "warn" => "[yellow]![/]",
                    _      => "[green]✓[/]",
                };
                var detail = c.Detail is null ? "" : $" [grey]— {RenderHelpers.Escape(c.Detail)}[/]";
                AnsiConsole.MarkupLine($"{tick} {RenderHelpers.Escape(c.Name)}{detail}");
            }
            if (!allOk)
            {
                AnsiConsole.MarkupLine("[red]Some checks failed.[/]");
            }
            else if (hasWarnings)
            {
                AnsiConsole.MarkupLine("[yellow]All required checks passed (with warnings).[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[green]All checks passed.[/]");
            }
        });
    }
}

public sealed class VersionCommand : Command<VersionCommand.Settings>
{
    public sealed class Settings : D365OutputSettings { }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var asm = typeof(VersionCommand).Assembly.GetName();
        var payload = ToolResult<object>.Success(new
        {
            name = "d365fo",
            version = asm.Version?.ToString() ?? "0.1.0-dev",
            runtime = $".NET {Environment.Version}",
            os = Environment.OSVersion.ToString(),
        });
        return RenderHelpers.Render(kind, payload);
    }
}
