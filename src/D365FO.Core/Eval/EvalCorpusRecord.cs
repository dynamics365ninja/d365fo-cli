namespace D365FO.Core.Eval;

/// <summary>
/// One record per <c>(case_id, run_id)</c>, mirroring the sibling
/// d365fo-mcp-server repo's corpus schema (docs/AGENT_EVAL_LOOP.md §5) but
/// scoped to what this offline loop can check. <see cref="Classification"/>
/// is never auto-computed — it is whatever the runner (replay or agent)
/// wrote, matching that repo's "implementer records a hypothesis, improver
/// confirms" split: <c>TOOL_DEFECT | VALIDATOR_GAP | KNOWLEDGE_GAP |
/// MODEL_ERROR | ENV_FLAKE</c>, or null when untriaged.
/// </summary>
public sealed record EvalCorpusRecord(
    string RunId,
    string CaseId,
    int Tier,
    DateTimeOffset TimestampUtc,
    string Source,
    EvalScoreCard Score,
    string? Classification,
    string? Note);
