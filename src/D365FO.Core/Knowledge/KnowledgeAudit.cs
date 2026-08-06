// <copyright file="KnowledgeAudit.cs" company="d365fo-cli contributors">
// MIT
// </copyright>

using System.Text;
using System.Text.Json.Serialization;
using D365FO.Core.Validation;

namespace D365FO.Core.Knowledge;

/// <summary>One symbol-index hit: the canonical spelling plus every kind the name resolves to.</summary>
public sealed record KnowledgeSymbolHit(string Canonical, IReadOnlyList<string> Kinds);

/// <summary>
/// Minimal view of the symbol index the knowledge audit needs. Kept separate from
/// <see cref="IReferenceIndex"/> because the audit asks a different question — "is this the
/// AOT's spelling of a real element" rather than "does this generated code resolve" — and
/// because keeping it narrow lets the audit unit-test with a fake index, VM-free.
/// </summary>
public interface IKnowledgeSymbolLookup
{
    /// <summary>Case-insensitive element lookup; null when the name is in no AOT collection.</summary>
    KnowledgeSymbolHit? Resolve(string name);

    /// <summary>
    /// Weaker proof of existence: the name is not an indexed element of its own, but real AOT
    /// elements declare it as their base class or extended table. Real, just not indexable here.
    /// </summary>
    bool IsReferencedBase(string name);

    /// <summary>Does <paramref name="canonical"/> declare a method named <paramref name="member"/>?</summary>
    bool HasMember(string canonical, string member);
}

/// <summary>Why one reference failed.</summary>
public sealed record KnowledgeAuditFinding(KnowledgeRef Ref, string Status, string Detail)
{
    public const string UnknownType = "unknown-type";
    public const string UnknownMember = "unknown-member";
    public const string Casing = "casing";
}

/// <summary>Outcome of one audit pass.</summary>
public sealed record KnowledgeAuditResult(
    int Checked,
    int Resolved,
    int Allowed,
    IReadOnlyList<KnowledgeAuditFinding> Findings);

/// <summary>
/// The audited state of the corpus, captured on a machine with a full standard index and
/// committed so CI (which has none) can still refuse un-audited knowledge edits.
/// </summary>
public sealed record KnowledgeAuditSnapshot
{
    /// <summary>ISO timestamp of the capture run.</summary>
    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; init; } = "";

    /// <summary>Newest model-extract timestamp of the index the capture ran against.</summary>
    [JsonPropertyName("indexedAt")]
    public string IndexedAt { get; init; } = "";

    /// <summary>Every reference key that resolved cleanly, sorted.</summary>
    [JsonPropertyName("ok")]
    public List<string> Ok { get; init; } = [];
}

/// <summary>
/// Resolves every AOT reference extracted from <c>skills/_source</c> against the real symbol
/// index, so knowledge content is gated the same fail-closed way generated code is.
///
/// Port of upstream <c>d365fo-mcp-server</c>'s <c>knowledgeAudit.ts</c>. Pure by design: it
/// takes an <see cref="IKnowledgeSymbolLookup"/>, so it unit-tests against a fake index while
/// the CLI supplies either the real SQLite index (capture) or the committed snapshot (CI).
/// </summary>
public static class KnowledgeAudit
{
    /// <summary>
    /// Member checks only make sense for element kinds that own methods. A <c>Foo::bar</c>
    /// where Foo is an enum or EDT is a value reference (<c>NoYes::Yes</c>), not a call.
    /// </summary>
    private static readonly HashSet<string> MemberBearing = new(StringComparer.OrdinalIgnoreCase)
    {
        "class", "table", "interface", "map", "view", "data-entity", "form",
    };

    /// <summary>
    /// Namespaces that never appear in the symbol index by construction — .NET BCL and
    /// platform types reachable from X++ only when written fully qualified. Everything else
    /// must be a reviewed entry in <c>eval/knowledge-audit.allow.json</c>.
    /// </summary>
    private static readonly string[] DotNetPrefixes = ["System.", "Microsoft."];

    /// <summary>
    /// Canonical AppSuite/platform elements every real index carries. The live gate only
    /// means anything against the full standard index — a dev machine or CI often has a small
    /// fixture index, and auditing against that reports every standard symbol as unknown.
    /// </summary>
    public static readonly string[] Sentinels = ["CustTable", "InventDim", "RunBaseBatch"];

    /// <summary>True when <paramref name="lookup"/> resolves every <see cref="Sentinels"/> entry.</summary>
    public static bool IsFullStandardIndex(IKnowledgeSymbolLookup lookup)
    {
        try
        {
            return Sentinels.All(s => lookup.Resolve(s) is not null);
        }
        catch
        {
            return false; // unreadable or foreign schema — treat as no index
        }
    }

