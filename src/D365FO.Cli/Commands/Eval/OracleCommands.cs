using D365FO.Core;
using D365FO.Core.Eval;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Eval;

// `d365fo oracle` — the measurements that decide whether the validator can be trusted.
//
// Every other gate in this repository judges what the tool produces. These judge the tool
// itself, against the one corpus nobody can argue with: the installation on disk.

/// <summary>
/// <c>d365fo oracle sweep</c> — run every rule over every shipped file and count what it says.
/// </summary>
/// <remarks>
/// The bar is zero ERRORS on Microsoft's own X++ and XML. A rule that fires on shipped code is
/// not a strict rule, it is a wrong one: it teaches a caller that findings are noise, and the
/// next finding — the real one — is ignored too.
/// </remarks>
public sealed class OracleSweepCommand : Command<OracleSweepCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("Root to sweep. Defaults to D365FO_PACKAGES_PATH.")]
        public string? PackagesPath { get; init; }

        [CommandOption("--model <NAME>")]
        [System.ComponentModel.Description("Sweep one model instead of the whole installation.")]
        public string? Model { get; init; }

        [CommandOption("--kind <FOLDER>")]
        [System.ComponentModel.Description("Sweep one AOT folder, e.g. AxTable or AxClass.")]
        public string? Kind { get; init; }

        [CommandOption("-l|--limit <N>")]
        [System.ComponentModel.Description("Stop after N files. 0 (default) sweeps everything.")]
        public int Limit { get; init; }

        [CommandOption("--samples <N>")]
        [System.ComponentModel.Description("Example findings kept per rule (default 3).")]
        public int Samples { get; init; } = 3;

        [CommandOption("--warnings")]
        [System.ComponentModel.Description("Report warnings too. Off by default: only errors are held to the zero bar.")]
        public bool Warnings { get; init; }

        [CommandOption("--parallelism <N>")]
        public int? Parallelism { get; init; }

        [CommandOption("--dry")]
        [System.ComponentModel.Description("Sweep the checked-in fixtures instead of an installation, so CI without a D365FO host still exercises the sweep itself.")]
        public bool Dry { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        // --dry sweeps the checked-in MiniAot fixture, which is packages-shaped
        // (<root>/<package>/<model>/Ax*) precisely so it can stand in for an installation.
        var root = settings.Dry
            ? (EvalPaths.FindRepoRoot() is { } repoRoot ? EvalPaths.FixtureDir(repoRoot) : null)
            : settings.PackagesPath ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH");

        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.PackagesPathNotFound,
                settings.Dry
                    ? $"No fixture root at {root}."
                    : $"No packages path to sweep{(root is null ? "" : $": {root}")}.",
                "Pass --packages <PATH>, set D365FO_PACKAGES_PATH, or use --dry to sweep the checked-in fixtures."));

        var report = OracleSweep.Run(root!, new OracleSweep.Options(
            settings.Model, settings.Kind, settings.Limit, settings.Samples,
            settings.Warnings, settings.Parallelism), RepoOrNull());

        var warnings = new List<string>();
        if (!report.BarHeld)
            warnings.Add($"{report.Errors} ERROR-severity finding(s) against shipped code. Each one is a rule "
                       + "claiming an artefact does not work, contradicted by the fact that it ships and builds — "
                       + "fix the rule, not the file.");
        if (report.FilesUnreadable > 0)
            warnings.Add($"{report.FilesUnreadable} file(s) could not be read and were not judged.");
        if (report.FilesScanned == 0)
            warnings.Add("Nothing was swept — the root holds no <package>/<model>/Ax* folders.");

        var rc = RenderHelpers.Render(kind, ToolResult<object>.Success(report, warnings.Count > 0 ? warnings : null));
        return rc != 0 ? rc : report.BarHeld ? 0 : 2;
    }

    /// <summary>The index, when there is one: the property rules are sharper with mined stats.</summary>
    private static D365FO.Core.Validation.IPropertyStatsProvider? RepoOrNull()
    {
        try
        {
            var repo = RepoFactory.Create();
            return repo.HasPropertyStats() ? repo : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// <c>d365fo oracle census</c> — what the installation actually writes for one element.
/// </summary>
/// <remarks>
/// The measurement that has to precede a rule. Enforcing a shape the installation does not keep
/// produces findings against files that ship and build, and this repository has already released
/// one such rule and withdrawn it — the census is what would have refused it first.
/// </remarks>
public sealed class OracleCensusCommand : Command<OracleCensusCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<ELEMENT>")]
        [System.ComponentModel.Description("Root element to measure, e.g. AxTable, AxEnum, AxForm.")]
        public string Element { get; init; } = "";

        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("Installation to measure. Defaults to D365FO_PACKAGES_PATH.")]
        public string? PackagesPath { get; init; }

        [CommandOption("-l|--limit <N>")]
        [System.ComponentModel.Description("Stop after N documents. 0 (default) reads them all.")]
        public int Limit { get; init; }

        [CommandOption("--values <N>")]
        [System.ComponentModel.Description("Distinct leaf values to keep per member (default 5).")]
        public int Values { get; init; } = 5;

        [CommandOption("--parallelism <N>")]
        public int? Parallelism { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Element))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "An element is required."));

        var root = settings.PackagesPath ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.PackagesPathNotFound,
                "No installation to measure.",
                "Pass --packages <PATH> or set D365FO_PACKAGES_PATH. A census needs real files; there is no offline form of it."));

        var report = OracleCensus.Run(root!, settings.Element, settings.Limit, settings.Values, settings.Parallelism);

        var warnings = new List<string>();
        if (report.FilesScanned == 0)
            warnings.Add($"No <{settings.Element}> document was found under {root} — check the element name against the AOT folder names.");
        if (!report.OrderIsStable)
            warnings.Add($"Member order is NOT stable across the corpus ({report.OrderCounterExamples.Count} pair(s) seen both ways). "
                       + "Do not write a rule that depends on it.");
        if (report.SeenNotDeclared.Count > 0)
            warnings.Add($"{report.SeenNotDeclared.Count} member(s) the installation writes are not in the metadata contract: "
                       + string.Join(", ", report.SeenNotDeclared.Take(10))
                       + ". That is the contract being behind, not the files being wrong.");

        return RenderHelpers.Render(kind, ToolResult<object>.Success(report, warnings.Count > 0 ? warnings : null));
    }
}

