// <copyright file="LabelInliner.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using D365FO.Core.Index;

namespace D365FO.Core.Guardrails;

/// <summary>
/// Post-processes a rendered object by replacing <c>@File12345</c> label
/// tokens with their resolved text (first available language). Works on the
/// JSON tree so it composes with any CLI command's response shape.
/// </summary>
public static class LabelInliner
{
    private static readonly Regex TokenRegex = new(
        @"@([A-Za-z]+)(\d+)",
        RegexOptions.Compiled);

    /// <summary>
    /// Walk <paramref name="node"/> in place and rewrite every
    /// <c>@File+Id</c> token inside string values to
    /// <c>text [[@File12345]]</c>.
    /// </summary>
    public static void WalkAndReplace(JsonNode node, MetadataRepository repo, IReadOnlyCollection<string> languages)
    {
        if (node is null) return;
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Walk(node, repo, languages, cache);
    }

    private static void Walk(JsonNode node, MetadataRepository repo, IReadOnlyCollection<string> languages, IDictionary<string, string?> cache)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var kv in obj)
                {
                    if (kv.Value is null) continue;
                    if (kv.Value is JsonValue v && v.TryGetValue(out string? s) && s is not null)
                    {
                        var replaced = ReplaceTokens(s, repo, languages, cache);
                        if (!ReferenceEquals(replaced, s))
                        {
                            obj[kv.Key] = JsonValue.Create(replaced);
                        }
                    }
                    else
                    {
                        Walk(kv.Value, repo, languages, cache);
                    }
                }
                break;
            case JsonArray arr:
                for (int i = 0; i < arr.Count; i++)
                {
                    var item = arr[i];
                    if (item is null) continue;
                    if (item is JsonValue v && v.TryGetValue(out string? s) && s is not null)
                    {
                        var replaced = ReplaceTokens(s, repo, languages, cache);
                        if (!ReferenceEquals(replaced, s))
                        {
                            arr[i] = JsonValue.Create(replaced);
                        }
                    }
                    else
                    {
                        Walk(item, repo, languages, cache);
                    }
                }
                break;
        }
    }

    private static string ReplaceTokens(string input, MetadataRepository repo, IReadOnlyCollection<string> languages, IDictionary<string, string?> cache)
    {
        if (!input.Contains('@')) return input;
        return TokenRegex.Replace(input, match =>
        {
            var token = match.Value;
            if (!cache.TryGetValue(token, out var text))
            {
                var matches = repo.ResolveLabel(token, languages);
                text = null;
                foreach (var lang in languages)
                {
                    foreach (var hit in matches)
                    {
                        if (string.Equals(hit.Language, lang, StringComparison.OrdinalIgnoreCase))
                        {
                            text = hit.Value;
                            break;
                        }
                    }
                    if (text is not null) break;
                }
                if (text is null && matches.Count > 0) text = matches[0].Value;
                cache[token] = text;
            }
            return string.IsNullOrEmpty(text) ? token : $"{text} [[{token}]]";
        });
    }
}
