using D365FO.Core.Index;

namespace D365FO.Core;

/// <summary>
/// Heuristic EDT suggester. Ranks indexed <c>Edts</c> by name similarity to a
/// target field name. Confidence bands:
/// <list type="bullet">
///   <item><c>1.00</c> — exact match (case-insensitive).</item>
///   <item><c>≥ 0.80</c> — fieldName equals EDT without common suffixes (Id / No / Num / Amount) or vice versa.</item>
///   <item><c>≥ 0.60</c> — fieldName contains EDT name as a whole token, or vice versa.</item>
///   <item><c>≥ 0.40</c> — fuzzy Damerau–Levenshtein below half the length.</item>
/// </list>
/// Falls back to <c>null</c> when no candidate clears <c>0.40</c>.
/// </summary>
public static class EdtSuggester
{
    private static readonly string[] NoiseSuffixes =
    {
        "Id", "Ids", "No", "Num", "Number", "Name",
        "Account", "Amount", "Code", "Key", "Value",
    };

    public readonly record struct Suggestion(EdtInfo Edt, double Confidence, string Reason);

    public static IReadOnlyList<Suggestion> Suggest(
        MetadataRepository repo, string fieldName, int limit = 5)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(fieldName)) return Array.Empty<Suggestion>();
        if (limit <= 0) limit = 5;

        var target = fieldName.Trim();
        var stripped = Strip(target);

        // Over-fetch then score. Single LIKE scan on Name — Edts table is small.
        var candidates = repo.SearchEdts(stripped.Length >= 3 ? stripped : target, Math.Max(limit * 20, 100));
        if (candidates.Count == 0 && stripped.Length >= 3)
            candidates = repo.SearchEdts(stripped, Math.Max(limit * 20, 100));

        var scored = new List<Suggestion>();
        foreach (var edt in candidates)
        {
            var (score, reason) = Score(target, stripped, edt.Name);
            if (score >= 0.40)
                scored.Add(new Suggestion(edt, Math.Round(score, 2), reason));
        }
        return scored
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.Edt.Name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToList();
    }

    private static (double Score, string Reason) Score(string target, string stripped, string edtName)
    {
        if (string.Equals(target, edtName, StringComparison.OrdinalIgnoreCase))
            return (1.0, "exact match");

        if (string.Equals(stripped, edtName, StringComparison.OrdinalIgnoreCase))
            return (0.90, "exact match after stripping common suffix");

        var edtStripped = Strip(edtName);
        if (string.Equals(stripped, edtStripped, StringComparison.OrdinalIgnoreCase))
            return (0.85, "roots match after stripping suffixes");

        if (target.Contains(edtName, StringComparison.OrdinalIgnoreCase) ||
            edtName.Contains(target, StringComparison.OrdinalIgnoreCase))
            return (0.70, "whole-name token match");

        if (stripped.Length >= 3 &&
            (edtName.Contains(stripped, StringComparison.OrdinalIgnoreCase) ||
             stripped.Contains(edtName, StringComparison.OrdinalIgnoreCase)))
            return (0.60, "root-token match");

        var distance = LevenshteinDistance(target.ToLowerInvariant(), edtName.ToLowerInvariant());
        var max = Math.Max(target.Length, edtName.Length);
        if (max == 0) return (0, "empty");
        var similarity = 1.0 - (double)distance / max;
        if (similarity >= 0.55)
            return (0.40 + (similarity - 0.55) * 0.8, $"fuzzy match (distance {distance}, similarity {similarity:0.00})");

        return (0, "no match");
    }

    internal static string Strip(string s)
    {
        foreach (var suffix in NoiseSuffixes)
        {
            if (s.Length > suffix.Length &&
                s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return s[..^suffix.Length];
        }
        return s;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
