namespace D365FO.Core.Eval;

/// <summary>
/// Result of scoring one case run.
/// </summary>
/// <remarks>
/// <para>
/// The first four dimensions are what the fully offline grounding chain
/// (<c>generate</c> → <c>validate xpp</c> → <c>validate references</c> → golden
/// diff) can check anywhere. <see cref="BuildClean"/> is the L3 dimension and is
/// only ever populated by <c>eval verify-build</c> on a Windows host with a real
/// D365FO installation — everywhere else it stays <c>null</c>, which reads as
/// "not evaluated", never as "passed". Reporting a compile verdict nobody
/// collected would be exactly the confident lie the triage rubric warns about.
/// </para>
/// <para>
/// There is still no runtime/SysTest dimension: no oracle produces one yet.
/// </para>
/// </remarks>
public sealed record EvalScoreCard(
    bool? XppClean,
    int XppErrors,
    bool? ReferencesClean,
    int ReferenceErrors,
    bool GoldenMatch,
    XmlGoldenDiff GoldenDiff,
    bool? BuildClean = null,
    int BuildErrors = 0);
