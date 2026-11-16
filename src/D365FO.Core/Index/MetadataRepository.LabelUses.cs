using Dapper;

namespace D365FO.Core.Index;

public sealed partial class MetadataRepository
{
    /// <summary>One indexed property whose value is a label reference.</summary>
    /// <param name="Kind">The AOT kind, named as its root element without <c>Ax</c> (<c>Table</c>, <c>MenuItemDisplay</c>, <c>DataEntityView</c>).</param>
    /// <param name="Name">The object.</param>
    /// <param name="Model">The model that declares it.</param>
    /// <param name="Element">Where in the object, or null for the object itself (<c>Fields/AccountNum</c>).</param>
    /// <param name="Property">The property that holds the label.</param>
    public sealed record IndexedLabelUse(string Kind, string Name, string Model, string? Element, string Property);

    /// <summary>
    /// The properties the index keeps a label reference for — the <c>Label</c> columns. Every
    /// other label-bearing property (<c>HelpText</c>, a form control's <c>Text</c>, a form's
    /// <c>Caption</c>, <c>DeveloperDocumentation</c>) is only in the XML on disk.
    /// </summary>
    public const string IndexedLabelProperties =
        "Label of tables, table fields, EDTs, enums, enum values, menu items, security roles, duties and "
        + "privileges, views, data entities, maps, map fields, configuration keys, tiles and workspaces";

    /// <summary>
    /// Every indexed <c>Label</c> column that holds exactly <paramref name="labelToken"/> (case-insensitive),
    /// across all models. One pass over each table, no source file read: well under a second on a
    /// full installation, where scanning the XML for the same answer takes minutes.
    /// </summary>
    public IReadOnlyList<IndexedLabelUse> FindIndexedLabelUses(string labelToken)
    {
        using var conn = OpenReadOnly();
        const string sql = @"
            SELECT 'Table' AS Kind, t.Name AS Name, m.Name AS Model, NULL AS Element, 'Label' AS Property
              FROM Tables t JOIN Models m ON m.ModelId = t.ModelId WHERE t.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Table', t.Name, m.Name, 'Fields/' || f.Name, 'Label'
              FROM TableFields f JOIN Tables t ON t.TableId = f.TableId JOIN Models m ON m.ModelId = t.ModelId
             WHERE f.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Edt', e.Name, m.Name, NULL, 'Label'
              FROM Edts e JOIN Models m ON m.ModelId = e.ModelId WHERE e.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Enum', e.Name, m.Name, NULL, 'Label'
              FROM Enums e JOIN Models m ON m.ModelId = e.ModelId WHERE e.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Enum', e.Name, m.Name, 'EnumValues/' || v.Name, 'Label'
              FROM EnumValues v JOIN Enums e ON e.EnumId = v.EnumId JOIN Models m ON m.ModelId = e.ModelId
             WHERE v.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'MenuItem' || mi.Kind, mi.Name, m.Name, NULL, 'Label'
              FROM MenuItems mi JOIN Models m ON m.ModelId = mi.ModelId WHERE mi.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'SecurityRole', r.Name, m.Name, NULL, 'Label'
              FROM SecurityRoles r JOIN Models m ON m.ModelId = r.ModelId WHERE r.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'SecurityDuty', d.Name, m.Name, NULL, 'Label'
              FROM SecurityDuties d JOIN Models m ON m.ModelId = d.ModelId WHERE d.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'SecurityPrivilege', p.Name, m.Name, NULL, 'Label'
              FROM SecurityPrivileges p JOIN Models m ON m.ModelId = p.ModelId WHERE p.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'View', v.Name, m.Name, NULL, 'Label'
              FROM Views v JOIN Models m ON m.ModelId = v.ModelId WHERE v.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'DataEntityView', d.Name, m.Name, NULL, 'Label'
              FROM DataEntities d JOIN Models m ON m.ModelId = d.ModelId WHERE d.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Map', mp.Name, m.Name, NULL, 'Label'
              FROM Maps mp JOIN Models m ON m.ModelId = mp.ModelId WHERE mp.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Map', mp.Name, m.Name, 'Fields/' || f.Name, 'Label'
              FROM MapFields f JOIN Maps mp ON mp.MapId = f.MapId JOIN Models m ON m.ModelId = mp.ModelId
             WHERE f.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'ConfigurationKey', c.Name, m.Name, NULL, 'Label'
              FROM ConfigurationKeys c JOIN Models m ON m.ModelId = c.ModelId WHERE c.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Tile', t.Name, m.Name, NULL, 'Label'
              FROM Tiles t JOIN Models m ON m.ModelId = t.ModelId WHERE t.Label = @l COLLATE NOCASE
            UNION ALL
            SELECT 'Workspace', w.Name, m.Name, NULL, 'Label'
              FROM Workspaces w JOIN Models m ON m.ModelId = w.ModelId WHERE w.Label = @l COLLATE NOCASE";
        return conn.Query<(string Kind, string Name, string Model, string? Element, string Property)>(sql, new { l = labelToken })
            .Select(r => new IndexedLabelUse(r.Kind, r.Name, r.Model, r.Element, r.Property))
            .ToList();
    }
}
