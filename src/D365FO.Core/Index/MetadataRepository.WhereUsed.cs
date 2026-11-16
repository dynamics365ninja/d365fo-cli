using Dapper;

namespace D365FO.Core.Index;

public sealed partial class MetadataRepository
{
    /// <summary>One metadata reference to an object, read from the index.</summary>
    /// <param name="Kind">The referencing object's AOT kind (<c>Form</c>, <c>Query</c>, <c>Edt</c>, <c>MenuItemDisplay</c>, <c>TableExtension</c>, …).</param>
    /// <param name="Name">The referencing object.</param>
    /// <param name="Model">The model that declares it, or null when the index does not record one.</param>
    /// <param name="Element">Where inside it, or null for the object itself (<c>DataSources/CustTable</c>, <c>Fields/AccountNum</c>).</param>
    /// <param name="Via">The property or structure that holds the reference (<c>Table</c>, <c>ExtendedDataType</c>, <c>RelatedTable</c>, <c>EntryPoint</c>, …).</param>
    public sealed record StructuralReference(string Kind, string Name, string? Model, string? Element, string Via);

    /// <summary>
    /// What the structural where-used reads, for the answer's <c>scope</c>. Everything else the
    /// AOT can hold a name in (a form control's DataField, a data entity's data sources, a field
    /// typed directly on an enum, a report's RDP class) is not in the index and only the compiler's
    /// cross-reference (<c>--xref</c>) or the X++ text scan sees it.
    /// </summary>
    public const string StructuralReferenceSources =
        "table extends, field EDTs (tables, table extensions, maps), table relations, table FormRef/ListPageRef, "
        + "EDT extends/enum/reference table/FormHelp, form and query data sources, view and data entity queries, "
        + "entity staging tables, map mappings, menu item objects, tiles, privilege entry points, duty and role "
        + "grants, security policies, services and service groups, report data sets, class extends, "
        + "object extensions, CoC extensions, event handlers, business event contracts, configuration key parents";

