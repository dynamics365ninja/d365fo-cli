namespace D365FO.Core.Eval;

/// <summary>
/// One entry in the eval catalog (<c>eval/cases/&lt;id&gt;.json</c>). See
/// docs/AGENT_EVAL_LOOP.md for the full case-authoring contract.
/// </summary>
public sealed record EvalCase(
    string Id,
    string Title,
    int Tier,
    string Instruction,
    IReadOnlyList<string>? CanonicalArgs,
    IReadOnlyList<string> TargetArtifactTypes,
    string GoldenPath,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Ignore,
    bool RequiresFixtureIndex,
    bool GoldenPending);
