namespace D365FO.Core.Knowledge;

/// <summary>
/// The three questions asked of the Best-Practice moniker catalog — is this name real, which
/// rule covers this scenario, and what does the suppression block look like — answered once for
/// both surfaces.
/// </summary>
/// <remarks>
/// The answers carry judgement that is easy to get wrong twice: an empty catalog must not
/// answer "not a moniker" (confidently wrong is worse than no answer), a case-only miss is
/// answered with the right casing rather than a flat no, and a suppression naming an unknown
/// moniker is refused rather than rendered. Keeping that in one place is why the MCP tool can
/// have it without a second implementation drifting from this one.
/// </remarks>
public static class BpMonikerAnswers
{
    /// <summary>Is this an exact, real moniker? Matched case-sensitively, as xppbp is.</summary>
    public static ToolResult<object> Validate(string moniker)
    {
        if (!BpMonikerCatalog.IsPopulated)
        {
            // An empty catalog cannot refute anything. Saying "not a moniker" here would be the
            // worst possible answer: confidently wrong, and indistinguishable from a real verdict.
            return ToolResult<object>.Fail(D365FoErrorCodes.NoIndex,
                "The BP moniker catalog is empty, so this cannot say whether the moniker is real.",
                $"Run `d365fo bp-moniker extract` on a machine with D365FO_PACKAGES_PATH set, or point {BpMonikerCatalog.PathEnvVar} at a captured snapshot.");
        }

        var found = BpMonikerCatalog.Find(moniker);
        if (found is not null)
        {
            return ToolResult<object>.Success(new
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
            });
        }

        var caseVariants = BpMonikerCatalog.CaseVariants(moniker);
        var near = BpMonikerCatalog.Search(moniker, 5);

        return ToolResult<object>.Success(new
        {
            moniker,
            valid = false,
            caseVariants = caseVariants.Count > 0 ? caseVariants : null,
            didYouMean = near.Count > 0 ? near.Select(m => m.Name).ToArray() : null,
            note = caseVariants.Count > 0
                ? "The name exists with different casing. The moniker is matched exactly, so the casing above is the one to use."
                : "No rule set in the indexed installation declares this name. Do not suppress it — a suppression naming a "
                  + "moniker that does not exist suppresses nothing.",
        });
    }

    /// <summary>Which rule covers this scenario? Every word must appear in name, message or description.</summary>
    public static ToolResult<object> Search(string query, int limit = 20, bool canonicalOnly = false)
    {
        if (!BpMonikerCatalog.IsPopulated)
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.NoIndex,
                "The BP moniker catalog is empty.",
                $"Run `d365fo bp-moniker extract`, or point {BpMonikerCatalog.PathEnvVar} at a captured snapshot.");
        }

        var hits = BpMonikerCatalog.Search(query, limit);
        if (canonicalOnly) hits = hits.Where(m => m.Canonical).ToList();

        return ToolResult<object>.Success(new
        {
            query,
            count = hits.Count,
            items = hits.Select(m => new { m.Name, m.Canonical, m.Message, m.Description }),
        });
    }

    /// <summary>Render a <c>_BPSuppressions.xml</c> <c>&lt;Diagnostic&gt;</c> block for a known moniker.</summary>
    public static ToolResult<object> Suppress(
        string moniker, string? path, string? message = null, string? justification = null, string severity = "Warning")
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return ToolResult<object>.Fail(D365FoErrorCodes.BadInput,
                "A dynamics:// path is required.",
                "It is the dynamics:// URI xppbp reported the finding against — without it the block "
                + "does not identify what is being suppressed.");
        }

        var known = BpMonikerCatalog.Find(moniker);
        if (BpMonikerCatalog.IsPopulated && known is null)
        {
            var variants = BpMonikerCatalog.CaseVariants(moniker);
            return ToolResult<object>.Fail(D365FoErrorCodes.TopicNotFound,
                $"'{moniker}' is not a moniker any indexed rule set declares.",
                variants.Count > 0
                    ? $"The name exists as: {string.Join(", ", variants)}. Monikers are matched exactly."
                    : "Find the right one with `d365fo bp-moniker search <words>`. Suppressing a name that does not exist suppresses nothing.");
        }

        var block = BpMonikerCatalog.SuppressionBlock(moniker, path!, message, justification, severity);

        return ToolResult<object>.Success(new
        {
            moniker,
            canonical = known?.Canonical,
            block,
            file = "<Model>/<Model>/AxIgnoreDiagnosticList/<Model>_BPSuppressions.xml",
            placement = "Add the block inside <IgnoreDiagnostics><Items>. The file is an AOT object like any "
                      + "other — it belongs to the model whose code raised the finding.",
        },
        justification is null
            ? new List<string> { "No justification given; the block carries a TODO placeholder that a reviewer will see." }
            : null);
    }
}