    /// <summary>Resolve <paramref name="refs"/>, honouring <paramref name="allow"/>.</summary>
    public static KnowledgeAuditResult Audit(
        IReadOnlyList<KnowledgeRef> refs,
        IKnowledgeSymbolLookup lookup,
        KnowledgeAuditAllow? allow = null)
    {
        var findings = new List<KnowledgeAuditFinding>();
        int resolved = 0, allowed = 0;

        foreach (var r in refs)
        {
            if (IsAllowed(r.Name, allow))
            {
                allowed++;
                continue;
            }

            // X++ lets an attribute be written without its `Attribute` suffix
            // ([DataContract] == [DataContractAttribute]), so both spellings resolve.
            var suffixed = r.Kind == KnowledgeRefKinds.Attribute ? r.Name + "Attribute" : null;
            var written = r.Name;
            var hit = lookup.Resolve(r.Name);
            if (hit is null && suffixed is not null)
            {
                hit = lookup.Resolve(suffixed);
                if (hit is not null) written = suffixed;
            }

            if (hit is null)
            {
                if (lookup.IsReferencedBase(r.Name) || (suffixed is not null && lookup.IsReferencedBase(suffixed)))
                {
                    resolved++;
                    continue;
                }
                findings.Add(new KnowledgeAuditFinding(
                    r, KnowledgeAuditFinding.UnknownType, $"\"{r.Name}\" does not exist in the symbol index"));
                continue;
            }

            if (!string.Equals(hit.Canonical, written, StringComparison.Ordinal))
            {
                // Casing is a defect but the type is real — keep checking the member.
                findings.Add(new KnowledgeAuditFinding(
                    r, KnowledgeAuditFinding.Casing, $"\"{written}\" is spelled \"{hit.Canonical}\" in the AOT"));
            }
            else
            {
                resolved++;
            }

            if (r.Member is not null && hit.Kinds.Any(MemberBearing.Contains) && !lookup.HasMember(hit.Canonical, r.Member))
            {
                findings.Add(new KnowledgeAuditFinding(
                    r, KnowledgeAuditFinding.UnknownMember, $"{hit.Canonical} has no method \"{r.Member}\""));
            }
        }

        return new KnowledgeAuditResult(refs.Count, resolved, allowed, findings);
    }

    private static bool IsAllowed(string name, KnowledgeAuditAllow? allow) =>
        DotNetPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal)) ||
        (allow is not null &&
            (allow.Symbols.ContainsKey(name) ||
             allow.Prefixes.Keys.Any(p => name.StartsWith(p, StringComparison.Ordinal))));

    /// <summary>Human-readable audit report, grouped by topic, worst first.</summary>
    public static string Render(KnowledgeAuditResult result)
    {
        var sb = new StringBuilder();
        sb.Append($"Knowledge audit: {result.Checked} reference(s) · {result.Resolved} resolved · ")
          .AppendLine($"{result.Allowed} allowlisted · {result.Findings.Count} defect(s)");
        if (result.Findings.Count == 0)
        {
            sb.AppendLine("OK — every named API/type in skills/_source resolves against the symbol index.");
            return sb.ToString();
        }

        foreach (var group in result.Findings.GroupBy(f => f.Ref.TopicId).OrderByDescending(g => g.Count()))
        {
            sb.AppendLine().AppendLine($"> {group.Key} ({group.Count()})");
            foreach (var f in group)
                sb.AppendLine($"   [{f.Status}] {f.Ref.Field} · {f.Ref.Kind} · {f.Detail}");
        }
        return sb.ToString();
    }

    /// <summary>Build the snapshot from a capture run: every key that came back clean.</summary>
    public static KnowledgeAuditSnapshot BuildSnapshot(
        IReadOnlyList<KnowledgeRef> refs,
        KnowledgeAuditResult result,
        string indexedAt,
        DateTimeOffset capturedAt)
    {
        var bad = result.Findings.Select(f => f.Ref.Key).ToHashSet(StringComparer.Ordinal);
        return new KnowledgeAuditSnapshot
        {
            // UtcDateTime, not the offset — "…Z" avoids a JSON-escaped "+00:00" in the diff.
            CapturedAt = capturedAt.UtcDateTime.ToString("o"),
            IndexedAt = indexedAt,
            Ok = refs.Select(r => r.Key).Where(k => !bad.Contains(k)).Distinct(StringComparer.Ordinal)
                     .OrderBy(k => k, StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>
    /// CI half of the gate: no symbol index available, so every reference must be covered by
    /// the committed snapshot or the allowlist. Editing knowledge therefore requires
    /// re-capturing the audit against a real index — knowledge cannot silently drift back to
    /// unverified. Returns the references that are not covered.
    /// </summary>
    public static IReadOnlyList<KnowledgeRef> VerifyAgainstSnapshot(
        IReadOnlyList<KnowledgeRef> refs,
        KnowledgeAuditSnapshot snapshot,
        KnowledgeAuditAllow? allow = null)
    {
        var ok = snapshot.Ok.ToHashSet(StringComparer.Ordinal);
        return refs.Where(r => !ok.Contains(r.Key) && !IsAllowed(r.Name, allow)).ToList();
    }

    /// <summary>
    /// Snapshot keys that no longer correspond to any extracted reference — dead entries left
    /// behind by a knowledge edit. Reported so the snapshot cannot rot into a rubber stamp.
    /// </summary>
    public static IReadOnlyList<string> StaleSnapshotKeys(
        IReadOnlyList<KnowledgeRef> refs,
        KnowledgeAuditSnapshot snapshot)
    {
        var live = refs.Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
        return snapshot.Ok.Where(k => !live.Contains(k)).ToList();
    }
}
