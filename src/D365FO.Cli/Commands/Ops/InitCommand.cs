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

        [CommandOption("--extra-packages <PATH>")]
        [System.ComponentModel.Description("Additional PackagesLocalDirectory root(s). Repeatable. Also writes D365FO_CUSTOM_PACKAGES_PATH when used with --persist-profile.")]
        public string[]? ExtraPackagesPaths { get; init; }

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

        [CommandOption("--label-languages <LANGS>")]
        [System.ComponentModel.Description("Comma-separated label languages to persist as D365FO_LABEL_LANGUAGES, e.g. en-us,cs. Only written with --persist-profile.")]
        public string? LabelLanguages { get; init; }

        [CommandOption("--no-wizard")]
        [System.ComponentModel.Description("Skip the interactive setup wizard even in a terminal; use flags/auto-detect only. Non-interactive runs (piped, CI, --output json) never show the wizard regardless of this flag.")]
        public bool NoWizard { get; init; }
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

        var extraFromFlags = D365FO.Cli.Commands.Index.IndexExtractCommand.MergeExtraPaths(
            settings.ExtraPackagesPaths,
            cfg.CustomPackagesPaths) ?? Array.Empty<string>();

        // Bare 'd365fo init' in a real terminal walks the user through it
        // instead of just reporting what auto-detect found — same job as
        // `npm run setup` upstream. Any explicit --packages, --output, or
        // --dry-run means the caller already knows what they want, so those
        // (and non-TTY / --no-wizard) skip straight to the flag-driven path.
        var runWizard = !settings.NoWizard
            && OutputMode.IsTty
            && string.IsNullOrEmpty(settings.Output)
            && !settings.DryRun
            && settings.PackagesPath is null;

        string? packages;
        List<string> extraPackages;
        bool persistProfile;
        bool runExtractNow;
        string? labelLanguages;

        if (runWizard)
        {
            var answers = RunWizard(settings, cfg, extraFromFlags);
            packages = answers.Packages;
            extraPackages = answers.ExtraPackages;
            persistProfile = answers.PersistProfile;
            runExtractNow = answers.RunExtract;
            labelLanguages = answers.LabelLanguages;
        }
        else
        {
            packages = settings.PackagesPath ?? cfg.PackagesPath ?? AutoDetectPackages();
            extraPackages = extraFromFlags.ToList();
            persistProfile = settings.PersistProfile;
            runExtractNow = settings.RunExtract;
            labelLanguages = settings.LabelLanguages;
        }

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
        if (runExtractNow && packages is not null && !settings.DryRun)
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

        if (persistProfile && packages is not null)
        {
            var vars = new Dictionary<string, string>
            {
                ["D365FO_PACKAGES_PATH"] = packages!,
                ["D365FO_INDEX_DB"]      = cfg.DatabasePath,
            };
            if (!string.IsNullOrEmpty(workspace))
                vars["D365FO_WORKSPACE_PATH"] = workspace;
            if (extraPackages is { Count: > 0 })
                vars["D365FO_CUSTOM_PACKAGES_PATH"] = string.Join(";", extraPackages);
            if (!string.IsNullOrWhiteSpace(labelLanguages))
                vars["D365FO_LABEL_LANGUAGES"] = labelLanguages;

            // --- JSON config file (shell-agnostic, solves Developer PowerShell issue) ---
            try
            {
                var configPath = D365FO.Core.D365FoSettings.GetDefaultConfigPath();
                if (settings.DryRun)
                {
                    Log("config.persist", true, $"Would write {configPath} (dry-run).");
                }
                else
                {
                    D365FO.Core.D365FoSettings.SaveJsonConfig(vars);
                    Log("config.persist", true, $"Written to {configPath}");
                }
            }
            catch (Exception ex)
            {
                Log("config.persist", false, ex.Message);
            }

            // --- Shell profiles (for interactive shell sessions that source $PROFILE) ---
            // Write to all profile paths that exist or can be created so that both
            // Windows PowerShell 5.1 (used by VS Developer PowerShell) and
            // PowerShell 7+ pick up the env vars automatically.
            foreach (var profilePath in ResolveAllProfilePaths())
            {
                try
                {
                    if (settings.DryRun)
                    {
                        Log("profile.persist", true, $"Would append to {profilePath} (dry-run).");
                    }
                    else
                    {
                        var added = WriteProfileBlock(profilePath, vars);
                        Log("profile.persist", true, added
                            ? $"Appended d365fo-cli block to {profilePath}"
                            : $"Profile block already present in {profilePath}");
                    }
                }
                catch (Exception ex)
                {
                    Log("profile.persist", false, $"{profilePath}: {ex.Message}");
                }
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
                extracted = runExtractNow && !settings.DryRun && extractExit == 0,
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

    private readonly record struct WizardAnswers(
        string? Packages,
        List<string> ExtraPackages,
        bool PersistProfile,
        bool RunExtract,
        string? LabelLanguages);

    /// <summary>
    /// Interactive first-run walkthrough for a bare <c>d365fo init</c> in a
    /// terminal — confirms/asks for the packages path, an optional UDE extra
    /// root, label languages, whether to persist, and whether to build the
    /// index now. Mirrors <c>npm run setup</c> upstream, scoped to what this
    /// CLI actually needs (no scenario picker, no secrets — it has neither).
    /// </summary>
    private static WizardAnswers RunWizard(Settings settings, D365FoSettings cfg, IReadOnlyList<string> extraFromFlags)
    {
        AnsiConsole.Write(new Rule("[bold]d365fo init — setup wizard[/]").LeftJustified());
        AnsiConsole.MarkupLine("[grey]Enter accepts the default shown. Nothing is written until the end. Pass --no-wizard to skip this.[/]");
        AnsiConsole.WriteLine();

        var detected = AutoDetectPackages() ?? cfg.PackagesPath;
        string? packages;
        if (detected is not null && AnsiConsole.Confirm($"Found PackagesLocalDirectory at [green]{RenderHelpers.Escape(detected)}[/] — use it?"))
        {
            packages = detected;
        }
        else
        {
            packages = AnsiConsole.Prompt(
                new TextPrompt<string>("Path to [bold]PackagesLocalDirectory[/]:")
                    .Validate(p => Directory.Exists(p)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Directory not found[/]")));
        }

        var extraPackages = new List<string>(extraFromFlags);
        if (AnsiConsole.Confirm("Second (UDE) packages root for local custom-model XML?", false))
        {
            var raw = AnsiConsole.Ask<string>("Extra path(s), semicolon-separated:");
            extraPackages.AddRange(raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var defaultLanguages = settings.LabelLanguages ?? D365FoSettings.Resolve("D365FO_LABEL_LANGUAGES") ?? "en-us";
        var labelLanguages = AnsiConsole.Ask("Label languages (comma-separated):", defaultLanguages);

        var persistProfile = settings.PersistProfile
            || AnsiConsole.Confirm("Persist these settings to your shell profile so new shells pick them up?");

        var runExtract = settings.RunExtract
            || AnsiConsole.Confirm("Build the metadata index now? (minutes for one model, longer for ApplicationSuite)", false);

        AnsiConsole.WriteLine();
        return new WizardAnswers(packages, extraPackages, persistProfile, runExtract, labelLanguages);
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

    /// <summary>
    /// Returns all PowerShell profile paths that should receive the d365fo-cli
    /// env-var block. On Windows this includes both the Windows PowerShell 5.1
    /// profile (used by the VS Developer PowerShell) and the PowerShell 7+
    /// profile so that neither shell host is left unconfigured.
    /// </summary>
    internal static IEnumerable<string> ResolveAllProfilePaths()
    {
        if (OperatingSystem.IsWindows())
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            // Windows PowerShell 5.1 — used by Visual Studio Developer PowerShell
            yield return Path.Combine(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1");
            // PowerShell 7+ (pwsh)
            yield return Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1");
        }
        else
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? "~";
            yield return Path.Combine(home, ".profile");
        }
    }

    /// <summary>Pick the canonical shell profile for the current OS.</summary>
    [Obsolete("Use ResolveAllProfilePaths() to handle both PS5.1 (VS Developer PowerShell) and PS7+.")]
    internal static string ResolveProfilePath()
        => ResolveAllProfilePaths().First();

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
