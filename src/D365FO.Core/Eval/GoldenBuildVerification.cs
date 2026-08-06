using System.Text.Json;
using D365FO.Core.Validation;

namespace D365FO.Core.Eval;

/// <summary>Per-case compile verdict. <c>Skipped</c> and <c>Verified</c> are different answers and are never merged.</summary>
public static class BuildVerdict
{
    /// <summary>The compiler ran and reported no error against this case's artifacts.</summary>
    public const string Clean = "clean";

    /// <summary>The compiler ran and reported at least one error attributable to this case.</summary>
    public const string Errors = "errors";

    /// <summary>Nothing was compiled for this case (no reviewed golden, unknown root element, …).</summary>
    public const string Skipped = "skipped";
}

public sealed record GoldenBuildCaseVerdict(
    string CaseId,
    string Verdict,
    int Errors,
    int Warnings,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> Messages,
    string? SkipReason = null);

/// <summary>
/// The persisted L3 result set (<c>eval/golden-build-verification.json</c>) — the
/// equivalent of the sibling repo's <c>verify-goldens-build.ts</c> output. Committed,
/// because the whole point is that a machine without a D365FO installation can still
/// see which goldens last compiled, when, and against what.
/// </summary>
public sealed record GoldenBuildVerification(
    DateTimeOffset CapturedUtc,
    string Host,
    string PackagesRoot,
    string Compiler,
    string CompilerArgs,
    string ModelName,
    int Total,
    int Clean,
    int Failed,
    int Skipped,
    IReadOnlyList<GoldenBuildCaseVerdict> Cases)
{
    public static GoldenBuildVerification Build(
        string host,
        string packagesRoot,
        string compiler,
        string compilerArgs,
        string modelName,
        DateTimeOffset capturedUtc,
        IReadOnlyList<GoldenBuildCaseVerdict> cases) =>
        new(capturedUtc, host, packagesRoot, compiler, compilerArgs, modelName,
            Total: cases.Count,
            Clean: cases.Count(c => c.Verdict == BuildVerdict.Clean),
            Failed: cases.Count(c => c.Verdict == BuildVerdict.Errors),
            Skipped: cases.Count(c => c.Verdict == BuildVerdict.Skipped),
            Cases: cases);

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, D365Json.Serialize(this, indented: true));
    }

    public static GoldenBuildVerification? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<GoldenBuildVerification>(File.ReadAllText(path), D365Json.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Attributes compiler diagnostics back to the eval case whose golden produced the
/// object they name.
/// </summary>
/// <remarks>
/// A single compile covers the whole throwaway model, so the log has to be split
/// again per case. <see cref="XppcDiagnostic.Object"/> carries the AOT object name
/// the compiler blamed, which is the same name the provisioner wrote the file
/// under — that is the join key. A diagnostic naming no object, or naming one no
/// case provisioned, is <em>unattributed</em> rather than spread across every case:
/// blaming the wrong case would send the improver at innocent code.
/// </remarks>
public static class BuildVerdictAttribution
{
    public static (IReadOnlyList<GoldenBuildCaseVerdict> Cases, IReadOnlyList<XppcDiagnostic> Unattributed) Attribute(
        ProvisionedModel model,
        IReadOnlyList<EvalCase> cases,
        IReadOnlyList<XppcDiagnostic> diagnostics)
    {
        // AOT object name → the case that provisioned it.
        var owner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in model.Artifacts)
        {
            var name = Path.GetFileNameWithoutExtension(a.RelativePath);
            owner[name] = a.CaseId;
        }

        var byCase = model.Artifacts
            .Select(a => a.CaseId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, _ => new List<XppcDiagnostic>(), StringComparer.OrdinalIgnoreCase);

        var unattributed = new List<XppcDiagnostic>();
        foreach (var d in diagnostics)
        {
            if (d.Object is { Length: > 0 } obj && owner.TryGetValue(obj, out var caseId))
                byCase[caseId].Add(d);
            else
                unattributed.Add(d);
        }

        var skipReasons = model.Skipped
            .Select(s => s.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => string.Join("; ", g.Select(p => p[1].Trim())), StringComparer.OrdinalIgnoreCase);

        var verdicts = new List<GoldenBuildCaseVerdict>();
        foreach (var @case in cases.OrderBy(c => c.Id, StringComparer.Ordinal))
        {
            if (!byCase.TryGetValue(@case.Id, out var diags))
            {
                verdicts.Add(new GoldenBuildCaseVerdict(
                    @case.Id, BuildVerdict.Skipped, 0, 0, [], [],
                    skipReasons.TryGetValue(@case.Id, out var why) ? why : "nothing provisioned for this case"));
                continue;
            }

            var errors = diags.Where(d => d.Severity == "error").ToList();
            // "information" diagnostics are the TODO markers the scaffolders leave on
            // purpose; counting them as warnings would make every skeleton look defective.
            var warnings = diags.Count(d => d.Severity == "warning");

            verdicts.Add(new GoldenBuildCaseVerdict(
                CaseId: @case.Id,
                Verdict: errors.Count == 0 ? BuildVerdict.Clean : BuildVerdict.Errors,
                Errors: errors.Count,
                Warnings: warnings,
                // The rule id, not the message text, is the clustering key — messages
                // carry object names and line numbers that differ per occurrence.
                RuleIds: errors.Select(e => e.HintRule).Where(r => r is not null).Distinct(StringComparer.Ordinal).ToList()!,
                Messages: errors.Select(e => e.Message).Distinct(StringComparer.Ordinal).Take(10).ToList()));
        }

        return (verdicts, unattributed);
    }
}
