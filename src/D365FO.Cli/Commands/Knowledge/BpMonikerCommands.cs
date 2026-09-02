using D365FO.Core;
using D365FO.Core.Knowledge;
using Spectre.Console.Cli;

namespace D365FO.Cli.Commands.Knowledge;

// `d365fo bp-moniker` — the answer to "is this a real Best-Practice rule?", from names an
// installation declares rather than from a plausible-looking PascalCase guess.
//
// Every moniker has been guessed wrong at least once. BPErrorPrivilegeNotCoveredByDuty is real;
// BPCheckNamingConventions reads exactly like a rule and is not one. A suppression naming a
// moniker that does not exist suppresses nothing while looking entirely deliberate, so this
// never infers a name — it looks one up.

/// <summary><c>d365fo bp-moniker validate</c> — is this an exact, real moniker?</summary>
public sealed class BpMonikerValidateCommand : Command<BpMonikerValidateCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<MONIKER>")]
        [System.ComponentModel.Description("Moniker to check, e.g. BPErrorPrivilegeNotCoveredByDuty. Matched exactly — xppbp and the suppression reader are case-sensitive.")]
        public string Moniker { get; init; } = "";
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            BpMonikerAnswers.Validate(settings.Moniker));
}

/// <summary><c>d365fo bp-moniker search</c> — which rule covers this scenario?</summary>
public sealed class BpMonikerSearchCommand : Command<BpMonikerSearchCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<QUERY>")]
        [System.ComponentModel.Description("Words to look for. All of them must appear, in the name, the message or the description — so a scenario can be described in words the rule name does not contain.")]
        public string Query { get; init; } = "";

        [CommandOption("--limit <N>")]
        [System.ComponentModel.Description("Maximum matches (default 20).")]
        public int Limit { get; init; } = 20;

        [CommandOption("--canonical-only")]
        [System.ComponentModel.Description("Only real BP rules. Without it, rule-assembly strings that no rule set declares are listed too, flagged canonical:false.")]
        public bool CanonicalOnly { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            BpMonikerAnswers.Search(settings.Query, settings.Limit, settings.CanonicalOnly));
}

/// <summary><c>d365fo bp-moniker suppress</c> — render a `_BPSuppressions.xml` block.</summary>
public sealed class BpMonikerSuppressCommand : Command<BpMonikerSuppressCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandArgument(0, "<MONIKER>")]
        [System.ComponentModel.Description("Rule to suppress. Refused when the catalog does not know it.")]
        public string Moniker { get; init; } = "";

        [CommandOption("--path <URI>")]
        [System.ComponentModel.Description("dynamics:// URI of the element the rule fired on, e.g. dynamics://Table/MyTable/Method/validateWrite. Required — a suppression with no path suppresses the rule everywhere.")]
        public string? Path { get; init; }

        [CommandOption("--justification <TEXT>")]
        [System.ComponentModel.Description("Why the rule does not apply here. A reviewer reads this, so a placeholder is emitted when it is omitted.")]
        public string? Justification { get; init; }

        [CommandOption("--message <TEXT>")]
        [System.ComponentModel.Description("Exact message xppbp reported. Defaults to the catalog's message template for the rule.")]
        public string? Message { get; init; }

        [CommandOption("--severity <LEVEL>")]
        [System.ComponentModel.Description("Warning (default) or Error, matching what xppbp reported.")]
        public string Severity { get; init; } = "Warning";
    }

    public override int Execute(CommandContext ctx, Settings settings) =>
        RenderHelpers.Render(OutputMode.Resolve(settings.Output),
            BpMonikerAnswers.Suppress(settings.Moniker, settings.Path, settings.Message,
                settings.Justification, settings.Severity));
}

/// <summary><c>d365fo bp-moniker extract</c> — rebuild the catalog from this installation.</summary>
public sealed class BpMonikerExtractCommand : Command<BpMonikerExtractCommand.Settings>
{
    public sealed class Settings : D365OutputSettings
    {
        [CommandOption("--packages <PATH>")]
        [System.ComponentModel.Description("PackagesLocalDirectory to scan. Defaults to D365FO_PACKAGES_PATH.")]
        public string? Packages { get; init; }

        [CommandOption("--out <PATH>")]
        [System.ComponentModel.Description("Write the snapshot here. Point D365FO_BP_CATALOG_PATH at it to use a catalog matching this instance's D365FO version instead of the shipped one.")]
        public string? Out { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        var packages = settings.Packages ?? D365FoSettings.FromEnvironment().PackagesPath;
        if (string.IsNullOrWhiteSpace(packages))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.PackagesPathNotFound,
                "No packages path.", "Set D365FO_PACKAGES_PATH or pass --packages <PATH>."));
        }

        BpMonikerSnapshot snapshot;
        BpExtractionReport report;
        try
        {
            (snapshot, report) = BpCatalogExtractor.Extract(packages!);
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.SourceUnreadable, ex.Message));
        }

        string? written = null;
        if (!string.IsNullOrWhiteSpace(settings.Out))
        {
            var full = System.IO.Path.GetFullPath(settings.Out!);
            var dir = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(full, BpMonikerCatalog.ToJson(snapshot));
            written = full;
        }

        var warnings = new List<string>();
        if (report.CanonicalNames == 0)
        {
            warnings.Add("No AxRuleSet/*.xml declared a single moniker — nothing here can tell a real rule from a "
                       + "resource string. Check that --packages points at a PackagesLocalDirectory.");
        }
        if (report.NameOnly > 0)
        {
            warnings.Add($"{report.NameOnly} canonical moniker(s) ship no message text in this install. They are still "
                       + "real rules — absence of a description is not absence of a rule.");
        }

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            packagesPath = packages,
            capturedAt = snapshot.CapturedAt,
            total = snapshot.Monikers.Count,
            report,
            path = written,
            note = written is null
                ? "Nothing written — pass --out <PATH> to keep the snapshot."
                : $"Set {BpMonikerCatalog.PathEnvVar} to this path to use it.",
        }, warnings.Count > 0 ? warnings : null));
    }
}
