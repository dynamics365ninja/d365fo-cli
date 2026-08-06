// <copyright file="KnowledgeAuditStore.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text.Json;
using System.Text.Json.Serialization;

namespace D365FO.Core.Knowledge;

/// <summary>
/// The reviewed exceptions to the knowledge audit. Kept as data rather than code so every
/// exception is a justified, reviewable line in <c>eval/knowledge-audit.allow.json</c> instead
/// of a silent skip in a matcher.
/// </summary>
public sealed record KnowledgeAuditAllow
{
    /// <summary>
    /// <c>name → why it can never resolve</c>: kernel classes and enums that live in the
    /// binaries rather than PackagesLocalDirectory metadata XML, and names the corpus mentions
    /// only in order to say they do not exist.
    /// </summary>
    [JsonPropertyName("symbols")]
    public Dictionary<string, string> Symbols { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// <c>topic::field::rule → why</c>: examples that deliberately show the wrong pattern next
    /// to the right one, so they stay teachable. A pin that stops firing is reported as dead.
    /// </summary>
    [JsonPropertyName("examples")]
    public Dictionary<string, string> Examples { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Reads and writes the two committed artifacts of the knowledge audit: the reviewed
/// allowlist and the capture snapshot. Kept in Core so the CLI command and the CI gate in
/// the test suite load them exactly the same way.
/// </summary>
public static class KnowledgeAuditStore
{
    /// <summary>
    /// Load the allowlist. A missing file is an empty allowlist, not an error — the audit
    /// then reports everything.
    /// </summary>
    public static KnowledgeAuditAllow LoadAllow(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<KnowledgeAuditAllow>(File.ReadAllText(path), D365Json.Options) ?? new()
            : new();

    /// <summary>Load the capture snapshot, or null when none has been committed.</summary>
    public static KnowledgeAuditSnapshot? LoadSnapshot(string path) =>
        File.Exists(path)
            ? JsonSerializer.Deserialize<KnowledgeAuditSnapshot>(File.ReadAllText(path), D365Json.Options)
            : null;

    /// <summary>Write the snapshot, pretty-printed and newline-terminated so diffs stay reviewable.</summary>
    public static void SaveSnapshot(string path, KnowledgeAuditSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, D365Json.Pretty) + "\n");
    }
}