    // Every branch selects (Kind, Name, Model, Element, Via) for one place a name is stored.
    // A branch whose table has no ModelId borrows the model of the object that owns the row.
    private const string StructuralReferenceSql = @"
        SELECT 'Table' AS Kind, t.Name AS Name, m.Name AS Model, NULL AS Element, 'TableExtends' AS Via
          FROM Tables t JOIN Models m ON m.ModelId = t.ModelId WHERE t.TableExtends = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Table', t.Name, m.Name, NULL, 'FormRef'
          FROM Tables t JOIN Models m ON m.ModelId = t.ModelId WHERE t.FormRef = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Table', t.Name, m.Name, NULL, 'ListPageRef'
          FROM Tables t JOIN Models m ON m.ModelId = t.ModelId WHERE t.ListPageRef = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Table', t.Name, m.Name, 'Fields/' || f.Name, 'ExtendedDataType'
          FROM TableFields f JOIN Tables t ON t.TableId = f.TableId JOIN Models m ON m.ModelId = t.ModelId
         WHERE f.EdtName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'TableExtension', x.ExtensionName, m.Name, 'Fields/' || x.Name, 'ExtendedDataType'
          FROM ExtensionFields x JOIN Models m ON m.ModelId = x.ModelId WHERE x.EdtName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Map', mp.Name, m.Name, 'Fields/' || f.Name, 'ExtendedDataType'
          FROM MapFields f JOIN Maps mp ON mp.MapId = f.MapId JOIN Models m ON m.ModelId = mp.ModelId
         WHERE f.EdtName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Table', r.FromTable,
               (SELECT m.Name FROM Tables t JOIN Models m ON m.ModelId = t.ModelId WHERE t.Name = r.FromTable LIMIT 1),
               'Relations/' || COALESCE(r.RelationName, r.ToTable), 'RelatedTable'
          FROM Relations r WHERE r.ToTable = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Edt', e.Name, m.Name, NULL, 'Extends'
          FROM Edts e JOIN Models m ON m.ModelId = e.ModelId WHERE e.ExtendsName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Edt', e.Name, m.Name, NULL, 'EnumType'
          FROM Edts e JOIN Models m ON m.ModelId = e.ModelId WHERE e.EnumType = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Edt', e.Name, m.Name, NULL, 'ReferenceTable'
          FROM Edts e JOIN Models m ON m.ModelId = e.ModelId WHERE e.ReferenceTable = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Edt', e.Name, m.Name, NULL, 'FormHelp'
          FROM Edts e JOIN Models m ON m.ModelId = e.ModelId WHERE e.FormHelp = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Form', f.Name, m.Name, 'DataSources/' || ds.Name, 'Table'
          FROM FormDataSources ds JOIN Forms f ON f.FormId = ds.FormId JOIN Models m ON m.ModelId = f.ModelId
         WHERE ds.TableName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Query', q.Name, m.Name, 'DataSources/' || ds.Name, 'Table'
          FROM QueryDataSources ds JOIN Queries q ON q.QueryId = ds.QueryId JOIN Models m ON m.ModelId = q.ModelId
         WHERE ds.TableName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'View', v.Name, m.Name, NULL, 'Query'
          FROM Views v JOIN Models m ON m.ModelId = v.ModelId WHERE v.QueryName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'DataEntityView', d.Name, m.Name, NULL, 'Query'
          FROM DataEntities d JOIN Models m ON m.ModelId = d.ModelId WHERE d.QueryName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'DataEntityView', d.Name, m.Name, NULL, 'DataManagementStagingTable'
          FROM DataEntities d JOIN Models m ON m.ModelId = d.ModelId WHERE d.StagingTable = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Map', mp.Name, m.Name, 'Mappings/' || mt.TableName, 'MappingTable'
          FROM MapTables mt JOIN Maps mp ON mp.MapId = mt.MapId JOIN Models m ON m.ModelId = mp.ModelId
         WHERE mt.TableName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'MenuItem' || mi.Kind, mi.Name, m.Name, NULL, 'Object'
          FROM MenuItems mi JOIN Models m ON m.ModelId = mi.ModelId WHERE mi.Object = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Tile', t.Name, m.Name, NULL, 'MenuItemName'
          FROM Tiles t JOIN Models m ON m.ModelId = t.ModelId WHERE t.MenuItemName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'SecurityPrivilege', ep.Privilege,
               (SELECT m.Name FROM SecurityPrivileges p JOIN Models m ON m.ModelId = p.ModelId WHERE p.Name = ep.Privilege LIMIT 1),
               'EntryPoints/' || COALESCE(ep.ObjectType, '') || '/' || ep.ObjectName || COALESCE('.' || ep.ObjectChild, ''),
               'EntryPoint' || COALESCE(' (' || ep.AccessLevel || ')', '')
          FROM SecurityPrivilegeEntryPoints ep WHERE ep.ObjectName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'SecurityDuty', dp.Duty,
               (SELECT m.Name FROM SecurityDuties d JOIN Models m ON m.ModelId = d.ModelId WHERE d.Name = dp.Duty LIMIT 1),
               'Privileges/' || dp.Privilege, 'Privilege'
          FROM SecurityDutyPrivileges dp WHERE dp.Privilege = @n COLLATE NOCASE
        UNION ALL
        SELECT 'SecurityRole', rp.Role,
               (SELECT m.Name FROM SecurityRoles r JOIN Models m ON m.ModelId = r.ModelId WHERE r.Name = rp.Role LIMIT 1),
               'Privileges/' || rp.Privilege, 'Privilege'
          FROM SecurityRolePrivileges rp WHERE rp.Privilege = @n COLLATE NOCASE
        UNION ALL
        SELECT 'SecurityRole', rd.Role,
               (SELECT m.Name FROM SecurityRoles r JOIN Models m ON m.ModelId = r.ModelId WHERE r.Name = rd.Role LIMIT 1),
               'Duties/' || rd.Duty, 'Duty'
          FROM SecurityRoleDuties rd WHERE rd.Duty = @n COLLATE NOCASE
        UNION ALL
        SELECT 'SecurityPolicy', sp.Name, m.Name, NULL, 'ConstrainedTable'
          FROM SecurityPolicies sp JOIN Models m ON m.ModelId = sp.ModelId WHERE sp.ConstrainedTable = @n COLLATE NOCASE
        UNION ALL
        SELECT 'SecurityPolicy', sp.Name, m.Name, NULL, 'Query'
          FROM SecurityPolicies sp JOIN Models m ON m.ModelId = sp.ModelId WHERE sp.PolicyQuery = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Service', s.Name, m.Name, NULL, 'Class'
          FROM Services s JOIN Models m ON m.ModelId = s.ModelId WHERE s.Class = @n COLLATE NOCASE
        UNION ALL
        SELECT 'ServiceGroup', g.Name, m.Name, 'Services/' || sm.ServiceName, 'Service'
          FROM ServiceGroupMembers sm JOIN ServiceGroups g ON g.GroupId = sm.GroupId JOIN Models m ON m.ModelId = g.ModelId
         WHERE sm.ServiceName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Report', rp.Name, m.Name, 'DataSets/' || ds.Name, COALESCE(ds.Kind, 'DataSet')
          FROM ReportDataSets ds JOIN Reports rp ON rp.ReportId = ds.ReportId JOIN Models m ON m.ModelId = rp.ModelId
         WHERE ds.QueryOrClass = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Class', c.Name, m.Name, NULL, 'Extends'
          FROM Classes c JOIN Models m ON m.ModelId = c.ModelId WHERE c.ExtendsName = @n COLLATE NOCASE
        UNION ALL
        SELECT x.Kind || 'Extension', x.ExtensionName, m.Name, NULL, 'Extends'
          FROM ObjectExtensions x JOIN Models m ON m.ModelId = x.ModelId WHERE x.TargetName = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Class', cc.ExtensionClass, m.Name, 'Methods/' || cc.TargetMethod, 'ChainOfCommand'
          FROM CocExtensions cc JOIN Models m ON m.ModelId = cc.ModelId WHERE cc.TargetClass = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Class', es.SubscriberClass, m.Name, 'Methods/' || es.SubscriberMethod,
               es.SourceKind || 'EventHandler' || COALESCE(' (' || COALESCE(es.SourceMember || '.', '') || es.EventType || ')',
                                                           ' (' || es.SourceMember || ')', '')
          FROM EventSubscribers es JOIN Models m ON m.ModelId = es.ModelId WHERE es.SourceObject = @n COLLATE NOCASE
        UNION ALL
        SELECT 'Class', b.Name, m.Name, NULL, 'BusinessEventContract'
          FROM BusinessEvents b JOIN Models m ON m.ModelId = b.ModelId WHERE b.ContractClass = @n COLLATE NOCASE
        UNION ALL
        SELECT 'ConfigurationKey', k.Name, m.Name, NULL, 'ParentKey'
          FROM ConfigurationKeys k JOIN Models m ON m.ModelId = k.ModelId WHERE k.ParentKey = @n COLLATE NOCASE";

    /// <summary>
    /// Where <paramref name="name"/> is referenced in metadata — form and query data sources,
    /// relations, field EDTs, menu item targets, security entry points, extensions and the rest of
    /// <see cref="StructuralReferenceSources"/> — read from the index alone: no build, no XML read.
    /// Case-insensitive, as AOT names are. Every reference the index holds is returned; the caller
    /// filters and caps.
    /// </summary>
    public IReadOnlyList<StructuralReference> FindStructuralReferences(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Array.Empty<StructuralReference>();
        using var conn = OpenReadOnly();
        return conn.Query<(string Kind, string Name, string? Model, string? Element, string Via)>(
                StructuralReferenceSql, new { n = name.Trim() })
            .Select(r => new StructuralReference(r.Kind, r.Name, r.Model, r.Element, r.Via))
            .Distinct()
            .ToList();
    }
}
