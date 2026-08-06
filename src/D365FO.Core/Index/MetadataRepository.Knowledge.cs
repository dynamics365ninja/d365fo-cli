using D365FO.Core.Knowledge;
using Dapper;

namespace D365FO.Core.Index;

/// <summary>
/// <see cref="IKnowledgeSymbolLookup"/> over the SQLite index — the half of the knowledge
/// audit that needs a real index. Deliberately wider than
/// <see cref="Validation.IReferenceIndex.SymbolKinds"/>: knowledge prose names menu items,
/// reports, services, security elements and workflow types as freely as it names classes, and
/// reporting those as "does not exist" would bury the real defects.
/// </summary>
public sealed partial class MetadataRepository : IKnowledgeSymbolLookup
{
    /// <summary>
    /// Every AOT collection the index carries a name column for, as
    /// <c>(kind, table)</c>. Kept as one list so a new indexed family is added in one place.
    /// </summary>
    private static readonly (string Kind, string Table)[] NamedCollections =
    [
        ("table", "Tables"),
        ("class", "Classes"),
        ("edt", "Edts"),
        ("enum", "Enums"),
        ("form", "Forms"),
        ("query", "Queries"),
        ("view", "Views"),
        ("data-entity", "DataEntities"),
        ("map", "Maps"),
        ("menu-item", "MenuItems"),
        ("report", "Reports"),
        ("service", "Services"),
        ("service-group", "ServiceGroups"),
        ("workflow", "WorkflowTypes"),
        ("business-event", "BusinessEvents"),
        ("config-key", "ConfigurationKeys"),
        ("security-role", "SecurityRoles"),
        ("security-duty", "SecurityDuties"),
        ("security-privilege", "SecurityPrivileges"),
        ("security-policy", "SecurityPolicies"),
        ("tile", "Tiles"),
        ("workspace", "Workspaces"),
    ];

    private static readonly string ResolveSql = string.Join(
        "\n            UNION ALL ",
        NamedCollections.Select(c => $"SELECT '{c.Kind}' AS Kind, Name FROM {c.Table} WHERE Name = @name COLLATE NOCASE"));

    /// <inheritdoc />
    public KnowledgeSymbolHit? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var conn = OpenReadOnly();
        var rows = conn.Query<(string Kind, string Name)>(ResolveSql, new { name }).ToList();
        if (rows.Count == 0) return null;

        // An exact-case row is the AOT's spelling; otherwise take the first hit so the
        // caller can report the correct casing.
        var exact = rows.Where(r => string.Equals(r.Name, name, StringComparison.Ordinal))
                        .Select(r => r.Name).FirstOrDefault();
        var canonical = exact ?? rows[0].Name;
        return new KnowledgeSymbolHit(canonical, rows.Select(r => r.Kind).Distinct(StringComparer.Ordinal).ToList());
    }

    /// <inheritdoc />
    public bool IsReferencedBase(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        using var conn = OpenReadOnly();
        return conn.ExecuteScalar<long>(@"
            SELECT COUNT(1) FROM (
                SELECT 1 FROM Classes WHERE ExtendsName = @name COLLATE NOCASE
                UNION ALL
                SELECT 1 FROM Tables  WHERE TableExtends = @name COLLATE NOCASE
                LIMIT 1
            )", new { name }) > 0;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reuses <see cref="FindMethod"/>, so an inherited method or one added by a CoC
    /// extension counts — knowledge that documents <c>CustTable::find()</c> is correct
    /// whether or not the method is declared on the type itself.
    /// </remarks>
    public bool HasMember(string canonical, string member) => FindMethod(canonical, member) is not null;
}
