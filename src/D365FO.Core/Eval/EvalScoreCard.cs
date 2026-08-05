namespace D365FO.Core.Eval;

/// <summary>
/// Result of scoring one case run. Smaller than the sibling
/// d365fo-mcp-server repo's scorecard by design: there is no VM/compiler
/// round-trip here, so there is no <c>build</c>/<c>bp_clean</c> dimension to
/// report, and no runtime/SysTest oracle — only what the offline grounding
/// chain (<c>generate</c> → <c>validate xpp</c> → <c>validate references</c>
/// → golden diff) can actually check.
/// </summary>
public sealed record EvalScoreCard(
    bool? XppClean,
    int XppErrors,
    bool? ReferencesClean,
    int ReferenceErrors,
    bool GoldenMatch,
    XmlGoldenDiff GoldenDiff);
