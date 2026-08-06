using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D365FO.Core.Metadata;

/// <summary>One MetaModel type's serialization contract.</summary>
/// <param name="Name">Contract (and element) name, e.g. <c>AxTable</c>.</param>
/// <param name="Namespace">XML namespace the contract declares; empty for most types.</param>
/// <param name="IsAbstract">Type cannot be instantiated — the file must pin a concrete <c>i:type</c>.</param>
/// <param name="Members">Member names in the exact order the serializer reads and writes them.</param>
/// <param name="EnumOf">Members whose value is constrained to an enum, keyed by member name.</param>
public sealed record MetadataContract(
    string Name,
    string Namespace,
    bool IsAbstract,
    string? BaseType,
    IReadOnlyList<string> Members,
    IReadOnlyDictionary<string, string> EnumOf)
{
    private Dictionary<string, int>? _index;

    /// <summary>The enum constraining <paramref name="member"/>, or null when it is free text.</summary>
    public string? EnumFor(string member)
        => EnumOf.TryGetValue(member, out var e) ? e : null;

    /// <summary>Position of <paramref name="member"/> in the contract, or -1 when the type has no such member.</summary>
    public int IndexOf(string member)
    {
        _index ??= BuildIndex(Members);
        return _index.TryGetValue(member, out var i) ? i : -1;
    }

    private static Dictionary<string, int> BuildIndex(IReadOnlyList<string> members)
    {
        var map = new Dictionary<string, int>(members.Count, StringComparer.Ordinal);
        for (var i = 0; i < members.Count; i++) map[members[i]] = i;
        return map;
    }
}

/// <summary>
/// The AOT serialization contracts, as declared by <c>Microsoft.Dynamics.AX.Metadata.dll</c>:
/// every MetaModel type's XML namespace and the exact order its members serialize in.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "valid AOT XML" checkable offline. The on-disk format is DataContract,
/// and <c>DataContractSerializer</c> matches elements in contract order: an element that is
/// out of order, or that the type does not declare, is <em>silently ignored</em>. Nothing
/// fails — the file parses, the validators pass, and the property is simply gone. That is how
/// a generated query lost its <c>JoinMode</c> (turning an inner join into a cross join) while
/// every check this repo had reported success.
/// </para>
/// <para>
/// Regenerate with <c>scripts/emit-metadata-contracts.ps1</c> on a machine that has the
/// metadata assemblies; the JSON is committed so everyone else, and CI, gets the data without
/// them. The catalog is proven against shipped AOT files by
/// <c>MetadataContractsAotTests</c>: for every element on disk, the order actually used must
/// be a subsequence of the order recorded here.
/// </para>
/// </remarks>
public static class MetadataContracts
{
    private const string ResourceName = "D365FO.Core.Metadata.metadata-contracts.json";

    private static readonly Lazy<Catalog> _catalog = new(Load, isThreadSafe: true);

    /// <summary>Version of the metadata assembly the catalog was generated from.</summary>
    public static string SourceVersion => _catalog.Value.Version;

    /// <summary>Every known contract, keyed by type name.</summary>
    public static IReadOnlyDictionary<string, MetadataContract> All => _catalog.Value.Types;

    /// <summary>The contract for <paramref name="typeName"/>, or null when the AOT has no such type.</summary>
    public static MetadataContract? Find(string? typeName)
        => string.IsNullOrEmpty(typeName) ? null
            : _catalog.Value.Types.TryGetValue(typeName!, out var c) ? c : null;

    /// <summary>
    /// The contract an element serializes against: its <c>i:type</c> when it pins one
    /// (<c>&lt;AxQuery i:type="AxQuerySimple"&gt;</c>), otherwise its element name.
    /// </summary>
    public static MetadataContract? ForElement(string elementName, string? xsiType)
        => Find(string.IsNullOrEmpty(xsiType) ? elementName : xsiType);

