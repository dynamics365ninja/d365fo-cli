using D365FO.Core.Validation;
using Dapper;

namespace D365FO.Core.Index;

/// <summary>
/// <see cref="IReferenceIndex"/> + <see cref="IPropertyStatsProvider"/> over the
/// SQLite index — the lookup surface used by <see cref="ReferenceResolver"/> and
/// <see cref="XppValidator"/> to prove generated code against real metadata.
/// </summary>
public sealed partial class MetadataRepository : IReferenceIndex, IPropertyStatsProvider
{
    private const int InheritanceWalkLimit = 10;

    /// <inheritdoc />
    /// <remarks>
    /// Extensions are symbols too. An extension row does not live in any of the object tables —
    /// it records the object it EXTENDS — so <c>NumberSeqModule.Kitting</c>, an enum extension the
    /// installation ships, used to come back empty here, and <c>prepare create</c> answered
    /// "no collision" for a name that is taken, immediately before a write (upstream 0b363e5).
    /// An extension reports as <c>&lt;kind&gt;-extension</c> (<c>enum-extension</c>,
    /// <c>table-extension</c>), so callers that look for a base kind by name are unaffected.
    /// </remarks>
    public IReadOnlyList<string> SymbolKinds(string name)
    {
        using var conn = OpenReadOnly();
        return conn.Query<string>(@"
            SELECT 'table'       AS Kind FROM Tables       WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'class'        FROM Classes      WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'edt'          FROM Edts         WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'enum'         FROM Enums        WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'form'         FROM Forms        WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'query'        FROM Queries      WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'view'         FROM Views        WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'data-entity'  FROM DataEntities WHERE Name = @name COLLATE NOCASE
            UNION SELECT 'map'          FROM Maps         WHERE Name = @name COLLATE NOCASE
            UNION SELECT lower(Kind) || '-extension'
                                        FROM ObjectExtensions WHERE ExtensionName = @name COLLATE NOCASE",
            new { name }).ToList();
    }

