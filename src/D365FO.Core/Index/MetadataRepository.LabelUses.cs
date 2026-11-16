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

    /// <summary>The indexed <c>Label</c> columns, as <c>(source, column)</c> pairs that each join to <c>Models m</c>.</summary>
    private static readonly (string From, string Column)[] LabelColumns =
    {
        ("Tables t JOIN Models m ON m.ModelId = t.ModelId", "t.Label"),
        ("TableFields f JOIN Tables t ON t.TableId = f.TableId JOIN Models m ON m.ModelId = t.ModelId", "f.Label"),
        ("Edts e JOIN Models m ON m.ModelId = e.ModelId", "e.Label"),
        ("Enums e JOIN Models m ON m.ModelId = e.ModelId", "e.Label"),
        ("EnumValues v JOIN Enums e ON e.EnumId = v.EnumId JOIN Models m ON m.ModelId = e.ModelId", "v.Label"),
        ("MenuItems mi JOIN Models m ON m.ModelId = mi.ModelId", "mi.Label"),
        ("SecurityRoles r JOIN Models m ON m.ModelId = r.ModelId", "r.Label"),
        ("SecurityDuties d JOIN Models m ON m.ModelId = d.ModelId", "d.Label"),
        ("SecurityPrivileges p JOIN Models m ON m.ModelId = p.ModelId", "p.Label"),
        ("Views v JOIN Models m ON m.ModelId = v.ModelId", "v.Label"),
        ("DataEntities d JOIN Models m ON m.ModelId = d.ModelId", "d.Label"),
        ("Maps mp JOIN Models m ON m.ModelId = mp.ModelId", "mp.Label"),
        ("MapFields f JOIN Maps mp ON mp.MapId = f.MapId JOIN Models m ON m.ModelId = mp.ModelId", "f.Label"),
        ("ConfigurationKeys c JOIN Models m ON m.ModelId = c.ModelId", "c.Label"),
        ("Tiles t JOIN Models m ON m.ModelId = t.ModelId", "t.Label"),
        ("Workspaces w JOIN Models m ON m.ModelId = w.ModelId", "w.Label"),
    };

    /// <summary>
    /// Every indexed <c>Label</c> column holding a reference into <paramref name="labelFile"/>
    /// (<c>@File:…</c>), with the model that declares it. One pass per column for a whole label
    /// file, so <c>labels audit --all-models</c> does not run one query per key.
    /// </summary>
    public IReadOnlyList<(string Label, string Model)> FindIndexedLabelTokensInFile(string labelFile)
    {
        if (string.IsNullOrWhiteSpace(labelFile)) return Array.Empty<(string, string)>();
        using var conn = OpenReadOnly();
        var sql = string.Join("\nUNION ALL\n", LabelColumns.Select(c =>
            $"SELECT {c.Column} AS Label, m.Name AS Model FROM {c.From} WHERE {c.Column} LIKE @p ESCAPE '\\'"));
        var prefix = "@" + labelFile.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + ":%";
        return conn.Query<(string Label, string Model)>(sql, new { p = prefix }).ToList();
    }

    /// <summary>
    /// Every key the index holds for label file <paramref name="labelFile"/>, in any indexed
    /// language. One query per file instead of one <see cref="ResolveLabel"/> per token: that
    /// compares case-insensitively, which the <c>Labels</c> index cannot serve, and took about
    /// 0.3 s a token on a full installation. The exact spelling is tried first (an index seek);
    /// a file id written in another case falls back to the case-insensitive scan.
    /// </summary>
    public IReadOnlySet<string> LabelKeysOf(string labelFile)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(labelFile)) return keys;
        using var conn = OpenReadOnly();
        keys.UnionWith(conn.Query<string>("SELECT DISTINCT Key FROM Labels WHERE LabelFile = @f", new { f = labelFile }));
        if (keys.Count == 0)
            keys.UnionWith(conn.Query<string>("SELECT DISTINCT Key FROM Labels WHERE LabelFile = @f COLLATE NOCASE", new { f = labelFile }));
        return keys;
    }

    /// <summary>
    /// The X++ bodies in the source index that mention <paramref name="word"/> as a whole token
    /// (a label file name, so <c>@File:Key</c> in a string literal is found). Empty when the host
    /// SQLite build has no FTS5 or no model is source-indexed.
    /// </summary>
    public IReadOnlyList<string> MethodBodiesMentioning(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return Array.Empty<string>();
        using var conn = OpenReadOnly();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Body FROM MethodSourceFts WHERE MethodSourceFts MATCH $q";
            cmd.Parameters.AddWithValue("$q", "\"" + word.Replace("\"", "\"\"") + "\"");
            using var reader = cmd.ExecuteReader();
            var bodies = new List<string>();
            while (reader.Read())
                if (!reader.IsDBNull(0)) bodies.Add(reader.GetString(0));
            return bodies;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return Array.Empty<string>();
        }
    }
}
