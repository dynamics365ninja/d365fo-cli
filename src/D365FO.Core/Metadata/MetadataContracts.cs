using System.Reflection;
using System.Xml.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace D365FO.Core.Metadata;

/// <summary>One MetaModel type's serialization contract.</summary>
/// <param name="Name">Contract (and element) name, e.g. <c>AxTable</c>.</param>
/// <param name="Namespace">XML namespace the contract declares; empty for most types.</param>
/// <param name="IsAbstract">Type cannot be instantiated — the file must pin a concrete <c>i:type</c>.</param>
/// <param name="Members">Member names in the exact order the serializer reads and writes them.</param>
/// <param name="EnumOf">Members whose value is constrained to an enum, keyed by member name.</param>
/// <param name="TypeOf">
/// Members that hold a contract object, mapped to that contract's type name. An element named
/// after such a member (<c>&lt;Grant&gt;</c>, <c>&lt;Design&gt;</c>) is the only way to reach the
/// type inside it, since the element carries the member's name rather than the type's.
/// </param>
public sealed record MetadataContract(
    string Name,
    string Namespace,
    bool IsAbstract,
    string? BaseType,
    IReadOnlyList<string> Members,
    IReadOnlyDictionary<string, string> EnumOf,
    IReadOnlyDictionary<string, string> TypeOf)
{
    private Dictionary<string, int>? _index;

    /// <summary>The enum constraining <paramref name="member"/>, or null when it is free text.</summary>
    public string? EnumFor(string member)
        => EnumOf.TryGetValue(member, out var e) ? e : null;

    /// <summary>
    /// The contract type <paramref name="member"/> holds, or null when the member is a scalar.
    /// </summary>
    public string? ContractTypeFor(string member)
        => TypeOf.TryGetValue(member, out var t) ? t : null;

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
    /// True when an element named after <paramref name="contract"/> keeps
    /// <paramref name="member"/> on read.
    /// </summary>
    /// <remarks>
    /// Simply whether the contract declares it — there is deliberately no walk into subtypes.
    /// That walk existed to explain <c>&lt;AxFormDataSource&gt;</c>, which carries thirty
    /// members while the CLR type of that name is abstract and has five. The real explanation
    /// was that the element does not name that CLR type at all: <c>AxFormDataSourceRoot</c>
    /// <em>contracts</em> to <c>AxFormDataSource</c>, and the catalog was keyed by CLR name.
    /// Keyed by contract name, the element names exactly the type the reader instantiates, and
    /// a subtype's member really is absent — accepting one would hide the very defect this rule
    /// exists to find, as it did for every data entity field's <c>DataField</c>.
    /// </remarks>
    public static bool AcceptsMember(MetadataContract contract, string member)
        => contract.IndexOf(member) >= 0;

    /// <summary>Every contract that derives, directly or transitively, from <paramref name="typeName"/>.</summary>
    public static IReadOnlyList<MetadataContract> DerivedFrom(string typeName)
        => _catalog.Value.Derived.TryGetValue(typeName, out var list) ? list : Array.Empty<MetadataContract>();

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
    /// The contract governing what appears <em>inside</em> a member element, or null when the
    /// member is a scalar or the parent is unknown.
    /// </summary>
    /// <remarks>
    /// A member element is named after the member, not the type it holds, so its contents are
    /// unreachable without this: <c>&lt;Grant&gt;</c> is an <c>AccessGrant</c> and
    /// <c>&lt;Design&gt;</c> an <c>AxFormDesign</c>, but neither name appears in the catalog.
    /// Until this existed, every such subtree was simply unchecked and unordered — which is how
    /// <c>&lt;AccessLevel&gt;</c> sat inside a privilege's <c>&lt;Grant&gt;</c>, a member of no
    /// type at all, through a full audit.
    /// </remarks>
    public static MetadataContract? MemberContract(MetadataContract? parent, string member)
        => parent is null ? null : Find(parent.ContractTypeFor(member));

    /// <summary>
    /// The contract governing what may appear inside <paramref name="element"/>, given the
    /// contract governing its parent's contents.
    /// </summary>
    /// <remarks>
    /// Three shapes reach the same question. An element with an <c>i:type</c> says what it is;
    /// an element named after a type <em>is</em> that type (a collection item, or a document
    /// root); anything else is a member, and what it holds is whatever the parent declares it to
    /// hold. Resolving all three in one place is what lets the order canonicaliser and the shape
    /// rules agree about every element in a document.
    /// </remarks>
    public static MetadataContract? GoverningContract(XElement element, MetadataContract? parent)
    {
        var local = element.Name.LocalName;
        var xsiType = element.Attribute(XsiType)?.Value;

        if (!string.IsNullOrEmpty(xsiType))
            return ForElement(local, xsiType);

        // A name that is BOTH a member of the parent and a type in the catalog is a member:
        // what the parent says it holds beats what the name happens to collide with. Two names
        // collide this way in the whole catalog (DataSource, Method), and one of them cost
        // 4 574 findings on a stock installation — <DataSource> inside
        // AxQueryExtensionEmbeddedDataSource is declared as an AxQuerySimpleEmbeddedDataSource,
        // but there is also a type called DataSource (the form data-source property collection,
        // three members), so every real data-source property under it was judged against a type
        // it has nothing to do with.
        var declared = MemberContract(parent, local);
        if (declared is not null) return declared;

        return Find(local) is not null ? ForElement(local, xsiType) : null;
    }

    private static readonly XName XsiType =
        XNamespace.Get("http://www.w3.org/2001/XMLSchema-instance") + "type";

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
                    ? EmptyMap
                    : new Dictionary<string, string>(t.EnumOf, StringComparer.Ordinal),
                t.TypeOf is null
                    ? EmptyMap
                    : new Dictionary<string, string>(t.TypeOf, StringComparer.Ordinal));

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

    private static readonly IReadOnlyDictionary<string, string> EmptyMap =
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
        public Dictionary<string, string>? TypeOf { get; set; }
    }
}
