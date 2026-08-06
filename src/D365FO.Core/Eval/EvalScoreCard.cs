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
/// <see cref="RuntimeClean"/> is the L4 dimension (issue #160) and follows the same discipline
/// one level further out: it is <c>null</c> unless a SysTest run actually happened and its
/// results were read. No runner is wired to it yet — the platform's runner is
/// <c>SysTestConsole.exe</c> and emits its verdict as an XML document via <c>/xml:</c>, but
/// nothing in this repo parses that document, so nothing may claim a runtime verdict. The field
/// exists so the scoring surface has one shape rather than growing a second one later; a
/// scorecard where it is null means "not run", exactly as with <see cref="BuildClean"/>.
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
    int BuildErrors = 0,
    bool? RuntimeClean = null,
    int RuntimeFailures = 0);
