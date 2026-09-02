using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// Answers "which forms look like this one?" from the index: the pattern histogram with no
/// filter, the forms matching a pattern/table, or the peers of a reference form.
/// </summary>
/// <remarks>
/// This is the mined half of the form-pattern toolkit — the curated half is
/// <c>FormPatternCatalog</c>'s specs. It lives in Core because both surfaces need it and only
/// one had it: <c>d365fo find form-patterns</c> did the mining, while the MCP
/// <c>object_patterns</c> tool offered spec, validate and repair and told the agent to go and
/// run the CLI for the rest.
/// </remarks>
public static class FormPatternMiner
{
    /// <param name="Mode">summary (no filter) | filter | similar.</param>
    /// <param name="TotalForms">Summary mode only: how many indexed forms the histogram covers.</param>
    /// <param name="Patterns">Summary mode only: the histogram.</param>
    /// <param name="Hint">Summary mode only: how to narrow the answer.</param>
    /// <param name="Filter">Filter/similar mode: what the list was narrowed by.</param>
    /// <param name="Reference">Similar mode only: the form the peers were derived from.</param>
    /// <param name="Count">Filter/similar mode: how many forms matched.</param>
    /// <param name="Items">Filter/similar mode: the matching forms.</param>
    public sealed record Result(
        string Mode,
        long? TotalForms = null,
        IReadOnlyList<FormPatternSummary>? Patterns = null,
        string? Hint = null,
        Filters? Filter = null,
        ReferenceForm? Reference = null,
        int? Count = null,
        IReadOnlyList<FormPatternRow>? Items = null);

    public sealed record Filters(string? Pattern, string? Table, string? Model);

    public sealed record ReferenceForm(string Name, string? Model, string? Pattern, string? PrimaryTable);

    /// <summary>The reference form named by <c>similarTo</c> is not in the index.</summary>
    public sealed class ReferenceNotFoundException(string formName)
        : Exception($"Form '{formName}' is not in the index.")
    {
        public string FormName { get; } = formName;
    }

    /// <exception cref="ReferenceNotFoundException"><paramref name="similarTo"/> is not indexed.</exception>
    public static Result Analyze(
        MetadataRepository repo,
        string? pattern = null,
        string? table = null,
        string? similarTo = null,
        string? model = null,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(repo);

        ReferenceForm? reference = null;
        if (!string.IsNullOrWhiteSpace(similarTo))
        {
            var refForm = repo.GetForm(similarTo)
                ?? throw new ReferenceNotFoundException(similarTo);

            // Pattern lives on Forms.Pattern but FormDetails doesn't surface it yet. Re-use the
            // analyser query with the model + name to pull the reference row directly.
            var enriched = repo.FindFormPatterns(model: refForm.Form.Model, limit: int.MaxValue)
                .FirstOrDefault(r => string.Equals(r.Name, refForm.Form.Name, StringComparison.OrdinalIgnoreCase));
            pattern ??= enriched?.Pattern;
            table ??= enriched?.PrimaryTable
                  ?? refForm.DataSources.Select(d => d.TableName).FirstOrDefault(t => !string.IsNullOrEmpty(t));
            reference = new ReferenceForm(refForm.Form.Name, refForm.Form.Model, pattern, table);
        }

        // No filters => show the histogram so the caller can pick a pattern.
        if (string.IsNullOrWhiteSpace(pattern) && string.IsNullOrWhiteSpace(table))
        {
            var summary = repo.SummarizeFormPatterns();
            return new Result(
                "summary",
                TotalForms: summary.Sum(s => s.Count),
                Patterns: summary,
                Hint: "Pass a pattern, a table, or a reference form to drill in.");
        }

        var rows = repo.FindFormPatterns(pattern, table, model, limit);
        // When a reference form was given, drop the reference form itself.
        if (reference is not null && !string.IsNullOrEmpty(similarTo))
            rows = rows.Where(r => !string.Equals(r.Name, similarTo, StringComparison.OrdinalIgnoreCase)).ToList();

        return new Result(
            reference is null ? "filter" : "similar",
            Filter: new Filters(pattern, table, model),
            Reference: reference,
            Count: rows.Count,
            Items: rows);
    }
}
