using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace D365FO.Core.Eval;

/// <summary>
/// Loads and validates <c>eval/cases/*.json</c> against the shape documented
/// in <c>eval/cases/schema.json</c>. Hand-rolled shape checks rather than a
/// JSON-Schema library — no such library is referenced anywhere else in this
/// repo, and the rules here are few enough to keep explicit.
/// </summary>
public static class EvalCaseCatalog
{
    private static readonly Regex IdPattern = new(@"^L([0-4])-[a-z0-9-]+$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Load every case file in <paramref name="casesDir"/> (skipping
    /// <c>schema.json</c>). Malformed files are collected as errors rather
    /// than thrown, so callers (e.g. <c>eval list</c>) can report a partial
    /// catalog alongside what's wrong with the rest.
    /// </summary>
    public static (IReadOnlyList<EvalCase> Cases, IReadOnlyList<string> Errors) LoadAll(string casesDir)
    {
        var cases = new List<EvalCase>();
        var errors = new List<string>();

        if (!Directory.Exists(casesDir))
        {
            errors.Add($"Cases directory not found: {casesDir}");
            return (cases, errors);
        }

        foreach (var file in Directory.GetFiles(casesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, "schema.json", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var raw = File.ReadAllText(file);
                var dto = JsonSerializer.Deserialize<EvalCaseFile>(raw, JsonOpts);
                if (dto is null)
                {
                    errors.Add($"{name}: empty or invalid JSON");
                    continue;
                }
                if (TryValidate(name, dto, out var evalCase, out var error))
                    cases.Add(evalCase!);
                else
                    errors.Add(error!);
            }
            catch (JsonException ex)
            {
                errors.Add($"{name}: invalid JSON — {ex.Message}");
            }
        }

        return (cases, errors);
    }

    public static EvalCase? Find(IReadOnlyList<EvalCase> cases, string id)
        => cases.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    private static bool TryValidate(string fileName, EvalCaseFile dto, out EvalCase? result, out string? error)
    {
        result = null;

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            error = $"{fileName}: 'id' is required";
            return false;
        }

        var match = IdPattern.Match(dto.Id);
        if (!match.Success)
        {
            error = $"{fileName}: id '{dto.Id}' must match ^L[0-4]-[a-z0-9-]+$";
            return false;
        }

        var tierFromId = int.Parse(match.Groups[1].Value);
        if (dto.Tier != tierFromId)
        {
            error = $"{fileName}: tier {dto.Tier} does not match id prefix L{tierFromId}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.Title))
        {
            error = $"{fileName}: 'title' is required";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.Instruction))
        {
            error = $"{fileName}: 'instruction' is required";
            return false;
        }

        if (dto.TargetArtifactTypes is null || dto.TargetArtifactTypes.Length == 0)
        {
            error = $"{fileName}: 'target_artifact_types' must be non-empty";
            return false;
        }

        if (string.IsNullOrWhiteSpace(dto.GoldenPath))
        {
            error = $"{fileName}: 'golden_path' is required";
            return false;
        }

        result = new EvalCase(
            Id: dto.Id!,
            Title: dto.Title!,
            Tier: dto.Tier,
            Instruction: dto.Instruction!,
            CanonicalArgs: dto.CanonicalArgs,
            TargetArtifactTypes: dto.TargetArtifactTypes!,
            GoldenPath: dto.GoldenPath!,
            Tags: dto.Tags ?? Array.Empty<string>(),
            Ignore: dto.Ignore ?? Array.Empty<string>(),
            RequiresFixtureIndex: dto.RequiresFixtureIndex,
            GoldenPending: dto.GoldenPending);
        error = null;
        return true;
    }

    private sealed class EvalCaseFile
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("tier")] public int Tier { get; set; } = -1;
        [JsonPropertyName("instruction")] public string? Instruction { get; set; }
        [JsonPropertyName("canonical_args")] public string[]? CanonicalArgs { get; set; }
        [JsonPropertyName("target_artifact_types")] public string[]? TargetArtifactTypes { get; set; }
        [JsonPropertyName("golden_path")] public string? GoldenPath { get; set; }
        [JsonPropertyName("tags")] public string[]? Tags { get; set; }
        [JsonPropertyName("ignore")] public string[]? Ignore { get; set; }
        [JsonPropertyName("requires_fixture_index")] public bool RequiresFixtureIndex { get; set; }
        [JsonPropertyName("golden_pending")] public bool GoldenPending { get; set; }
    }
}
