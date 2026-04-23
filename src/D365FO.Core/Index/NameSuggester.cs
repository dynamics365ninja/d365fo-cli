// <copyright file="NameSuggester.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;

namespace D365FO.Core.Index;

/// <summary>
/// Approximate name matcher used to emit "Did you mean …?" hints when a
/// lookup misses. Queries the index via its stock SearchX helpers, then
/// ranks the candidates by Levenshtein distance against the requested name.
/// </summary>
public static class NameSuggester
{
    public enum Kind
    {
        Class,
        Table,
        Edt,
        Enum,
        Form,
        Query,
        View,
        Entity,
        Report,
        Service,
        Role,
        Duty,
        Privilege,
    }

    public static IReadOnlyList<string> Suggest(MetadataRepository repo, Kind kind, string name, int limit = 5)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<string>();
        // First try a substring search; if nothing, try the first 3 chars as
        // a cheaper fallback (handles minor typos at the end).
        var primary = Fetch(repo, kind, name);
        if (primary.Count == 0 && name.Length >= 3)
        {
            primary = Fetch(repo, kind, name.Substring(0, 3));
        }
        if (primary.Count == 0) return Array.Empty<string>();

        return primary
            .Select(n => (name: n, dist: Distance(n, name)))
            .OrderBy(t => t.dist)
            .ThenBy(t => t.name, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(t => t.name)
            .ToList();
    }

    /// <summary>Format a "Did you mean" hint or return null when no suggestions.</summary>
    public static string? HintFor(MetadataRepository repo, Kind kind, string name, int limit = 5)
    {
        var names = Suggest(repo, kind, name, limit);
        return names.Count == 0 ? null : "Did you mean: " + string.Join(", ", names);
    }

    private static IReadOnlyList<string> Fetch(MetadataRepository repo, Kind kind, string needle) => kind switch
    {
        Kind.Class => repo.SearchClasses(needle, null, 50).Select(x => x.Name).ToList(),
        Kind.Table => repo.SearchTables(needle, null, 50).Select(x => x.Name).ToList(),
        Kind.Edt => repo.SearchEdts(needle, 50).Select(x => x.Name).ToList(),
        Kind.Enum => repo.SearchEnums(needle, 50).Select(x => x.Name).ToList(),
        Kind.Query => repo.SearchQueries(needle, 50).Select(x => x.Name).ToList(),
        Kind.View => repo.SearchViews(needle, 50).Select(x => x.Name).ToList(),
        Kind.Entity => repo.SearchDataEntities(needle, 50).Select(x => x.Name).ToList(),
        Kind.Report => repo.SearchReports(needle, 50).Select(x => x.Name).ToList(),
        Kind.Service => repo.SearchServices(needle, 50).Select(x => x.Name).ToList(),
        _ => Array.Empty<string>(),
    };

    /// <summary>Levenshtein distance (case-insensitive).</summary>
    public static int Distance(string a, string b)
    {
        a = a?.ToLowerInvariant() ?? string.Empty;
        b = b?.ToLowerInvariant() ?? string.Empty;
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
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min(curr[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + cost);
            }
            Array.Copy(curr, prev, b.Length + 1);
        }
        return prev[b.Length];
    }
}