/// <summary>
/// <c>d365fo oracle members</c> — the contract's declared members against the ones files carry.
/// </summary>
/// <remarks>
/// The same measurement read the other way: which declared members no file uses (dead contract),
/// and which used members are undeclared (drift). Both directions matter, because the validator
/// judges documents against the contract and the contract is a snapshot of a shipped assembly.
/// </remarks>
public sealed class OracleMembersCommand : Command<OracleMembersCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<ELEMENT>")]
        [System.ComponentModel.Description("Root element, e.g. AxTable.")]
        public string Element { get; init; } = "";

        [CommandOption("--packages <PATH>")]
        public string? PackagesPath { get; init; }

        [CommandOption("-l|--limit <N>")]
        [System.ComponentModel.Description("Documents to read. 0 (default) reads them all.")]
        public int Limit { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Element))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "An element is required."));

        var root = settings.PackagesPath ?? D365FoSettings.Resolve("D365FO_PACKAGES_PATH");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.PackagesPathNotFound, "No installation to measure.",
                "Pass --packages <PATH> or set D365FO_PACKAGES_PATH."));

        var census = OracleCensus.Run(root!, settings.Element, settings.Limit, sampleValues: 0);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            element = census.Element,
            contract = census.Contract,
            filesScanned = census.FilesScanned,
            declaredNeverSeen = census.DeclaredNeverSeen,
            seenNotDeclared = census.SeenNotDeclared,
            usage = census.Members.Select(m => new
            {
                m.Member,
                m.Declared,
                m.Files,
                share = census.FilesScanned == 0 ? "0%" : Math.Round(100.0 * m.Files / census.FilesScanned) + "%",
            }),
            note = "share is the proportion of documents carrying the member — the evidence a property rule "
                 + "needs before it can call the member required.",
        }, census.SeenNotDeclared.Count > 0
            ? [$"{census.SeenNotDeclared.Count} member(s) in the files are absent from the contract — the contract snapshot is behind."]
            : null));
    }
}
