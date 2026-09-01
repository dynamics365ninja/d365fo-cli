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

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (!BpMonikerCatalog.IsPopulated)
        {
            // An empty catalog cannot refute anything. Saying "not a moniker" here would be the
            // worst possible answer: confidently wrong, and indistinguishable from a real verdict.
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.NoIndex,
                "The BP moniker catalog is empty, so this cannot say whether the moniker is real.",
                $"Run `d365fo bp-moniker extract` on a machine with D365FO_PACKAGES_PATH set, or point {BpMonikerCatalog.PathEnvVar} at a captured snapshot."));
        }

        var found = BpMonikerCatalog.Find(settings.Moniker);
        if (found is not null)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                moniker = found.Name,
                valid = true,
                canonical = found.Canonical,
                found.Message,
                found.Description,
                note = found.Canonical
                    ? null
                    : "This string ships in a rule assembly but NO rule set declares it — it is not a BP rule, "
                      + "and suppressing it will have no effect.",
            }));
        }

        var caseVariants = BpMonikerCatalog.CaseVariants(settings.Moniker);
        var near = BpMonikerCatalog.Search(settings.Moniker, 5);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            moniker = settings.Moniker,
            valid = false,
            caseVariants = caseVariants.Count > 0 ? caseVariants : null,
            didYouMean = near.Count > 0 ? near.Select(m => m.Name).ToArray() : null,
            note = caseVariants.Count > 0
                ? "The name exists with different casing. The moniker is matched exactly, so the casing above is the one to use."
                : "No rule set in the indexed installation declares this name. Do not suppress it — a suppression naming a "
                  + "moniker that does not exist suppresses nothing.",
        }));
    }
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

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (!BpMonikerCatalog.IsPopulated)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.NoIndex,
                "The BP moniker catalog is empty.",
                $"Run `d365fo bp-moniker extract`, or point {BpMonikerCatalog.PathEnvVar} at a captured snapshot."));
        }

        var hits = BpMonikerCatalog.Search(settings.Query, settings.Limit);
        if (settings.CanonicalOnly) hits = hits.Where(m => m.Canonical).ToList();

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            query = settings.Query,
            count = hits.Count,
            items = hits.Select(m => new { m.Name, m.Canonical, m.Message, m.Description }),
        }));
    }
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

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Path))
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "--path is required.",
                "It is the dynamics:// URI xppbp reported the finding against — without it the block "
                + "does not identify what is being suppressed."));
        }

        var known = BpMonikerCatalog.Find(settings.Moniker);
        if (BpMonikerCatalog.IsPopulated && known is null)
        {
            var variants = BpMonikerCatalog.CaseVariants(settings.Moniker);
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                $"'{settings.Moniker}' is not a moniker any indexed rule set declares.",
                variants.Count > 0
                    ? $"The name exists as: {string.Join(", ", variants)}. Monikers are matched exactly."
                    : "Find the right one with `d365fo bp-moniker search <words>`. Suppressing a name that does not exist suppresses nothing."));
        }

        var block = BpMonikerCatalog.SuppressionBlock(
            settings.Moniker, settings.Path!, settings.Message, settings.Justification, settings.Severity);

        return RenderHelpers.Render(kind, ToolResult<object>.Success(new
        {
            moniker = settings.Moniker,
            canonical = known?.Canonical,
            block,
            file = "<Model>/<Model>/AxIgnoreDiagnosticList/<Model>_BPSuppressions.xml",
            placement = "Add the block inside <IgnoreDiagnostics><Items>. The file is an AOT object like any "
                      + "other — it belongs to the model whose code raised the finding.",
        },
        settings.Justification is null
            ? new List<string> { "No --justification given; the block carries a TODO placeholder that a reviewer will see." }
            : null));
    }
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