    /// <inheritdoc />
    public bool MenuItemExists(string name)
    {
        using var conn = OpenReadOnly();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM MenuItems WHERE Name = @name COLLATE NOCASE LIMIT 1",
            new { name }) > 0;
    }

    /// <summary>
    /// Every object in the index that DECLARES a method of this name, with the signature it
    /// declared — classes and tables alike.
    /// </summary>
    /// <remarks>
    /// The inverse of <see cref="FindMethod"/>, which asks whether one owner has one method.
    /// "Who else has implemented <c>validateWrite</c>, and with what signature" is the question
    /// asked before writing an override, and it was not answerable from the index at all.
    /// </remarks>
    public IReadOnlyList<MethodDeclaration> FindMethodDeclarations(string methodName, string? model = null, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(methodName)) return Array.Empty<MethodDeclaration>();
        limit = ClampLimit(limit, 20);

        using var conn = OpenReadOnly();

        // Read through a raw reader rather than Dapper's record materialiser: SQLite columns are
        // typeless, and for a literal kind on an EMPTY result set Dapper infers byte[] and
        // refuses to bind the record. It fails only when there are no rows — the case a test on
        // an empty index hits first and a caller with data never sees. SearchMethodSource
        // documents the same trap for the FTS columns.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT c.Name AS Owner, 'class' AS OwnerKind, mo.Name AS Model, m.Signature
            FROM Methods m
              JOIN Classes c ON m.ClassId = c.ClassId
              JOIN Models  mo ON mo.ModelId = c.ModelId
            WHERE m.Name = $method COLLATE NOCASE
              AND ($model IS NULL OR mo.Name = $model COLLATE NOCASE)
            UNION ALL
            SELECT t.Name AS Owner, 'table' AS OwnerKind, mo.Name AS Model, tm.Signature
            FROM TableMethods tm
              JOIN Tables t  ON tm.TableId = t.TableId
              JOIN Models mo ON mo.ModelId = t.ModelId
            WHERE tm.Name = $method COLLATE NOCASE
              AND ($model IS NULL OR mo.Name = $model COLLATE NOCASE)
            ORDER BY Owner
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$method", methodName);
        cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$limit", limit);

        using var reader = cmd.ExecuteReader();
        var rows = new List<MethodDeclaration>();
        while (reader.Read())
        {
            rows.Add(new MethodDeclaration(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return rows;
    }

    /// <inheritdoc />
    public MethodLookup? FindMethod(string ownerName, string methodName)
    {
        using var conn = OpenReadOnly();
        var owner = ownerName;
        for (int depth = 0; depth < InheritanceWalkLimit && !string.IsNullOrEmpty(owner); depth++)
        {
            // Class methods
            var sig = conn.QueryFirstOrDefault<(long Found, string? Signature)?>(@"
                SELECT 1 AS Found, m.Signature
                FROM Methods m JOIN Classes c ON m.ClassId = c.ClassId
                WHERE c.Name = @owner COLLATE NOCASE AND m.Name = @method COLLATE NOCASE
                LIMIT 1", new { owner, method = methodName });
            if (sig is not null) return new MethodLookup(sig.Value.Signature);

            // Table methods
            sig = conn.QueryFirstOrDefault<(long Found, string? Signature)?>(@"
                SELECT 1 AS Found, tm.Signature
                FROM TableMethods tm JOIN Tables t ON tm.TableId = t.TableId
                WHERE t.Name = @owner COLLATE NOCASE AND tm.Name = @method COLLATE NOCASE
                LIMIT 1", new { owner, method = methodName });
            if (sig is not null) return new MethodLookup(sig.Value.Signature);

            // CoC wrappers / extension-added methods targeting this object
            var coc = conn.ExecuteScalar<long>(@"
                SELECT COUNT(1) FROM CocExtensions
                WHERE TargetClass = @owner COLLATE NOCASE AND TargetMethod = @method COLLATE NOCASE",
                new { owner, method = methodName });
            if (coc > 0) return new MethodLookup(null);

            // Walk the inheritance chain (classes extend classes, tables extend tables)
            var parent = conn.QueryFirstOrDefault<string?>(@"
                SELECT ExtendsName FROM Classes WHERE Name = @owner COLLATE NOCASE AND ExtendsName IS NOT NULL
                UNION SELECT TableExtends FROM Tables WHERE Name = @owner COLLATE NOCASE AND TableExtends IS NOT NULL
                LIMIT 1", new { owner });
            if (parent is null || parent.Equals(owner, StringComparison.OrdinalIgnoreCase)) return null;
            owner = parent;
        }
        return null;
    }

    /// <inheritdoc />
    public bool FieldExists(string tableName, string fieldName)
    {
        using var conn = OpenReadOnly();
        var owner = tableName;
        for (int depth = 0; depth < InheritanceWalkLimit && !string.IsNullOrEmpty(owner); depth++)
        {
            var found = conn.ExecuteScalar<long>(@"
                SELECT COUNT(1) FROM (
                    SELECT 1 FROM TableFields tf JOIN Tables t ON tf.TableId = t.TableId
                    WHERE t.Name = @owner COLLATE NOCASE AND tf.Name = @field COLLATE NOCASE
                    UNION ALL
                    SELECT 1 FROM ExtensionFields ef
                    WHERE ef.TargetTable = @owner COLLATE NOCASE AND ef.Name = @field COLLATE NOCASE
                    UNION ALL
                    SELECT 1 FROM ViewFields vf JOIN Views v ON vf.ViewId = v.ViewId
                    WHERE v.Name = @owner COLLATE NOCASE AND vf.Name = @field COLLATE NOCASE
                    UNION ALL
                    SELECT 1 FROM DataEntityFields def JOIN DataEntities de ON def.EntityId = de.EntityId
                    WHERE de.Name = @owner COLLATE NOCASE AND def.Name = @field COLLATE NOCASE
                    UNION ALL
                    SELECT 1 FROM MapFields mf JOIN Maps mp ON mf.MapId = mp.MapId
                    WHERE mp.Name = @owner COLLATE NOCASE AND mf.Name = @field COLLATE NOCASE
                )", new { owner, field = fieldName });
            if (found > 0) return true;

            var parent = conn.QueryFirstOrDefault<string?>(
                "SELECT TableExtends FROM Tables WHERE Name = @owner COLLATE NOCASE AND TableExtends IS NOT NULL LIMIT 1",
                new { owner });
            if (parent is null || parent.Equals(owner, StringComparison.OrdinalIgnoreCase)) return false;
            owner = parent;
        }
        return false;
    }

    /// <inheritdoc />
    public bool LabelExists(string key, string? labelFile)
    {
        if (labelFile is null)
        {
            // Legacy token (@SYS12345) — reuse the multi-format resolver.
            return ResolveLabel(key).Count > 0;
        }
        using var conn = OpenReadOnly();
        return conn.ExecuteScalar<long>(@"
            SELECT COUNT(1) FROM Labels
            WHERE LabelFile = @labelFile COLLATE NOCASE AND Key = @key COLLATE NOCASE",
            new { key, labelFile }) > 0;
    }

    /// <inheritdoc />
    public bool LabelFileExists(string fileId)
    {
        using var conn = OpenReadOnly();
        return conn.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM Labels WHERE LabelFile = @fileId COLLATE NOCASE LIMIT 1",
            new { fileId }) > 0;
    }

    // ── IPropertyStatsProvider ───────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// Presence counts every observation that is not the special '(absent)'
    /// marker — this covers both the '(present)' encoding (boolean properties)
    /// and properties recorded with their actual value (e.g. TableGroup=Main).
    /// </remarks>
    public (long Present, long Total, double Ratio) GetPropertyPresenceRatio(string nodeType, string property)
    {
        using var conn = OpenReadOnly();
        var row = conn.QueryFirstOrDefault<(long Present, long Total)>(@"
            SELECT COALESCE(SUM(CASE WHEN Value != '(absent)' THEN Count ELSE 0 END), 0) AS Present,
                   COALESCE(SUM(Count), 0) AS Total
            FROM PropertyStats
            WHERE NodeType = @nodeType AND Property = @property",
            new { nodeType, property });
        return (row.Present, row.Total, row.Total > 0 ? (double)row.Present / row.Total : 0);
    }

    /// <inheritdoc />
    public IReadOnlyList<(string Value, long Count)> GetPropertyValueDistribution(string nodeType, string property, int limit = 5)
    {
        limit = ClampLimit(limit, 5);
        using var conn = OpenReadOnly();
        return conn.Query<(string Value, long Count)>(@"
            SELECT Value, SUM(Count) AS Count FROM PropertyStats
            WHERE NodeType = @nodeType AND Property = @property
              AND Value NOT IN ('(present)', '(absent)')
            GROUP BY Value ORDER BY Count DESC LIMIT @limit",
            new { nodeType, property, limit }).ToList();
    }

    /// <summary>
    /// Newest <c>Models.LastExtractedUtc</c> across all models — the staleness
    /// detector's view of "when did the index last see the workspace".
    /// </summary>
    public DateTime? GetNewestExtractTimestampUtc()
    {
        using var conn = OpenReadOnly();
        var raw = conn.ExecuteScalar<string?>("SELECT MAX(LastExtractedUtc) FROM Models");
        return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }

    /// <summary>True when the PropertyStats table holds at least one mined observation.</summary>
    public bool HasPropertyStats()
    {
        using var conn = OpenReadOnly();
        return conn.ExecuteScalar<long>("SELECT COUNT(1) FROM PropertyStats LIMIT 1") > 0;
    }

    /// <summary>
    /// Attributes on a class method (e.g. Hookable/Wrappable) — used by
    /// <c>prepare change</c> to determine CoC eligibility.
    /// </summary>
    public IReadOnlyList<(string AttributeName, string? RawArgs)> GetMethodAttributes(string className, string methodName)
    {
        using var conn = OpenReadOnly();
        return conn.Query<(string AttributeName, string? RawArgs)>(@"
            SELECT a.AttributeName, a.RawArgs
            FROM ClassAttributes a JOIN Classes c ON a.ClassId = c.ClassId
            WHERE c.Name = @className COLLATE NOCASE
              AND a.MethodName = @methodName COLLATE NOCASE",
            new { className, methodName }).ToList();
    }

    /// <summary>
    /// Similar existing objects of one kind (LIKE on a name fragment) — used by
    /// <c>prepare create</c> to point the agent at patterns worth copying.
    /// </summary>
    public IReadOnlyList<(string Name, string Model)> FindSimilarObjects(string kind, string needle, int limit = 5)
    {
        limit = ClampLimit(limit, 5);
        var table = kind.ToLowerInvariant() switch
        {
            "table" => "Tables",
            "class" => "Classes",
            "edt" => "Edts",
            "enum" => "Enums",
            "form" => "Forms",
            "query" => "Queries",
            "view" => "Views",
            "data-entity" or "entity" => "DataEntities",
            "report" => "Reports",
            _ => "Classes",
        };
        using var conn = OpenReadOnly();
        return conn.Query<(string Name, string Model)>($@"
            SELECT t.Name, m.Name AS Model
            FROM {table} t JOIN Models m ON m.ModelId = t.ModelId
            WHERE t.Name LIKE @pattern ESCAPE '\'
            ORDER BY LENGTH(t.Name) LIMIT @limit",
            new { pattern = "%" + EscapeLike(needle) + "%", limit }).ToList();
    }

    // ── Property-stats mining ────────────────────────────────────────────────

    /// <summary>
    /// Mine property statistics from one extract batch. Only STANDARD
    /// (non-custom) models are mined — the stats answer "what does the standard
    /// platform do", not "what did our customizations do". Observations are
    /// buffered in memory and written in one batch per model (the upstream
    /// build-database perf optimization).
    /// </summary>
    private static void MinePropertyStats(Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction tx, ExtractBatch batch)
    {
        // Clear stale stats in both directions: a model that flipped from
        // standard to custom must not keep contributing observations.
        conn.Execute("DELETE FROM PropertyStats WHERE Model=@m", new { m = batch.Model }, tx);
        if (batch.IsCustom) return;

        var buffer = new Dictionary<(string NodeType, string Property, string Value), long>();
        void Record(string nodeType, string property, string value)
        {
            var key = (nodeType, property, value);
            buffer[key] = buffer.TryGetValue(key, out var c) ? c + 1 : 1;
        }
        static string Presence(bool present) => present ? "(present)" : "(absent)";

        foreach (var t in batch.Tables)
        {
            // Extractors default the label to the table name — same value means no real label.
            var hasLabel = !string.IsNullOrEmpty(t.Label) && !string.Equals(t.Label, t.Name, StringComparison.OrdinalIgnoreCase);
            Record("AxTable", "Label", Presence(hasLabel));
            Record("AxTable", "TableGroup", string.IsNullOrEmpty(t.TableGroup) ? "(absent)" : t.TableGroup!);
            Record("AxTable", "ClusteredIndex", Presence(!string.IsNullOrEmpty(t.ClusteredIndex)));
            Record("AxTable", "AlternateKeyIndex", Presence(t.Indexes.Any(i => i.AlternateKey)));
            Record("AxTable", "CacheLookup", string.IsNullOrEmpty(t.CacheLookup) ? "(absent)" : t.CacheLookup!);
            foreach (var f in t.Fields)
            {
                var typed = !string.IsNullOrEmpty(f.EdtName)
                    || (f.Type?.Contains("Enum", StringComparison.OrdinalIgnoreCase) ?? false);
                Record("AxTableField", "ExtendedDataType", Presence(typed));
            }
        }

        if (buffer.Count == 0) return;
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO PropertyStats(NodeType, Property, Value, Model, Count)
                            VALUES(@nt, @p, @v, @m, @c)
                            ON CONFLICT(NodeType, Property, Value, Model) DO UPDATE SET Count = Count + excluded.Count";
        var pNt = cmd.CreateParameter(); pNt.ParameterName = "@nt"; cmd.Parameters.Add(pNt);
        var pP = cmd.CreateParameter(); pP.ParameterName = "@p"; cmd.Parameters.Add(pP);
        var pV = cmd.CreateParameter(); pV.ParameterName = "@v"; cmd.Parameters.Add(pV);
        var pM = cmd.CreateParameter(); pM.ParameterName = "@m"; pM.Value = batch.Model; cmd.Parameters.Add(pM);
        var pC = cmd.CreateParameter(); pC.ParameterName = "@c"; cmd.Parameters.Add(pC);
        cmd.Prepare();
        foreach (var ((nodeType, property, value), count) in buffer)
        {
            pNt.Value = nodeType;
            pP.Value = property;
            pV.Value = value;
            pC.Value = count;
            cmd.ExecuteNonQuery();
        }
    }
}
