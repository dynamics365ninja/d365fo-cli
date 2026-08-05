using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D365FO.Core.Metadata;

/// <summary>One MetaModel type's serialization contract.</summary>
/// <param name="Name">Contract (and element) name, e.g. <c>AxTable</c>.</param>
/// <param name="Namespace">XML namespace the contract declares; empty for most types.</param>
/// <param name="IsAbstract">Type cannot be instantiated — the file must pin a concrete <c>i:type</c>.</param>
/// <param name="Members">Member names in the exact order the serializer reads and writes them.</param>
public sealed record MetadataContract(
    string Name,
    string Namespace,
    bool IsAbstract,
    string? BaseType,
    IReadOnlyList<string> Members)
{
    private Dictionary<string, int>? _index;

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
    /// True when <paramref name="member"/> is declared by <paramref name="contract"/> or by any
    /// type derived from it.
    /// </summary>
    /// <remarks>
    /// Elements are routinely named after a base type while carrying a subtype's data — every
    /// shipped form writes <c>&lt;AxFormDataSource&gt;</c> for what is really an
    /// <c>AxFormDataSourceRoot</c>, with the subtype's members inside and no <c>i:type</c> to
    /// say so. Judging those against the base alone would call a dozen correct properties
    /// unknown.
    /// </remarks>
    public static bool AcceptsMember(MetadataContract contract, string member)
    {
        if (contract.IndexOf(member) >= 0) return true;

        foreach (var derived in DerivedFrom(contract.Name))
            if (derived.IndexOf(member) >= 0) return true;

        return false;
    }

    /// <summary>Every contract that derives, directly or transitively, from <paramref name="typeName"/>.</summary>
    public static IReadOnlyList<MetadataContract> DerivedFrom(string typeName)
        => _catalog.Value.Derived.TryGetValue(typeName, out var list) ? list : Array.Empty<MetadataContract>();

    private sealed record Catalog(
        string Version,
        IReadOnlyDictionary<string, MetadataContract> Types,
        IReadOnlyDictionary<string, IReadOnlyList<MetadataContract>> Derived);

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
            types[name] = new MetadataContract(name, t.Ns ?? string.Empty, t.Abstract, t.Base, t.Members ?? Array.Empty<string>());

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

        return new Catalog(
            dto.Version ?? "unknown",
            types,
            derived.ToDictionary(p => p.Key, p => (IReadOnlyList<MetadataContract>)p.Value, StringComparer.Ordinal));
    }

    private sealed class CatalogDto
    {
        public string? Version { get; set; }

        [JsonPropertyName("types")]
        public Dictionary<string, TypeDto> Types { get; set; } = new();
    }

    private sealed class TypeDto
    {
        public string? Ns { get; set; }
        public bool Abstract { get; set; }
        public string? Base { get; set; }
        public string[]? Members { get; set; }
    }
}
