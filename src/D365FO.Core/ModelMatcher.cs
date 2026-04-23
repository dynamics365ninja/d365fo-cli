using System.Text.RegularExpressions;

namespace D365FO.Core;

/// <summary>
/// Matches model names against a list of patterns. Supports:
///   - Exact names (case-insensitive): <c>AslCore</c>
///   - Glob wildcards: <c>*</c> (any chars), <c>?</c> (single char). Example: <c>Asl*</c>, <c>ISV_*</c>.
///   - Negation: prefix a pattern with <c>!</c> to exclude matches that earlier patterns would include.
/// Patterns are evaluated in order; the last matching pattern wins. An empty pattern
/// list never matches.
/// </summary>
public sealed class ModelMatcher
{
    private readonly IReadOnlyList<(Regex Rx, bool Negate)> _patterns;

    public ModelMatcher(IEnumerable<string> patterns)
    {
        var list = new List<(Regex, bool)>();
        foreach (var raw in patterns ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var p = raw.Trim();
            var negate = p.StartsWith('!');
            if (negate) p = p[1..];
            if (p.Length == 0) continue;
            list.Add((GlobToRegex(p), negate));
        }
        _patterns = list;
    }

    public bool IsEmpty => _patterns.Count == 0;

    public bool IsMatch(string modelName)
    {
        if (string.IsNullOrEmpty(modelName) || _patterns.Count == 0) return false;
        bool matched = false;
        foreach (var (rx, negate) in _patterns)
        {
            if (rx.IsMatch(modelName)) matched = !negate;
        }
        return matched;
    }

    private static Regex GlobToRegex(string glob)
    {
        var sb = new System.Text.StringBuilder("^");
        foreach (var c in glob)
        {
            switch (c)
            {
                case '*': sb.Append(".*"); break;
                case '?': sb.Append('.'); break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
        sb.Append('$');
        return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }
}