    /// <summary>
    /// True when <paramref name="member"/> survives a read of an element named after
    /// <paramref name="contract"/> — which depends on whether the contract can be instantiated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An element named after an <em>abstract</em> contract has to become something else, and
    /// the serializer resolves that from the collection holding it — no <c>i:type</c> is
    /// written. Every shipped form does this: <c>&lt;AxFormDataSource&gt;</c> is abstract and
    /// five members wide, yet carries an <c>AxFormDataSourceRoot</c>'s thirty. Judging those
    /// against the base alone would call a dozen correct properties unknown.
    /// </para>
    /// <para>
    /// A <em>concrete</em> contract gets no such promotion. It is instantiated as named, and a
    /// subtype's member is simply not on the object — read, dropped, gone. That is the
    /// difference between a form data source keeping its <c>DataSourceLinks</c> and a data
    /// entity losing every field's <c>DataField</c> and <c>DataSource</c>, which is what
    /// happened: those live on <c>AxDataEntityViewMappedField</c>, while the file said
    /// <c>&lt;AxDataEntityViewField&gt;</c> — concrete, and therefore taken at its word.
    /// </para>
    /// </remarks>
    public static bool AcceptsMember(MetadataContract contract, string member)
    {
        if (contract.IndexOf(member) >= 0) return true;
        if (!contract.IsAbstract) return false;

        foreach (var derived in DerivedFrom(contract.Name))
            if (derived.IndexOf(member) >= 0) return true;

        return false;
    }

    /// <summary>Every contract that derives, directly or transitively, from <paramref name="typeName"/>.</summary>
    public static IReadOnlyList<MetadataContract> DerivedFrom(string typeName)
        => _catalog.Value.Derived.TryGetValue(typeName, out var list) ? list : Array.Empty<MetadataContract>();

