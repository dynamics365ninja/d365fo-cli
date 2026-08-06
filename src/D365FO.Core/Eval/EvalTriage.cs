namespace D365FO.Core.Eval;

/// <summary>
/// Turns a scorecard into the <em>hypothesis</em> half of the triage rubric
/// (docs/AGENT_EVAL_LOOP.md §9). The runner records a hypothesis; the
/// eval-improver confirms it by reproducing the defect as a failing test
/// before touching any source — this class never claims more than the
/// evidence supports.
/// </summary>
/// <remarks>
/// <para>
/// The classification depends on <em>who drove the CLI</em>, which is why
/// <see cref="EvalCorpusRecord.Source"/> is an input:
/// </para>
/// <list type="bullet">
/// <item><description><b>replay</b> — canonical args, no agent, no model in the
/// loop. <c>MODEL_ERROR</c> is impossible by construction, so a golden mismatch
/// can only be the tool changing its output, and a validator complaint about
/// output that <em>does</em> match a reviewed golden can only be the validator
/// being wrong about reviewed-correct code.</description></item>
/// <item><description><b>agent</b> — a failure is either a real tool defect or
/// the agent misusing a correct tool, and nothing in the scorecard distinguishes
/// them. This returns null rather than guessing: the eval-runner supplies its
/// own <c>--hypothesis</c>, and an unclassified record is honest where an
/// invented one would send the improver at the wrong file.</description></item>
/// </list>
/// </remarks>
public static class EvalTriage
{
    public const string ToolDefect = "TOOL_DEFECT";
    public const string ValidatorGap = "VALIDATOR_GAP";
    public const string KnowledgeGap = "KNOWLEDGE_GAP";
    public const string ModelError = "MODEL_ERROR";
    public const string EnvFlake = "ENV_FLAKE";

    /// <summary>Classes the eval-improver can act on; the rest are noise or prompt work.</summary>
    public static readonly IReadOnlyList<string> Actionable = new[] { ToolDefect, ValidatorGap, KnowledgeGap };

    /// <summary>Placeholder used for grouping records that carry no classification at all.</summary>
    public const string Unclassified = "UNCLASSIFIED";

    /// <summary>
    /// A case tagged this way generates an artifact whose body legitimately
    /// references a sibling artifact or a standard system object that a minimal
    /// offline fixture index cannot contain, so <c>referencesClean: false</c> is
    /// the expected result of scoring one artifact in isolation — not a defect.
    /// </summary>
    public const string KnownReferenceGapTag = "known-reference-gap";

    /// <summary>
    /// The runner's hypothesis for one scored run, plus the sentence that
    /// justifies it. Both null when the run is clean, or when the evidence does
    /// not single out one class.
    /// </summary>
    public static (string? Classification, string? Note) Hypothesize(EvalCase @case, EvalScoreCard score, string source)
    {
        var expectedReferenceGap =
            @case.Tags.Contains(KnownReferenceGapTag, StringComparer.OrdinalIgnoreCase);

        if (!string.Equals(source, "replay", StringComparison.OrdinalIgnoreCase))
        {
            // An agent run that failed is TOOL_DEFECT or MODEL_ERROR and the
            // scorecard cannot tell which. Say so instead of picking one.
            return (null, null);
        }

        if (!score.GoldenMatch)
        {
            var d = score.GoldenDiff;
            return (ToolDefect,
                $"Replay of canonical_args diverged from the reviewed golden: " +
                $"{d.Missing.Count} missing, {d.Extra.Count} extra, {d.Changed.Count} changed. " +
                "No agent was involved, so the scaffolder's output changed.");
        }

        // Below here the artifact matches a golden a human reviewed, so the
        // artifact is by definition the intended output — a validator that
        // rejects it is the thing that is wrong.
        if (score.XppClean == false)
        {
            return (ValidatorGap,
                $"Output matches the reviewed golden, yet `validate xpp` reports {score.XppErrors} error(s): " +
                "the BP rule fires on output this repo considers correct.");
        }

        if (score.ReferencesClean == false)
        {
            if (expectedReferenceGap)
            {
                return (null,
                    $"{score.ReferenceErrors} unresolved reference(s), expected for a `{KnownReferenceGapTag}` case: " +
                    "the artifact names a sibling or standard object the mini fixture index does not contain.");
            }

            return (ValidatorGap,
                $"Output matches the reviewed golden, yet `validate references` reports {score.ReferenceErrors} " +
                "unresolved reference(s) and the case is not tagged " + KnownReferenceGapTag + ".");
        }

        return (null, null);
    }

    /// <summary>Normalises a caller-supplied class, or null when it is not one of the five.</summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToUpperInvariant().Replace('-', '_');
        return v is ToolDefect or ValidatorGap or KnowledgeGap or ModelError or EnvFlake ? v : null;
    }
}
