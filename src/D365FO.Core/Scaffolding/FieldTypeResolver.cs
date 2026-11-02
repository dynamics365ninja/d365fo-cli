using D365FO.Core.Index;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Resolves the type name a scaffolded field is declared with — an EDT, or a base enum — to the
/// primitive type that picks its concrete <c>AxTableField*</c> / <c>AxMapField*</c> subtype.
/// Every surface that scaffolds fields (CLI <c>generate table|map|extension</c>,
/// <c>modify add-field</c>, MCP <c>generate_object</c>) builds its resolver here, so the same
/// name cannot be typed differently through two of them.
/// </summary>
/// <remarks>
/// Issue #233. Resolution order: the EDT's indexed base type (walking <c>Extends</c>, see
/// <see cref="MetadataRepository.GetEdtBaseType"/>), then a base enum of that name, which
/// resolves to <see cref="BaseEnum"/> so the field is written as <c>AxTableFieldEnum</c> with
/// <c>&lt;EnumType&gt;</c> instead of an <c>&lt;ExtendedDataType&gt;</c> naming an enum. Only a
/// name the index knows neither way falls back to the scaffolder's name heuristic — and is
/// recorded in <see cref="Unresolved"/>, so the caller can say so instead of letting a guessed
/// String compile silently.
/// </remarks>
public sealed class FieldTypeResolver
{
    /// <summary>
    /// What <see cref="Resolve"/> returns for a base enum that is not an EDT. The scaffolders
    /// type such a field <c>Enum</c> and write the name as its <c>EnumType</c>.
    /// </summary>
    public const string BaseEnum = "BaseEnum";

    /// <summary>The warning code a caller prefixes <see cref="Warning"/> with.</summary>
    public const string UnresolvedCode = "EDT_BASETYPE_UNRESOLVED";

    private readonly MetadataRepository? _repo;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _unresolved = [];

    private FieldTypeResolver(MetadataRepository? repo) => _repo = repo;

    /// <summary>A resolver over <paramref name="repo"/>; with no repository every name is unresolved.</summary>
    public static FieldTypeResolver For(MetadataRepository? repo) => new(repo);

    /// <summary>
    /// The base type of the EDT <paramref name="name"/>, <see cref="BaseEnum"/> for a base enum,
    /// or null when the index knows neither. Never throws: an index error reads as unresolved.
    /// </summary>
    public string? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_cache.TryGetValue(name, out var cached)) return cached;

        string? resolved = null;
        if (_repo is not null)
        {
            try
            {
                resolved = _repo.GetEdtBaseType(name);
                if (resolved is null && _repo.GetEdt(name) is null && _repo.GetEnum(name) is not null)
                    resolved = BaseEnum;
            }
            catch
            {
                resolved = null;
            }
        }

        _cache[name] = resolved;
        if (resolved is null) _unresolved.Add(name);
        return resolved;
    }

    /// <summary>The names <see cref="Resolve"/> could not type from the index, in first-seen order.</summary>
    public IReadOnlyList<string> Unresolved => _unresolved;

    /// <summary>
    /// One warning naming every unresolved type and the subtype the heuristic picked for it, or
    /// null when everything resolved.
    /// </summary>
    public string? Warning()
    {
        if (_unresolved.Count == 0) return null;
        var guesses = string.Join(", ", _unresolved.Select(n => $"{n} → {XppScaffolder.HeuristicFieldSuffix(n)}"));
        return $"{UnresolvedCode}: the index has no base type for {guesses}, so the field type was guessed from the name. " +
               "Check the i:type of those fields. If the names are right, run `d365fo index refresh` " +
               "(an index from before schema v21 stored no EDT base types) or pass an EDT the index knows.";
    }
}