    /// <summary>
    /// The contract whose member order actually governs an element — which is not always the
    /// contract its name points at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A collection writes its items under the <em>declared</em> item type's name, and the
    /// serializer resolves the concrete type from the collection itself, so no <c>i:type</c> is
    /// written. Every shipped form does this: <c>&lt;AxFormDataSource&gt;</c> — an abstract type
    /// with five members — carries an <c>AxFormDataSourceRoot</c>, whose thirty members include
    /// <c>AllowDelete</c> and <c>DataSourceLinks</c>. Ranking those against the base finds no
    /// position for them, leaves them where they were, and the serializer drops them on read.
    /// </para>
    /// <para>
    /// So when an abstract contract does not account for every member present, the subtype that
    /// accounts for the most of them wins; ties go to the smallest contract, then ordinal name,
    /// so the choice is deterministic. A concrete contract is taken at its word — it is what
    /// gets instantiated — and an explicit <c>i:type</c> is always authoritative.
    /// </para>
    /// </remarks>
    public static MetadataContract? EffectiveContract(string elementName, string? xsiType, IEnumerable<string> presentMembers)
    {
        if (!string.IsNullOrEmpty(xsiType)) return Find(xsiType);

        var named = Find(elementName);
        if (named is null || !named.IsAbstract) return named;

        var unresolved = presentMembers.Where(m => named.IndexOf(m) < 0).ToList();
        if (unresolved.Count == 0) return named;

        MetadataContract? best = null;
        var bestScore = 0;
        foreach (var candidate in DerivedFrom(named.Name))
        {
            var score = unresolved.Count(m => candidate.IndexOf(m) >= 0);
            if (score == 0) continue;

            if (score > bestScore
                || (score == bestScore && best is not null && IsPreferredOver(candidate, best)))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best ?? named;
    }

    private static bool IsPreferredOver(MetadataContract candidate, MetadataContract incumbent)
        => candidate.Members.Count != incumbent.Members.Count
            ? candidate.Members.Count < incumbent.Members.Count
            : string.CompareOrdinal(candidate.Name, incumbent.Name) < 0;

    /// <summary>
    /// The values <paramref name="enumName"/> accepts, or an empty list when the catalog has no
    /// such enum.
    /// </summary>
    /// <remarks>
    /// An out-of-range enum value is a harder failure than an unknown member: the serializer
    /// throws rather than skipping, so the provider cannot read the file at all and the object
    /// is invisible to every tool downstream. A generated workspace claimed
    /// <c>Style=TileSection</c> and a table-of-contents form <c>TabStyle=TOCList</c> — neither
    /// value exists, and both files were unreadable end to end.
    /// </remarks>
    public static IReadOnlyList<string> EnumValues(string enumName)
        => _catalog.Value.Enums.TryGetValue(enumName, out var values) ? values : Array.Empty<string>();

    /// <summary>Every enum the catalog knows, keyed by name.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Enums => _catalog.Value.Enums;

    /// <summary>
    /// The enum constraining <paramref name="member"/> on <paramref name="contract"/> or on any
    /// type derived from it, or null when the member is not enum-typed.
    /// </summary>
    /// <remarks>
    /// Derived types are searched for the same reason <see cref="AcceptsMember"/> searches them:
    /// the element may be named after a base while carrying a subtype's members. Where subtypes
    /// disagree about a member's enum the member is left unconstrained rather than judged against
    /// an arbitrary one.
    /// </remarks>
    public static string? EnumForMember(MetadataContract contract, string member)
    {
        var direct = contract.EnumFor(member);
        if (direct is not null) return direct;

        string? found = null;
        foreach (var derived in DerivedFrom(contract.Name))
        {
            var candidate = derived.EnumFor(member);
            if (candidate is null) continue;
            if (found is null) found = candidate;
            else if (!string.Equals(found, candidate, StringComparison.Ordinal)) return null;
        }

        return found;
    }

    private sealed record Catalog(
        string Version,
        IReadOnlyDictionary<string, MetadataContract> Types,
        IReadOnlyDictionary<string, IReadOnlyList<MetadataContract>> Derived,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Enums);

    private static Catalog Load()
    {
        using var stream = typeof(MetadataContracts).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' not found.");

        var dto = JsonSerializer.Deserialize<CatalogDto>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? throw new InvalidOperationException("metadata-contracts.json could not be parsed.");

        var types = new Dictionary<string, MetadataContract>(dto.Types.Count, StringComparer.Ordinal);
        foreach (var (name, t) in dto.Types)
            types[name] = new MetadataContract(
                name,
                t.Ns ?? string.Empty,
                t.Abstract,
                t.Base,
                t.Members ?? Array.Empty<string>(),
                t.EnumOf is null
                    ? EmptyEnumOf
                    : new Dictionary<string, string>(t.EnumOf, StringComparer.Ordinal));

        // Transitive subtype index: walking up each type's base chain is cheaper than walking
        // down, and both directions are needed only once.
        var derived = new Dictionary<string, List<MetadataContract>>(StringComparer.Ordinal);
        foreach (var contract in types.Values)
        {
            var ancestor = contract.BaseType;
            var guard = 0;
            while (!string.IsNullOrEmpty(ancestor) && guard++ < 32)
            {
                if (!derived.TryGetValue(ancestor!, out var list))
                    derived[ancestor!] = list = new List<MetadataContract>();
                list.Add(contract);

                ancestor = types.TryGetValue(ancestor!, out var parent) ? parent.BaseType : null;
            }
        }

        var enums = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (name, values) in dto.Enums)
            enums[name] = values ?? Array.Empty<string>();

        return new Catalog(
            dto.Version ?? "unknown",
            types,
            derived.ToDictionary(p => p.Key, p => (IReadOnlyList<MetadataContract>)p.Value, StringComparer.Ordinal),
            enums);
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyEnumOf =
        new Dictionary<string, string>(0, StringComparer.Ordinal);

    private sealed class CatalogDto
    {
        public string? Version { get; set; }

        [JsonPropertyName("types")]
        public Dictionary<string, TypeDto> Types { get; set; } = new();

        [JsonPropertyName("enums")]
        public Dictionary<string, string[]?> Enums { get; set; } = new();
    }

    private sealed class TypeDto
    {
        public string? Ns { get; set; }
        public bool Abstract { get; set; }
        public string? Base { get; set; }
        public string[]? Members { get; set; }
        public Dictionary<string, string>? EnumOf { get; set; }
    }
}
