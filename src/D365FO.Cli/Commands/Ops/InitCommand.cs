using D365FO.Core;
using D365FO.Core.Index;
using Spectre.Console;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Ops;

/// <summary>
/// Quickstart: emits a JSON run report listing the effective settings,
/// detects <c>PackagesLocalDirectory</c> automatically, and optionally
/// runs <c>index build</c> + <c>index extract</c>. Replaces the copy-paste
/// shell snippet documented in <c>docs/SETUP.md#quickstart-script</c>.
/// </summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("Explicit PackagesLocalDirectory path. Skips auto-detect.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--db <PATH>")]
        public string? DatabasePath { get; init; }

        [CommandOption("--run-extract")]
        [System.ComponentModel.Description("Immediately walk packages + populate the index (equivalent to a follow-up 'index build' + 'index extract').")]
        public bool RunExtract { get; init; }

        [CommandOption("--dry-run")]
        [System.ComponentModel.Description("Report discovered paths without touching disk.")]
        public bool DryRun { get; init; }

        [CommandOption("--persist-profile")]
        [System.ComponentModel.Description("Append D365FO_PACKAGES_PATH / D365FO_INDEX_DB to the user's shell profile (PowerShell $PROFILE on Windows, ~/.profile otherwise).")]
        public bool PersistProfile { get; init; }
    }

    private static readonly string[] CandidateRoots =
    {
        @"C:\AosService\PackagesLocalDirectory",
        @"K:\AosService\PackagesLocalDirectory",
        @"J:\AosService\PackagesLocalDirectory",
        @"C:\PackagesLocalDirectory",
    };

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);
        var cfg = D365FoSettings.FromEnvironment(settings.DatabasePath);

        var packages = settings.PackagesPath ?? cfg.PackagesPath ?? AutoDetectPackages();
        var workspace = cfg.WorkspacePath ?? (packages is null ? null : Path.GetFullPath(Path.Combine(packages, "..")));
        var steps = new List<object>();

        void Log(string name, bool ok, string? detail = null, string? hint = null)
            => steps.Add(new { step = name, ok, detail, hint });

        Log("resolve.packages", packages is not null,
            detail: packages,
            hint: packages is null ? "Pass --packages <PATH> or set D365FO_PACKAGES_PATH." : null);
        Log("resolve.workspace", workspace is not null, workspace);
        Log("resolve.database", !string.IsNullOrEmpty(cfg.DatabasePath), cfg.DatabasePath);

        if (!settings.DryRun && packages is not null)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(cfg.DatabasePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var repo = new MetadataRepository(cfg.DatabasePath);
                var applied = repo.EnsureSchema();
                Log("index.schema", true, applied ? $"Applied v{MetadataRepository.CurrentSchemaVersion}" : $"Already v{MetadataRepository.CurrentSchemaVersion}");
            }
            catch (Exception ex)
            {
                Log("index.schema", false, ex.Message);
            }
        }

        int extractExit = 0;
        if (settings.RunExtract && packages is not null && !settings.DryRun)
        {
            try
            {
                Log("index.extract", true, "Starting…");
                extractExit = D365FO.Cli.Commands.Index.IndexExtractCommand.ExtractCore(
                    OutputMode.Kind.Json, packages, cfg.DatabasePath, null, null);
                Log("index.extract.done", extractExit == 0, extractExit == 0 ? "ok" : $"exit code {extractExit}");
            }
            catch (Exception ex)
            {
                Log("index.extract.done", false, ex.Message);
            }
        }

        string? profilePath = null;
        if (settings.PersistProfile && packages is not null)
        {
            try
            {
                profilePath = ResolveProfilePath();
                if (settings.DryRun)
                {
                    Log("profile.persist", true, $"Would append to {profilePath} (dry-run).");
                }
                else
                {
                    var vars = new Dictionary<string, string>
                    {
                        ["D365FO_PACKAGES_PATH"] = packages!,
                        ["D365FO_INDEX_DB"] = cfg.DatabasePath,
                    };
                    if (!string.IsNullOrEmpty(workspace))
                        vars["D365FO_WORKSPACE"] = workspace;
                    var added = WriteProfileBlock(profilePath, vars);
                    Log("profile.persist", true, added
                        ? $"Appended d365fo-cli block to {profilePath}"
                        : $"Profile block already present in {profilePath}");
                }
            }
            catch (Exception ex)
            {
                Log("profile.persist", false, ex.Message);
            }
        }

        var ok = steps.Cast<dynamic>().All(s => (bool)s.ok);
        var payload = ok
            ? ToolResult<object>.Success(new
            {
                packages,
                workspace,
                database = cfg.DatabasePath,
                dryRun = settings.DryRun,
                extracted = settings.RunExtract && !settings.DryRun && extractExit == 0,
                nextSteps = new[]
                {
                    "Set D365FO_PACKAGES_PATH to persist the discovered path.",
                    "Run 'd365fo index extract' to ingest metadata.",
                    "Run 'd365fo doctor' to verify environment.",
                },
                steps,
            })
            : ToolResult<object>.Fail(D365FoErrorCodes.DoctorFailed, "Init completed with errors.",
                hint: "See 'data.steps' (use --output json) for details.");

        return RenderHelpers.Render(kind, payload, _ =>
        {
            foreach (dynamic s in steps)
            {
                var tick = (bool)s.ok ? "[green]✓[/]" : "[red]✗[/]";
                var detail = s.detail is null ? "" : $" [grey]— {RenderHelpers.Escape((string)s.detail)}[/]";
                AnsiConsole.MarkupLine($"{tick} {s.step}{detail}");
            }
            if (ok)
                AnsiConsole.MarkupLine("[green]Init complete.[/] Run 'd365fo doctor' to verify.");
        });
    }

    private static string? AutoDetectPackages()
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var c in CandidateRoots)
            if (Directory.Exists(c)) return c;
        return null;
    }

    // ---- profile persistence -----------------------------------------

    private const string BlockBegin = "# >>> d365fo-cli init (auto-generated) >>>";
    private const string BlockEnd   = "# <<< d365fo-cli init <<<";

    /// <summary>Pick the canonical shell profile for the current OS.</summary>
    internal static string ResolveProfilePath()
    {
        if (OperatingSystem.IsWindows())
        {
            // PowerShell 5.1 + 7 agree on this path.
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1");
        }
        var home = Environment.GetEnvironmentVariable("HOME") ?? "~";
        return Path.Combine(home, ".profile");
    }

    /// <summary>
    /// Append (or replace) a marker-delimited block of env-var exports to the
    /// profile. Returns <c>true</c> when the block was added / refreshed;
    /// <c>false</c> when it was already present with identical contents.
    /// Idempotent — safe to re-run.
    /// </summary>
    internal static bool WriteProfileBlock(string profilePath, IReadOnlyDictionary<string, string> vars)
    {
        var dir = Path.GetDirectoryName(profilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var isPowerShell = profilePath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase);
        var newBlock = BuildBlock(vars, isPowerShell);
        var existing = File.Exists(profilePath) ? File.ReadAllText(profilePath) : "";

        var startIdx = existing.IndexOf(BlockBegin, StringComparison.Ordinal);
        var endIdx = existing.IndexOf(BlockEnd, StringComparison.Ordinal);
        if (startIdx >= 0 && endIdx > startIdx)
        {
            var oldBlock = existing.Substring(startIdx, endIdx + BlockEnd.Length - startIdx);
            if (string.Equals(oldBlock, newBlock, StringComparison.Ordinal))
                return false;
            var replaced = existing.Remove(startIdx, endIdx + BlockEnd.Length - startIdx).Insert(startIdx, newBlock);
            File.WriteAllText(profilePath, replaced);
            return true;
        }

        var sep = existing.Length == 0 || existing.EndsWith('\n') ? "" : Environment.NewLine;
        File.AppendAllText(profilePath, sep + Environment.NewLine + newBlock + Environment.NewLine);
        return true;
    }

    private static string BuildBlock(IReadOnlyDictionary<string, string> vars, bool isPowerShell)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(BlockBegin);
        foreach (var (k, v) in vars)
        {
            if (isPowerShell)
                sb.AppendLine($"$env:{k} = '{v.Replace("'", "''")}'");
            else
                sb.AppendLine($"export {k}=\"{v.Replace("\"", "\\\"")}\"");
        }
        sb.Append(BlockEnd);
        return sb.ToString();
    }
}
