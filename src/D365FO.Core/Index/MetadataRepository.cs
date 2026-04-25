using System.Data;
using System.Reflection;
using Dapper;
using Microsoft.Data.Sqlite;

namespace D365FO.Core.Index;

/// <summary>
/// Thin repository over the SQLite metadata index.
/// Stateless: every public method opens and disposes its own connection so the
/// repository is safe to use both from short-lived CLI processes and from a
/// long-running daemon / MCP host.
/// </summary>
public sealed class MetadataRepository
{
    /// <summary>Current schema version tracked in PRAGMA user_version.</summary>
    public const int CurrentSchemaVersion = 8;

    private static readonly Lazy<string> SchemaSql = new(LoadEmbeddedSchema);

    private readonly string _connectionString;

    static MetadataRepository()
    {
        // SQLite stores booleans as INTEGER; teach Dapper the conversion once.
        SqlMapper.AddTypeHandler(new SqliteBoolHandler());
    }

    public MetadataRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must be provided.", nameof(databasePath));

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Private cache: we intentionally want one logical DB per process.
            // Shared cache caused subtle WAL-plus-multi-writer contention in
            // multi-CLI / daemon cross-usage. Private is safer and the perf
            // cost for a single-writer workload is negligible.
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    /// <summary>
    /// Ensure schema is applied. Skips the CREATE script when PRAGMA
    /// user_version already matches the current version, so subsequent CLI
    /// invocations pay only the cost of opening a connection.
    /// Returns <c>true</c> when the schema was (re)applied, <c>false</c>
    /// when the DB was already at the current version.
    /// </summary>
    public bool EnsureSchema()
    {
        using var conn = Open();
        var current = conn.ExecuteScalar<long>("PRAGMA user_version");
        if (current == CurrentSchemaVersion) return false;

        conn.Execute(SchemaSql.Value);
        // v7 migration: add new columns on pre-existing Models tables. SQLite
        // lacks `ADD COLUMN IF NOT EXISTS`, so we check via PRAGMA table_info
        // rather than relying on a benign exception.
        if (current < 7)
        {
            var existingCols = conn.Query<string>("SELECT name FROM pragma_table_info('Models')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!existingCols.Contains("LastExtractedUtc"))
                conn.Execute("ALTER TABLE Models ADD COLUMN LastExtractedUtc TEXT");
            if (!existingCols.Contains("SourceFingerprint"))
                conn.Execute("ALTER TABLE Models ADD COLUMN SourceFingerprint TEXT");
        }
        if (current < 8)
        {
            // v8: Form pattern columns. Backfilled lazily — extractor will
            // populate them on the next `index extract` / `refresh`.
            var formCols = conn.Query<string>("SELECT name FROM pragma_table_info('Forms')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!formCols.Contains("Pattern")) conn.Execute("ALTER TABLE Forms ADD COLUMN Pattern TEXT");
            if (!formCols.Contains("PatternVersion")) conn.Execute("ALTER TABLE Forms ADD COLUMN PatternVersion TEXT");
            if (!formCols.Contains("Style")) conn.Execute("ALTER TABLE Forms ADD COLUMN Style TEXT");
            if (!formCols.Contains("TitleDataSource")) conn.Execute("ALTER TABLE Forms ADD COLUMN TitleDataSource TEXT");
            var dsCols = conn.Query<string>("SELECT name FROM pragma_table_info('FormDataSources')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!dsCols.Contains("OrderIndex")) conn.Execute("ALTER TABLE FormDataSources ADD COLUMN OrderIndex INTEGER NOT NULL DEFAULT 0");
            if (!dsCols.Contains("JoinSource")) conn.Execute("ALTER TABLE FormDataSources ADD COLUMN JoinSource TEXT");
        }
        conn.Execute($"PRAGMA user_version = {CurrentSchemaVersion}");
        conn.Execute(
            "INSERT OR IGNORE INTO SchemaVersion(Version, AppliedUtc) VALUES(@v, @t)",
            new { v = CurrentSchemaVersion, t = DateTime.UtcNow.ToString("O") });

        // Backfill FTS5 index from pre-existing Labels rows. This is a no-op
        // when Labels is empty or when the FTS5 mirror is already in sync
        // (SQLite's rebuild command is cheap in that case).
        try
        {
            conn.Execute("INSERT INTO LabelFts(LabelFts) VALUES('rebuild')");
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Host SQLite build lacks FTS5 — degrade gracefully; SearchLabels()
            // stays on the LIKE fallback path.
        }
        return true;
    }

    public IReadOnlyList<ClassInfo> SearchClasses(string query, string? model = null, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        var sql = @"
            SELECT c.ClassId, c.Name, m.Name AS Model, c.ExtendsName AS Extends,
                   c.IsAbstract, c.IsFinal, c.SourcePath
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.Name LIKE @like
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY c.Name
            LIMIT @limit";
        return conn.Query<ClassInfo>(sql, new { like, model, limit }).ToList();
    }

    public ClassDetails? GetClassDetails(string name)
    {
        using var conn = Open();
        var cls = conn.QueryFirstOrDefault<ClassInfo>(@"
            SELECT c.ClassId, c.Name, m.Name AS Model, c.ExtendsName AS Extends,
                   c.IsAbstract, c.IsFinal, c.SourcePath
            FROM Classes c JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.Name = @name LIMIT 1", new { name });
        if (cls is null) return null;

        var methods = conn.Query<MethodInfo>(@"
            SELECT Name, Signature, ReturnType, IsStatic
            FROM Methods WHERE ClassId = @id ORDER BY Name",
            new { id = cls.ClassId }).ToList();

        return new ClassDetails(cls, methods);
    }

    public TableDetails? GetTableDetails(string name)
    {
        using var conn = Open();
        var table = conn.QueryFirstOrDefault<TableInfo>(@"
            SELECT t.TableId, t.Name, m.Name AS Model, t.Label, t.SourcePath
            FROM Tables t JOIN Models m ON m.ModelId = t.ModelId
            WHERE t.Name = @name LIMIT 1", new { name });
        if (table is null) return null;

        var fields = conn.Query<TableFieldInfo>(@"
            SELECT Name, Type, EdtName, Label, Mandatory
            FROM TableFields WHERE TableId = @id ORDER BY FieldId",
            new { id = table.TableId }).ToList();

        var relations = conn.Query<RelationInfo>(@"
            SELECT FromTable, ToTable, Cardinality, RelationName
            FROM Relations WHERE FromTable = @n OR ToTable = @n",
            new { n = name }).ToList();

        var methods = conn.Query<TableMethodInfo>(@"
            SELECT Name, Signature, ReturnType, IsStatic
            FROM TableMethods WHERE TableId = @id ORDER BY Name",
            new { id = table.TableId }).ToList();

        var indexes = conn.Query<TableIndexInfo>(@"
            SELECT Name, AllowDuplicates, AlternateKey, FieldsCsv
            FROM TableIndexes WHERE TableId = @id ORDER BY Name",
            new { id = table.TableId }).ToList();

        var deleteActions = conn.Query<TableDeleteActionInfo>(@"
            SELECT Name, RelatedTable, DeleteAction
            FROM TableDeleteActions WHERE TableId = @id ORDER BY RelatedTable",
            new { id = table.TableId }).ToList();

        return new TableDetails(table, fields, relations, methods, indexes, deleteActions);
    }

    public EdtInfo? GetEdt(string name)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<EdtInfo>(@"
            SELECT e.Name, m.Name AS Model, e.ExtendsName AS Extends,
                   e.BaseType, e.Label, e.StringSize
            FROM Edts e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name = @name LIMIT 1", new { name });
    }

    public IReadOnlyList<CocExtensionInfo> FindCocExtensions(string targetClass, string? targetMethod = null)
    {
        using var conn = Open();
        var sql = @"
            SELECT c.TargetClass, c.TargetMethod, c.ExtensionClass, m.Name AS Model
            FROM CocExtensions c JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.TargetClass = @cls
              AND (@method IS NULL OR c.TargetMethod = @method)
            ORDER BY c.TargetMethod, c.ExtensionClass";
        return conn.Query<CocExtensionInfo>(sql, new { cls = targetClass, method = targetMethod }).ToList();
    }

    public IReadOnlyList<LabelMatch> SearchLabels(string query, IReadOnlyCollection<string>? languages = null, int limit = 100)
    {
        using var conn = Open();
        var like = $"%{query}%";
        var langsLower = languages?.Select(l => l.ToLowerInvariant()).ToList();
        var sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE (Value LIKE @like OR Key LIKE @like)
              AND (@langs IS NULL OR LOWER(Language) IN @langs)
            ORDER BY LabelFile, Key
            LIMIT @limit";
        return conn.Query<LabelMatch>(sql, new { like, langs = langsLower, limit }).ToList();
    }

    /// <summary>
    /// FTS5-backed label search. Falls back to <see cref="SearchLabels"/>
    /// when the host SQLite build lacks FTS5. Supports standard FTS5 query
    /// syntax — phrases in quotes, NEAR, column filters like
    /// <c>Value:customer Key:invoice</c>.
    /// </summary>
    public IReadOnlyList<LabelMatch> SearchLabelsFts(string query, IReadOnlyCollection<string>? languages = null, int limit = 100)
    {
        using var conn = Open();
        var langsLower = languages?.Select(l => l.ToLowerInvariant()).ToList();
        try
        {
            var sql = @"
                SELECT LabelFile AS File, Language, Key, Value
                FROM LabelFts
                WHERE LabelFts MATCH @q
                  AND (@langs IS NULL OR LOWER(Language) IN @langs)
                ORDER BY rank
                LIMIT @limit";
            return conn.Query<LabelMatch>(sql, new { q = query, langs = langsLower, limit }).ToList();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Either FTS5 is absent or the query string isn't valid FTS syntax
            // — degrade to LIKE so callers always get *some* results.
            return SearchLabels(query, languages, limit);
        }
    }

    public MenuItemInfo? GetMenuItem(string name)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<MenuItemInfo>(@"
            SELECT mi.Name, mi.Kind, mi.Object, mi.ObjectType, mi.Label, m.Name AS Model
            FROM MenuItems mi JOIN Models m ON m.ModelId = mi.ModelId
            WHERE mi.Name = @name LIMIT 1", new { name });
    }

    public IReadOnlyList<RelationInfo> GetTableRelations(string table)
    {
        using var conn = Open();
        return conn.Query<RelationInfo>(@"
            SELECT FromTable, ToTable, Cardinality, RelationName
            FROM Relations WHERE FromTable = @n OR ToTable = @n",
            new { n = table }).ToList();
    }

    public SecurityCoverage GetSecurityCoverage(string objectName, string objectType)
    {
        using var conn = Open();
        var routes = conn.Query<SecurityRoute>(@"
            SELECT Role, Duty, Privilege, EntryPoint
            FROM SecurityMap
            WHERE ObjectName = @n AND ObjectType = @t
            ORDER BY Role, Duty, Privilege",
            new { n = objectName, t = objectType }).ToList();
        return new SecurityCoverage(objectName, objectType, routes);
    }

    public IReadOnlyList<ObjectExtensionInfo> FindExtensions(string targetName, string? kind = null)
    {
        using var conn = Open();
        return conn.Query<ObjectExtensionInfo>(@"
            SELECT e.Kind, e.TargetName, e.ExtensionName, m.Name AS Model, e.SourcePath
            FROM ObjectExtensions e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.TargetName = @t AND (@k IS NULL OR e.Kind = @k)
            ORDER BY e.Kind, e.ExtensionName",
            new { t = targetName, k = kind }).ToList();
    }

    public IReadOnlyList<EventSubscriberInfo> FindEventSubscribers(string sourceObject, string? sourceKind = null)
    {
        using var conn = Open();
        return conn.Query<EventSubscriberInfo>(@"
            SELECT s.SubscriberClass, s.SubscriberMethod, s.SourceKind, s.SourceObject,
                   s.SourceMember, s.EventType, m.Name AS Model
            FROM EventSubscribers s JOIN Models m ON m.ModelId = s.ModelId
            WHERE s.SourceObject = @o
              AND (@k IS NULL OR s.SourceKind = @k)
            ORDER BY s.SourceKind, s.SubscriberClass, s.SubscriberMethod",
            new { o = sourceObject, k = sourceKind }).ToList();
    }

    /// <summary>
    /// Returns indexed forms whose Microsoft pattern matches the filters.
    /// Used by <c>d365fo find form-patterns</c> to surface real reference
    /// forms ("show me every SimpleListDetails on a setup table") and to
    /// find forms that use a given primary table.
    /// </summary>
    public IReadOnlyList<FormPatternRow> FindFormPatterns(
        string? pattern = null,
        string? table = null,
        string? model = null,
        int limit = 50)
    {
        using var conn = Open();
        // Driving datasource = OrderIndex 0 (or any when none populated yet).
        var sql = @"
            SELECT f.Name, f.Pattern, f.PatternVersion, f.Style, f.TitleDataSource,
                   m.Name AS Model, f.SourcePath,
                   (SELECT TableName FROM FormDataSources d
                     WHERE d.FormId = f.FormId
                     ORDER BY d.OrderIndex, d.Id LIMIT 1) AS PrimaryTable,
                   (SELECT COUNT(*) FROM FormDataSources d WHERE d.FormId = f.FormId) AS DataSourceCount
            FROM Forms f JOIN Models m ON m.ModelId = f.ModelId
            WHERE (@pat IS NULL OR f.Pattern LIKE @patLike)
              AND (@tbl IS NULL OR EXISTS (
                    SELECT 1 FROM FormDataSources d
                    WHERE d.FormId = f.FormId AND d.TableName = @tbl))
              AND (@mdl IS NULL OR m.Name = @mdl)
            ORDER BY f.Pattern, f.Name
            LIMIT @lim";
        var patLike = pattern is null ? null : pattern + "%";
        return conn.Query<FormPatternRow>(sql, new
        {
            pat = pattern,
            patLike,
            tbl = table,
            mdl = model,
            lim = limit,
        }).ToList();
    }

    /// <summary>
    /// Aggregate counts of patterns across the whole index. Useful as the
    /// no-argument default of <c>find form-patterns</c> so callers can see
    /// what the catalogue looks like before drilling in.
    /// </summary>
    public IReadOnlyList<FormPatternSummary> SummarizeFormPatterns()
    {
        using var conn = Open();
        return conn.Query<FormPatternSummary>(@"
            SELECT CAST(COALESCE(NULLIF(Pattern, ''), '(none)') AS TEXT) AS Pattern,
                   CAST(COUNT(*) AS INTEGER) AS Count
            FROM Forms
            GROUP BY COALESCE(NULLIF(Pattern, ''), '(none)')
            ORDER BY Count DESC, Pattern").ToList();
    }

    public FormDetails? GetForm(string name)
    {
        using var conn = Open();
        var form = conn.QueryFirstOrDefault<FormInfo>(@"
            SELECT f.FormId, f.Name, m.Name AS Model, f.SourcePath
            FROM Forms f JOIN Models m ON m.ModelId = f.ModelId
            WHERE f.Name = @n LIMIT 1", new { n = name });
        if (form is null) return null;
        var ds = conn.Query<FormDataSourceInfo>(@"
            SELECT Name, TableName FROM FormDataSources WHERE FormId = @id",
            new { id = form.FormId }).ToList();
        return new FormDetails(form, ds);
    }

    public SecurityRoleDetails? GetSecurityRole(string name)
    {
        using var conn = Open();
        var header = conn.QueryFirstOrDefault<(string Name, string? Label, string Model)>(@"
            SELECT r.Name, r.Label, m.Name AS Model
            FROM SecurityRoles r JOIN Models m ON m.ModelId = r.ModelId
            WHERE r.Name = @n LIMIT 1", new { n = name });
        if (header.Name is null) return null;
        var duties = conn.Query<string>("SELECT Duty FROM SecurityRoleDuties WHERE Role=@r ORDER BY Duty", new { r = name }).ToList();
        var privs = conn.Query<string>("SELECT Privilege FROM SecurityRolePrivileges WHERE Role=@r ORDER BY Privilege", new { r = name }).ToList();
        return new SecurityRoleDetails(header.Name, header.Label, header.Model, duties, privs);
    }

    public SecurityDutyDetails? GetSecurityDuty(string name)
    {
        using var conn = Open();
        var header = conn.QueryFirstOrDefault<(string Name, string? Label, string Model)>(@"
            SELECT d.Name, d.Label, m.Name AS Model
            FROM SecurityDuties d JOIN Models m ON m.ModelId = d.ModelId
            WHERE d.Name = @n LIMIT 1", new { n = name });
        if (header.Name is null) return null;
        var privs = conn.Query<string>("SELECT Privilege FROM SecurityDutyPrivileges WHERE Duty=@d ORDER BY Privilege", new { d = name }).ToList();
        return new SecurityDutyDetails(header.Name, header.Label, header.Model, privs);
    }

    public SecurityPrivilegeDetails? GetSecurityPrivilege(string name)
    {
        using var conn = Open();
        var header = conn.QueryFirstOrDefault<(string Name, string? Label, string Model)>(@"
            SELECT p.Name, p.Label, m.Name AS Model
            FROM SecurityPrivileges p JOIN Models m ON m.ModelId = p.ModelId
            WHERE p.Name = @n LIMIT 1", new { n = name });
        if (header.Name is null) return null;
        var eps = conn.Query<SecurityEntryPointInfo>(@"
            SELECT ObjectName, ObjectType, ObjectChild, AccessLevel
            FROM SecurityPrivilegeEntryPoints WHERE Privilege = @p ORDER BY ObjectName",
            new { p = name }).ToList();
        return new SecurityPrivilegeDetails(header.Name, header.Label, header.Model, eps);
    }

    public IReadOnlyList<TableMethodInfo> GetTableMethods(string table)
    {
        using var conn = Open();
        return conn.Query<TableMethodInfo>(@"
            SELECT tm.Name, tm.Signature, tm.ReturnType, tm.IsStatic
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            WHERE t.Name = @n ORDER BY tm.Name", new { n = table }).ToList();
    }

    public IReadOnlyList<TableIndexInfo> GetTableIndexes(string table)
    {
        using var conn = Open();
        return conn.Query<TableIndexInfo>(@"
            SELECT ti.Name, ti.AllowDuplicates, ti.AlternateKey, ti.FieldsCsv
            FROM TableIndexes ti
            JOIN Tables t ON t.TableId = ti.TableId
            WHERE t.Name = @n ORDER BY ti.Name", new { n = table }).ToList();
    }

    public IReadOnlyList<TableDeleteActionInfo> GetTableDeleteActions(string table)
    {
        using var conn = Open();
        return conn.Query<TableDeleteActionInfo>(@"
            SELECT da.Name, da.RelatedTable, da.DeleteAction
            FROM TableDeleteActions da
            JOIN Tables t ON t.TableId = da.TableId
            WHERE t.Name = @n ORDER BY da.RelatedTable", new { n = table }).ToList();
    }

    // ---- Lint heuristics (ROADMAP §7.1) ----

    /// <summary>
    /// Tables that don't have any index. Always a BP violation — the DB
    /// sync falls back to synthetic clustering and joins get expensive.
    /// Restricted to custom models by default so platform noise is filtered.
    /// </summary>
    public IReadOnlyList<LintHit> FindTablesWithoutIndex(bool onlyCustomModels = true)
    {
        using var conn = Open();
        var sql = @"
            SELECT t.Name AS TargetName, m.Name AS Model
            FROM Tables t
            JOIN Models m ON m.ModelId = t.ModelId
            LEFT JOIN TableIndexes ti ON ti.TableId = t.TableId
            WHERE ti.TableId IS NULL
              AND (@custom = 0 OR m.IsCustom = 1)
            ORDER BY m.Name, t.Name";
        return conn.Query(sql, new { custom = onlyCustomModels ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, null)).ToList();
    }

    /// <summary>
    /// Classes whose name ends with <c>_Extension</c> but where no method
    /// carries an <c>ExtensionOf</c> attribute — typical false extension
    /// from copy-paste scaffolding.
    /// </summary>
    public IReadOnlyList<LintHit> FindExtensionNamedButNotAttributed(bool onlyCustomModels = true)
    {
        using var conn = Open();
        var sql = @"
            SELECT c.Name AS TargetName, m.Name AS Model
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.Name LIKE '%\_Extension' ESCAPE '\'
              AND (@custom = 0 OR m.IsCustom = 1)
              AND NOT EXISTS (
                  SELECT 1 FROM ClassAttributes a
                  WHERE a.ClassId = c.ClassId AND a.AttributeName = 'ExtensionOf')
            ORDER BY m.Name, c.Name";
        return conn.Query(sql, new { custom = onlyCustomModels ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, null)).ToList();
    }

    /// <summary>
    /// Table fields with type <c>String</c> that do not reference an
    /// ExtendedDataType. Violates the "use EDTs for all string columns" BP.
    /// </summary>
    public IReadOnlyList<LintHit> FindStringFieldsWithoutEdt(bool onlyCustomModels = true)
    {
        using var conn = Open();
        var sql = @"
            SELECT (t.Name || '.' || f.Name) AS TargetName, m.Name AS Model, f.Type AS Detail
            FROM TableFields f
            JOIN Tables t ON t.TableId = f.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE f.Type = 'String'
              AND (f.EdtName IS NULL OR f.EdtName = '')
              AND (@custom = 0 OR m.IsCustom = 1)
            ORDER BY m.Name, t.Name, f.Name";
        return conn.Query(sql, new { custom = onlyCustomModels ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    // ---- v4: queries / views / data entities / reports / services / workflow ----

    public IReadOnlyList<QueryInfo> SearchQueries(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<QueryInfo>(@"
            SELECT q.QueryId, q.Name, m.Name AS Model, q.SourcePath
            FROM Queries q JOIN Models m ON m.ModelId = q.ModelId
            WHERE q.Name LIKE @like ORDER BY q.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public QueryDetails? GetQuery(string name)
    {
        using var conn = Open();
        var q = conn.QueryFirstOrDefault<QueryInfo>(@"
            SELECT q.QueryId, q.Name, m.Name AS Model, q.SourcePath
            FROM Queries q JOIN Models m ON m.ModelId = q.ModelId
            WHERE q.Name = @n LIMIT 1", new { n = name });
        if (q is null) return null;
        var ds = conn.Query<QueryDataSourceInfo>(@"
            SELECT Name, TableName, JoinMode, ParentDs FROM QueryDataSources WHERE QueryId = @id",
            new { id = q.QueryId }).ToList();
        return new QueryDetails(q, ds);
    }

    public IReadOnlyList<ViewInfo> SearchViews(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<ViewInfo>(@"
            SELECT v.ViewId, v.Name, m.Name AS Model, v.Label, v.QueryName, v.SourcePath
            FROM Views v JOIN Models m ON m.ModelId = v.ModelId
            WHERE v.Name LIKE @like ORDER BY v.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public ViewDetails? GetView(string name)
    {
        using var conn = Open();
        var v = conn.QueryFirstOrDefault<ViewInfo>(@"
            SELECT v.ViewId, v.Name, m.Name AS Model, v.Label, v.QueryName, v.SourcePath
            FROM Views v JOIN Models m ON m.ModelId = v.ModelId
            WHERE v.Name = @n LIMIT 1", new { n = name });
        if (v is null) return null;
        var fields = conn.Query<ViewFieldInfo>(@"
            SELECT Name, DataSource, DataField FROM ViewFields WHERE ViewId = @id",
            new { id = v.ViewId }).ToList();
        return new ViewDetails(v, fields);
    }

    public IReadOnlyList<DataEntityInfo> SearchDataEntities(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<DataEntityInfo>(@"
            SELECT e.EntityId, e.Name, m.Name AS Model, e.PublicEntityName, e.PublicCollectionName,
                   e.StagingTable, e.QueryName, e.Label, e.SourcePath
            FROM DataEntities e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name LIKE @like OR e.PublicEntityName LIKE @like OR e.PublicCollectionName LIKE @like
            ORDER BY e.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public DataEntityDetails? GetDataEntity(string name)
    {
        using var conn = Open();
        var e = conn.QueryFirstOrDefault<DataEntityInfo>(@"
            SELECT e.EntityId, e.Name, m.Name AS Model, e.PublicEntityName, e.PublicCollectionName,
                   e.StagingTable, e.QueryName, e.Label, e.SourcePath
            FROM DataEntities e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name = @n OR e.PublicEntityName = @n LIMIT 1", new { n = name });
        if (e is null) return null;
        var fields = conn.Query<DataEntityFieldInfo>(@"
            SELECT Name, DataSource, DataField, IsMandatory, IsReadOnly
            FROM DataEntityFields WHERE EntityId = @id", new { id = e.EntityId }).ToList();
        return new DataEntityDetails(e, fields);
    }

    public IReadOnlyList<ReportInfo> SearchReports(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<ReportInfo>(@"
            SELECT r.ReportId, r.Name, r.Kind, m.Name AS Model, r.SourcePath
            FROM Reports r JOIN Models m ON m.ModelId = r.ModelId
            WHERE r.Name LIKE @like ORDER BY r.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public ReportDetails? GetReport(string name)
    {
        using var conn = Open();
        var r = conn.QueryFirstOrDefault<ReportInfo>(@"
            SELECT r.ReportId, r.Name, r.Kind, m.Name AS Model, r.SourcePath
            FROM Reports r JOIN Models m ON m.ModelId = r.ModelId
            WHERE r.Name = @n LIMIT 1", new { n = name });
        if (r is null) return null;
        var ds = conn.Query<ReportDataSetInfo>(@"
            SELECT Name, Kind, QueryOrClass FROM ReportDataSets WHERE ReportId = @id",
            new { id = r.ReportId }).ToList();
        return new ReportDetails(r, ds);
    }

    public IReadOnlyList<ServiceInfo> SearchServices(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<ServiceInfo>(@"
            SELECT s.ServiceId, s.Name, s.Class, m.Name AS Model, s.SourcePath
            FROM Services s JOIN Models m ON m.ModelId = s.ModelId
            WHERE s.Name LIKE @like ORDER BY s.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public ServiceDetails? GetService(string name)
    {
        using var conn = Open();
        var s = conn.QueryFirstOrDefault<ServiceInfo>(@"
            SELECT s.ServiceId, s.Name, s.Class, m.Name AS Model, s.SourcePath
            FROM Services s JOIN Models m ON m.ModelId = s.ModelId
            WHERE s.Name = @n LIMIT 1", new { n = name });
        if (s is null) return null;
        var ops = conn.Query<ServiceOperationInfo>(@"
            SELECT OperationName, MethodName FROM ServiceOperations WHERE ServiceId = @id",
            new { id = s.ServiceId }).ToList();
        return new ServiceDetails(s, ops);
    }

    public ServiceGroupDetails? GetServiceGroup(string name)
    {
        using var conn = Open();
        var g = conn.QueryFirstOrDefault<ServiceGroupInfo>(@"
            SELECT g.GroupId, g.Name, m.Name AS Model, g.SourcePath
            FROM ServiceGroups g JOIN Models m ON m.ModelId = g.ModelId
            WHERE g.Name = @n LIMIT 1", new { n = name });
        if (g is null) return null;
        var members = conn.Query<string>(@"
            SELECT ServiceName FROM ServiceGroupMembers WHERE GroupId = @id ORDER BY ServiceName",
            new { id = g.GroupId }).ToList();
        return new ServiceGroupDetails(g, members);
    }

    public IReadOnlyList<WorkflowTypeInfo> SearchWorkflowTypes(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<WorkflowTypeInfo>(@"
            SELECT w.Name, w.Category, w.DocumentClass, m.Name AS Model, w.SourcePath
            FROM WorkflowTypes w JOIN Models m ON m.ModelId = w.ModelId
            WHERE w.Name LIKE @like OR w.DocumentClass LIKE @like
            ORDER BY w.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public IReadOnlyList<ModelInfo> ListModels()
    {
        using var conn = Open();
        return conn.Query<ModelInfo>(@"
            SELECT ModelId, Name, Publisher, Layer, IsCustom
            FROM Models ORDER BY Name").ToList();
    }

    public ModelDependencies? GetModelDependencies(string name)
    {
        using var conn = Open();
        var mi = conn.QueryFirstOrDefault<ModelInfo>(@"
            SELECT ModelId, Name, Publisher, Layer, IsCustom
            FROM Models WHERE Name = @name LIMIT 1", new { name });
        if (mi is null) return null;
        var dependsOn = conn.Query<string>(@"
            SELECT Target FROM ModelDependencies
            WHERE ModelId = @id ORDER BY Target", new { id = mi.ModelId }).ToList();
        var dependedBy = conn.Query<string>(@"
            SELECT m.Name FROM ModelDependencies d
            JOIN Models m ON m.ModelId = d.ModelId
            WHERE d.Target = @n ORDER BY m.Name", new { n = name }).ToList();
        return new ModelDependencies(mi, dependsOn, dependedBy);
    }

    /// <summary>
    /// Return every <c>Models.Name</c> → list of <c>ModelDependencies.Target</c>
    /// edges. Used by the coupling-metrics command (ROADMAP §6.2).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> GetDependencyGraph()
    {
        using var conn = Open();
        var rows = conn.Query<(string Source, string Target)>(@"
            SELECT m.Name AS Source, d.Target AS Target
            FROM ModelDependencies d
            JOIN Models m ON m.ModelId = d.ModelId");
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (!graph.TryGetValue(r.Source, out var list))
            {
                list = new List<string>();
                graph[r.Source] = list;
            }
            if (!list.Contains(r.Target, StringComparer.OrdinalIgnoreCase))
                list.Add(r.Target);
        }
        // Ensure every model is a node even if it has no outgoing edges.
        foreach (var m in conn.Query<string>("SELECT Name FROM Models"))
        {
            if (!graph.ContainsKey(m)) graph[m] = new List<string>();
        }
        return graph.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // ---- additional read operations ----

    public IReadOnlyList<TableInfo> SearchTables(string query, string? model = null, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<TableInfo>(@"
            SELECT t.TableId, t.Name, m.Name AS Model, t.Label, t.SourcePath
            FROM Tables t JOIN Models m ON m.ModelId = t.ModelId
            WHERE t.Name LIKE @like
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY t.Name
            LIMIT @limit", new { like, model, limit }).ToList();
    }

    public IReadOnlyList<EdtInfo> SearchEdts(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<EdtInfo>(@"
            SELECT e.Name, m.Name AS Model, e.ExtendsName AS Extends,
                   e.BaseType, e.Label, e.StringSize
            FROM Edts e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name LIKE @like
            ORDER BY e.Name
            LIMIT @limit", new { like, limit }).ToList();
    }

    public IReadOnlyList<EnumInfo> SearchEnums(string query, int limit = 50)
    {
        using var conn = Open();
        var like = $"%{query}%";
        return conn.Query<EnumInfo>(@"
            SELECT e.Name, m.Name AS Model, e.Label
            FROM Enums e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name LIKE @like
            ORDER BY e.Name
            LIMIT @limit", new { like, limit }).ToList();
    }

    public EnumDetails? GetEnum(string name)
    {
        using var conn = Open();
        var en = conn.QueryFirstOrDefault<EnumHeaderRow>(@"
            SELECT e.EnumId AS EnumId, e.Name AS Name, m.Name AS Model, e.Label AS Label
            FROM Enums e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name = @name LIMIT 1", new { name });
        if (en is null) return null;
        var values = conn.Query<EnumValueInfo>(@"
            SELECT Name, Value, Label
            FROM EnumValues WHERE EnumId = @id
            ORDER BY COALESCE(Value, EnumValueId)", new { id = en.EnumId }).ToList();
        return new EnumDetails(new EnumInfo(en.Name, en.Model, en.Label), values);
    }

    private sealed record EnumHeaderRow(long EnumId, string Name, string Model, string? Label);
    private sealed record UsageRow(string Kind, string Name, string Model);

    public LabelMatch? GetLabel(string file, string language, string key)
    {
        using var conn = Open();
        return conn.QueryFirstOrDefault<LabelMatch>(@"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE LabelFile = @file COLLATE NOCASE
              AND Language  = @lang COLLATE NOCASE
              AND Key       = @key  COLLATE NOCASE
            LIMIT 1", new { file, lang = language, key });
    }

    /// <summary>
    /// Resolve a label token like "@SYS12345" (the '@' is optional). Tries to
    /// split the alphabetic prefix (= label file) from the remainder (= key)
    /// and return one row per requested language.
    /// </summary>
    public IReadOnlyList<LabelMatch> ResolveLabel(string token, IReadOnlyCollection<string>? languages = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return Array.Empty<LabelMatch>();
        var raw = token.TrimStart('@').Trim();
        if (raw.Length == 0) return Array.Empty<LabelMatch>();

        // Split on first digit: "SYS12345" -> prefix="SYS", suffix="12345".
        int splitIdx = 0;
        while (splitIdx < raw.Length && char.IsLetter(raw[splitIdx])) splitIdx++;
        var prefix = splitIdx > 0 ? raw.Substring(0, splitIdx) : raw;
        var suffix = splitIdx < raw.Length ? raw.Substring(splitIdx) : string.Empty;

        using var conn = Open();
        // Try two shapes: Key == raw (e.g. "SYS12345") in file=prefix,
        // or Key == suffix (e.g. "12345") in file=prefix. Fall back to
        // exact key match across all files for odd prefixes.
        var sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE (@langs IS NULL OR Language IN @langs)
              AND (
                    (LabelFile = @prefix AND Key IN (@raw, @suffix))
                 OR Key = @raw
              )
            ORDER BY LabelFile, Language";
        return conn.Query<LabelMatch>(sql, new { langs = languages, prefix, raw, suffix }).ToList();
    }

    /// <summary>
    /// Find any index entity whose name contains the given substring. Used by
    /// `d365fo find usages` to approximate a cross-object search without
    /// loading X++ source itself.
    /// </summary>
    public IReadOnlyList<(string Kind, string Name, string Model)> FindUsages(string needle, int limit = 100)
    {
        using var conn = Open();
        var like = $"%{needle}%";
        var rows = conn.Query<UsageRow>(@"
            SELECT 'Table' AS Kind, t.Name AS Name, m.Name AS Model FROM Tables t JOIN Models m ON m.ModelId=t.ModelId WHERE t.Name LIKE @like
            UNION ALL
            SELECT 'Class', c.Name, m.Name FROM Classes c JOIN Models m ON m.ModelId=c.ModelId WHERE c.Name LIKE @like OR c.ExtendsName LIKE @like
            UNION ALL
            SELECT 'EDT',   e.Name, m.Name FROM Edts e JOIN Models m ON m.ModelId=e.ModelId WHERE e.Name LIKE @like OR e.ExtendsName LIKE @like
            UNION ALL
            SELECT 'Enum',  e.Name, m.Name FROM Enums e JOIN Models m ON m.ModelId=e.ModelId WHERE e.Name LIKE @like
            UNION ALL
            SELECT 'MenuItem', mi.Name, m.Name FROM MenuItems mi JOIN Models m ON m.ModelId=mi.ModelId WHERE mi.Name LIKE @like OR mi.Object LIKE @like
            UNION ALL
            SELECT 'Form',  f.Name, m.Name FROM Forms f JOIN Models m ON m.ModelId=f.ModelId WHERE f.Name LIKE @like
            UNION ALL
            SELECT 'Query', q.Name, m.Name FROM Queries q JOIN Models m ON m.ModelId=q.ModelId WHERE q.Name LIKE @like
            UNION ALL
            SELECT 'View',  v.Name, m.Name FROM Views v JOIN Models m ON m.ModelId=v.ModelId WHERE v.Name LIKE @like
            UNION ALL
            SELECT 'DataEntity', de.Name, m.Name FROM DataEntities de JOIN Models m ON m.ModelId=de.ModelId WHERE de.Name LIKE @like OR de.PublicEntityName LIKE @like
            UNION ALL
            SELECT 'Report', r.Name, m.Name FROM Reports r JOIN Models m ON m.ModelId=r.ModelId WHERE r.Name LIKE @like
            UNION ALL
            SELECT 'Service', s.Name, m.Name FROM Services s JOIN Models m ON m.ModelId=s.ModelId WHERE s.Name LIKE @like
            UNION ALL
            SELECT 'Workflow', w.Name, m.Name FROM WorkflowTypes w JOIN Models m ON m.ModelId=w.ModelId WHERE w.Name LIKE @like
            ORDER BY Name
            LIMIT @limit", new { like, limit });
        return rows.Select(r => (r.Kind, r.Name, r.Model)).ToList();
    }

    /// <summary>
    /// Enumerate (Kind, Name, Model, SourcePath) tuples for artifacts whose
    /// X++ source is worth scanning for reverse references. Filters to rows
    /// that have a non-empty SourcePath. Used by <c>find refs</c>.
    /// </summary>
    public IReadOnlyList<(string Kind, string Name, string Model, string SourcePath)> EnumerateSourcePaths(string? modelFilter = null)
    {
        using var conn = Open();
        var sql = @"
            SELECT 'Class' AS Kind, c.Name AS Name, m.Name AS Model, c.SourcePath AS SourcePath
              FROM Classes c JOIN Models m ON m.ModelId=c.ModelId
             WHERE c.SourcePath IS NOT NULL AND c.SourcePath <> ''
               AND (@model IS NULL OR m.Name = @model)
            UNION ALL
            SELECT 'Table', t.Name, m.Name, t.SourcePath
              FROM Tables t JOIN Models m ON m.ModelId=t.ModelId
             WHERE t.SourcePath IS NOT NULL AND t.SourcePath <> ''
               AND (@model IS NULL OR m.Name = @model)
            UNION ALL
            SELECT 'Form', f.Name, m.Name, f.SourcePath
              FROM Forms f JOIN Models m ON m.ModelId=f.ModelId
             WHERE f.SourcePath IS NOT NULL AND f.SourcePath <> ''
               AND (@model IS NULL OR m.Name = @model)";
        return conn.Query<SourcePathRow>(sql, new { model = modelFilter })
            .Select(r => (r.Kind, r.Name, r.Model, r.SourcePath))
            .ToList();
    }

    private sealed class SourcePathRow
    {
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
        public string SourcePath { get; set; } = "";
    }

    // ---- writer API used by the extract pipeline ----

    /// <summary>
    /// Return every model's current <c>SourceFingerprint</c> (may be null for
    /// pre-v7 rows or models that have never been through a fingerprint-aware
    /// refresh). Keyed by model name, case-insensitive.
    /// </summary>
    public IReadOnlyDictionary<string, string?> GetModelFingerprints()
    {
        using var conn = Open();
        var rows = conn.Query<(string Name, string? SourceFingerprint)>(
            "SELECT Name, SourceFingerprint FROM Models");
        var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) map[r.Name] = r.SourceFingerprint;
        return map;
    }

    /// <summary>
    /// Append one <c>ExtractionRuns</c> row. Cheap (single INSERT) and
    /// separate from <see cref="ApplyExtract(ExtractBatch)"/> so callers that extract
    /// speculatively (e.g. dry-run) can skip telemetry.
    /// </summary>
    public void RecordExtractionRun(string model, DateTime startedUtc, long elapsedMs,
        int tables, int classes, int edts, int enums, int labels, bool isCustom)
    {
        using var conn = Open();
        conn.Execute(@"INSERT INTO ExtractionRuns(StartedUtc, Model, ElapsedMs, Tables, Classes, Edts, Enums, Labels, IsCustom)
                       VALUES(@s, @m, @el, @t, @c, @ed, @en, @lb, @ic)",
            new
            {
                s = startedUtc.ToString("O"),
                m = model,
                el = elapsedMs,
                t = tables,
                c = classes,
                ed = edts,
                en = enums,
                lb = labels,
                ic = isCustom ? 1 : 0,
            });
    }

    /// <summary>Recent <c>ExtractionRuns</c>, newest first.</summary>
    public IReadOnlyList<ExtractionRunRow> GetExtractionRuns(int limit = 200, string? model = null)
    {
        using var conn = Open();
        var sql = @"SELECT RunId, StartedUtc, Model, ElapsedMs, Tables, Classes, Edts, Enums, Labels, IsCustom
                    FROM ExtractionRuns
                    WHERE (@model IS NULL OR Model = @model)
                    ORDER BY RunId DESC
                    LIMIT @limit";
        return conn.Query<ExtractionRunRow>(sql, new { model, limit }).ToList();
    }

    /// <summary>
    /// Upsert a <c>Models</c> row by name. Intended for callers that know the
    /// authoritative IsCustom/Publisher/Layer. If the row already exists the
    /// existing flags are preserved; the canonical way to refresh flags is
    /// <see cref="ApplyExtract(ExtractBatch)"/>, which re-runs this upsert and then issues
    /// an <c>UPDATE</c> with the authoritative descriptor values.
    /// </summary>
    public long UpsertModel(string name, string? publisher, string? layer, bool isCustom)
    {
        using var conn = Open();
        return UpsertModelInternal(conn, null, name, publisher, layer, isCustom);
    }

    // Invariant: <c>ApplyExtract</c> is the single source of truth for
    // <c>Models.IsCustom</c>. <c>UpsertModelInternal</c> only sets it on the
    // very first insert; later ApplyExtract calls refresh it via UPDATE so a
    // model's custom-flag always reflects the last successful extract, never a
    // stale best-effort guess from dep-graph traversal.
    internal long UpsertModelInternal(SqliteConnection conn, IDbTransaction? tx, string name, string? publisher, string? layer, bool isCustom)
    {
        var id = conn.ExecuteScalar<long?>("SELECT ModelId FROM Models WHERE Name = @n", new { n = name }, tx);
        if (id is not null) return id.Value;
        conn.Execute(@"INSERT INTO Models(Name, Publisher, Layer, IsCustom)
                       VALUES(@n, @p, @l, @c)", new { n = name, p = publisher, l = layer, c = isCustom ? 1 : 0 }, tx);
        return conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
    }

    /// <summary>
    /// Apply a batch of extracted records atomically. The writer clears any
    /// existing rows for the given model so the pipeline stays idempotent
    /// (re-extract = replace).
    /// </summary>
    public void ApplyExtract(ExtractBatch batch) => ApplyExtract(batch, sourceFingerprint: null);

    /// <summary>
    /// Apply a batch of extracted records atomically, stamping the model's
    /// <c>LastExtractedUtc</c> and (when provided) <c>SourceFingerprint</c>.
    /// </summary>
    public void ApplyExtract(ExtractBatch batch, string? sourceFingerprint)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var modelId = UpsertModelInternal(conn, tx, batch.Model, batch.Publisher, batch.Layer, batch.IsCustom);
        // Refresh publisher/layer + fingerprint metadata if the descriptor has been learned later.
        conn.Execute(@"UPDATE Models
                          SET Publisher=@p, Layer=@l, IsCustom=@c,
                              LastExtractedUtc=@u,
                              SourceFingerprint=COALESCE(@fp, SourceFingerprint)
                        WHERE ModelId=@m",
            new
            {
                p = batch.Publisher,
                l = batch.Layer,
                c = batch.IsCustom ? 1 : 0,
                u = DateTime.UtcNow.ToString("O"),
                fp = sourceFingerprint,
                m = modelId,
            }, tx);

        conn.Execute("DELETE FROM EnumValues WHERE EnumId IN (SELECT EnumId FROM Enums WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Enums WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM TableFields WHERE TableId IN (SELECT TableId FROM Tables WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM TableMethods WHERE TableId IN (SELECT TableId FROM Tables WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM TableIndexes WHERE TableId IN (SELECT TableId FROM Tables WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM TableDeleteActions WHERE TableId IN (SELECT TableId FROM Tables WHERE ModelId=@m)", new { m = modelId }, tx);
        // Relations are keyed globally by FromTable; clear any row whose
        // FromTable belongs to this model *before* dropping Tables.
        conn.Execute(@"DELETE FROM Relations
                       WHERE FromTable IN (SELECT Name FROM Tables WHERE ModelId=@m)",
                     new { m = modelId }, tx);
        conn.Execute("DELETE FROM Tables WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Methods WHERE ClassId IN (SELECT ClassId FROM Classes WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ClassAttributes WHERE ClassId IN (SELECT ClassId FROM Classes WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Classes WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Edts WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM MenuItems WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM CocExtensions WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM FormDataSources WHERE FormId IN (SELECT FormId FROM Forms WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Forms WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ObjectExtensions WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM EventSubscribers WHERE ModelId=@m", new { m = modelId }, tx);
        // Security: clear by model; link tables are global but only refer to
        // names we are about to re-insert.
        conn.Execute(@"DELETE FROM SecurityRoleDuties WHERE Role IN (SELECT Name FROM SecurityRoles WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute(@"DELETE FROM SecurityRolePrivileges WHERE Role IN (SELECT Name FROM SecurityRoles WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute(@"DELETE FROM SecurityDutyPrivileges WHERE Duty IN (SELECT Name FROM SecurityDuties WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute(@"DELETE FROM SecurityPrivilegeEntryPoints WHERE Privilege IN (SELECT Name FROM SecurityPrivileges WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute(@"DELETE FROM SecurityMap WHERE Role IN (SELECT Name FROM SecurityRoles WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM SecurityRoles WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM SecurityDuties WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM SecurityPrivileges WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM QueryDataSources WHERE QueryId IN (SELECT QueryId FROM Queries WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Queries WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ViewFields WHERE ViewId IN (SELECT ViewId FROM Views WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Views WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM DataEntityFields WHERE EntityId IN (SELECT EntityId FROM DataEntities WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM DataEntities WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ReportDataSets WHERE ReportId IN (SELECT ReportId FROM Reports WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Reports WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ServiceOperations WHERE ServiceId IN (SELECT ServiceId FROM Services WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Services WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ServiceGroupMembers WHERE GroupId IN (SELECT GroupId FROM ServiceGroups WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ServiceGroups WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM WorkflowTypes WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ModelDependencies WHERE ModelId=@m", new { m = modelId }, tx);
        // Labels are keyed by file+lang, not model; we delete by file instead.
        foreach (var file in batch.Labels.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase))
            conn.Execute("DELETE FROM Labels WHERE LabelFile=@f", new { f = file }, tx);

        foreach (var t in batch.Tables)
        {
            conn.Execute(@"INSERT INTO Tables(Name, ModelId, Label, SourcePath)
                           VALUES(@n, @m, @l, @p)",
                         new { n = t.Name, m = modelId, l = t.Label, p = t.SourcePath }, tx);
            var tableId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var f in t.Fields)
            {
                conn.Execute(@"INSERT INTO TableFields(TableId, Name, Type, EdtName, Label, Mandatory)
                               VALUES(@t, @n, @ty, @e, @l, @md)",
                             new { t = tableId, n = f.Name, ty = f.Type, e = f.EdtName, l = f.Label, md = f.Mandatory ? 1 : 0 }, tx);
            }
            foreach (var mtd in t.Methods)
            {
                conn.Execute(@"INSERT INTO TableMethods(TableId, Name, Signature, IsStatic, ReturnType)
                               VALUES(@t, @n, @s, @st, @rt)",
                             new { t = tableId, n = mtd.Name, s = mtd.Signature, st = mtd.IsStatic ? 1 : 0, rt = mtd.ReturnType }, tx);
            }
            foreach (var ix in t.Indexes)
            {
                conn.Execute(@"INSERT INTO TableIndexes(TableId, Name, AllowDuplicates, AlternateKey, FieldsCsv)
                               VALUES(@t, @n, @a, @k, @f)",
                             new { t = tableId, n = ix.Name, a = ix.AllowDuplicates ? 1 : 0, k = ix.AlternateKey ? 1 : 0, f = string.Join(",", ix.Fields) }, tx);
            }
            foreach (var da in t.DeleteActions)
            {
                conn.Execute(@"INSERT INTO TableDeleteActions(TableId, Name, RelatedTable, DeleteAction)
                               VALUES(@t, @n, @r, @a)",
                             new { t = tableId, n = da.Name, r = da.RelatedTable, a = da.DeleteAction }, tx);
            }
            foreach (var r in t.Relations)
            {
                conn.Execute(@"INSERT INTO Relations(FromTable, ToTable, Cardinality, RelationName)
                               VALUES(@f, @to, @c, @n)",
                             new { f = t.Name, to = r.RelatedTable, c = r.Cardinality, n = r.Name }, tx);
            }
        }

        foreach (var c in batch.Classes)
        {
            conn.Execute(@"INSERT INTO Classes(Name, ModelId, ExtendsName, IsAbstract, IsFinal, SourcePath)
                           VALUES(@n, @m, @e, @a, @f, @p)",
                         new { n = c.Name, m = modelId, e = c.Extends, a = c.IsAbstract ? 1 : 0, f = c.IsFinal ? 1 : 0, p = c.SourcePath }, tx);
            var classId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var mtd in c.Methods)
            {
                conn.Execute(@"INSERT INTO Methods(ClassId, Name, Signature, IsStatic, ReturnType)
                               VALUES(@c, @n, @s, @st, @rt)",
                             new { c = classId, n = mtd.Name, s = mtd.Signature, st = mtd.IsStatic ? 1 : 0, rt = mtd.ReturnType }, tx);
            }
            foreach (var a in c.Attributes)
            {
                conn.Execute(@"INSERT INTO ClassAttributes(ClassId, MethodName, AttributeName, RawArgs)
                               VALUES(@c, @m, @n, @a)",
                             new { c = classId, m = a.MethodName, n = a.AttributeName, a = a.RawArgs }, tx);
            }
        }

        foreach (var e in batch.Edts)
        {
            conn.Execute(@"INSERT INTO Edts(Name, ModelId, ExtendsName, BaseType, Label, StringSize)
                           VALUES(@n, @m, @e, @b, @l, @s)",
                         new { n = e.Name, m = modelId, e = e.Extends, b = e.BaseType, l = e.Label, s = e.StringSize }, tx);
        }

        foreach (var en in batch.Enums)
        {
            conn.Execute(@"INSERT INTO Enums(Name, ModelId, Label) VALUES(@n, @m, @l)",
                         new { n = en.Name, m = modelId, l = en.Label }, tx);
            var enumId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var v in en.Values)
            {
                conn.Execute(@"INSERT INTO EnumValues(EnumId, Name, Value, Label)
                               VALUES(@e, @n, @v, @l)",
                             new { e = enumId, n = v.Name, v = v.Value, l = v.Label }, tx);
            }
        }

        foreach (var mi in batch.MenuItems)
        {
            conn.Execute(@"INSERT INTO MenuItems(Name, Kind, Object, ObjectType, Label, ModelId)
                           VALUES(@n, @k, @o, @ot, @l, @m)",
                         new { n = mi.Name, k = mi.Kind, o = mi.Object, ot = mi.ObjectType, l = mi.Label, m = modelId }, tx);
        }

        foreach (var coc in batch.CocExtensions)
        {
            conn.Execute(@"INSERT INTO CocExtensions(TargetClass, TargetMethod, ExtensionClass, ModelId)
                           VALUES(@tc, @tm, @ec, @m)",
                         new { tc = coc.TargetClass, tm = coc.TargetMethod, ec = coc.ExtensionClass, m = modelId }, tx);
        }

        foreach (var l in batch.Labels)
        {
            conn.Execute(@"INSERT INTO Labels(LabelFile, Language, Key, Value) VALUES(@f, @lg, @k, @v)",
                         new { f = l.File, lg = l.Language, k = l.Key, v = l.Value }, tx);
        }

        foreach (var f in batch.Forms)
        {
            conn.Execute(@"INSERT INTO Forms(Name, ModelId, SourcePath, Pattern, PatternVersion, Style, TitleDataSource)
                           VALUES(@n, @m, @p, @pat, @pv, @st, @td)",
                         new { n = f.Name, m = modelId, p = f.SourcePath,
                               pat = f.Pattern, pv = f.PatternVersion, st = f.Style, td = f.TitleDataSource }, tx);
            var formId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            int idx = 0;
            foreach (var ds in f.DataSources)
            {
                conn.Execute(@"INSERT INTO FormDataSources(FormId, Name, TableName, OrderIndex, JoinSource)
                               VALUES(@f, @n, @t, @o, @j)",
                             new { f = formId, n = ds.Name, t = ds.Table, o = idx, j = ds.JoinSource }, tx);
                idx++;
            }
        }

        foreach (var ext in batch.Extensions)
        {
            conn.Execute(@"INSERT INTO ObjectExtensions(Kind, TargetName, ExtensionName, ModelId, SourcePath)
                           VALUES(@k, @t, @e, @m, @p)",
                         new { k = ext.Kind, t = ext.TargetName, e = ext.ExtensionName, m = modelId, p = ext.SourcePath }, tx);
        }

        foreach (var s in batch.EventSubscribers)
        {
            conn.Execute(@"INSERT INTO EventSubscribers(SubscriberClass, SubscriberMethod, SourceKind, SourceObject, SourceMember, EventType, ModelId)
                           VALUES(@sc, @sm, @sk, @so, @mm, @et, @mid)",
                         new { sc = s.SubscriberClass, sm = s.SubscriberMethod, sk = s.SourceKind, so = s.SourceObject, mm = s.SourceMember, et = s.EventType, mid = modelId }, tx);
        }

        foreach (var r in batch.Roles)
        {
            conn.Execute(@"INSERT INTO SecurityRoles(Name, Label, ModelId) VALUES(@n, @l, @m)",
                         new { n = r.Name, l = r.Label, m = modelId }, tx);
            foreach (var d in r.Duties.Distinct(StringComparer.OrdinalIgnoreCase))
                conn.Execute(@"INSERT INTO SecurityRoleDuties(Role, Duty) VALUES(@r, @d)",
                             new { r = r.Name, d }, tx);
            foreach (var p in r.Privileges.Distinct(StringComparer.OrdinalIgnoreCase))
                conn.Execute(@"INSERT INTO SecurityRolePrivileges(Role, Privilege) VALUES(@r, @p)",
                             new { r = r.Name, p }, tx);
        }
        foreach (var d in batch.Duties)
        {
            conn.Execute(@"INSERT INTO SecurityDuties(Name, Label, ModelId) VALUES(@n, @l, @m)",
                         new { n = d.Name, l = d.Label, m = modelId }, tx);
            foreach (var p in d.Privileges.Distinct(StringComparer.OrdinalIgnoreCase))
                conn.Execute(@"INSERT INTO SecurityDutyPrivileges(Duty, Privilege) VALUES(@d, @p)",
                             new { d = d.Name, p }, tx);
        }
        foreach (var p in batch.Privileges)
        {
            conn.Execute(@"INSERT INTO SecurityPrivileges(Name, Label, ModelId) VALUES(@n, @l, @m)",
                         new { n = p.Name, l = p.Label, m = modelId }, tx);
            foreach (var ep in p.EntryPoints)
                conn.Execute(@"INSERT INTO SecurityPrivilegeEntryPoints(Privilege, ObjectName, ObjectType, ObjectChild, AccessLevel)
                               VALUES(@p, @o, @t, @c, @a)",
                             new { p = p.Name, o = ep.ObjectName, t = ep.ObjectType, c = ep.ObjectChild, a = ep.AccessLevel }, tx);
        }

        foreach (var q in batch.Queries)
        {
            conn.Execute(@"INSERT INTO Queries(Name, ModelId, SourcePath) VALUES(@n, @m, @p)",
                         new { n = q.Name, m = modelId, p = q.SourcePath }, tx);
            var queryId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var ds in q.DataSources)
                conn.Execute(@"INSERT INTO QueryDataSources(QueryId, Name, TableName, JoinMode, ParentDs)
                               VALUES(@q, @n, @t, @j, @p)",
                             new { q = queryId, n = ds.Name, t = ds.Table, j = ds.JoinMode, p = ds.ParentDs }, tx);
        }
        foreach (var v in batch.Views)
        {
            conn.Execute(@"INSERT INTO Views(Name, ModelId, Label, QueryName, SourcePath) VALUES(@n, @m, @l, @q, @p)",
                         new { n = v.Name, m = modelId, l = v.Label, q = v.QueryName, p = v.SourcePath }, tx);
            var viewId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var f in v.Fields)
                conn.Execute(@"INSERT INTO ViewFields(ViewId, Name, DataSource, DataField) VALUES(@v, @n, @ds, @df)",
                             new { v = viewId, n = f.Name, ds = f.DataSource, df = f.DataField }, tx);
        }
        foreach (var e in batch.DataEntities)
        {
            conn.Execute(@"INSERT INTO DataEntities(Name, ModelId, PublicEntityName, PublicCollectionName, StagingTable, QueryName, Label, SourcePath)
                           VALUES(@n, @m, @pe, @pc, @st, @q, @l, @p)",
                         new { n = e.Name, m = modelId, pe = e.PublicEntityName, pc = e.PublicCollectionName, st = e.StagingTable, q = e.QueryName, l = e.Label, p = e.SourcePath }, tx);
            var entityId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var f in e.Fields)
                conn.Execute(@"INSERT INTO DataEntityFields(EntityId, Name, DataSource, DataField, IsMandatory, IsReadOnly)
                               VALUES(@e, @n, @ds, @df, @mn, @ro)",
                             new { e = entityId, n = f.Name, ds = f.DataSource, df = f.DataField, mn = f.IsMandatory ? 1 : 0, ro = f.IsReadOnly ? 1 : 0 }, tx);
        }
        foreach (var r in batch.Reports)
        {
            conn.Execute(@"INSERT INTO Reports(Name, Kind, ModelId, SourcePath) VALUES(@n, @k, @m, @p)",
                         new { n = r.Name, k = r.Kind, m = modelId, p = r.SourcePath }, tx);
            var reportId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var ds in r.DataSets)
                conn.Execute(@"INSERT INTO ReportDataSets(ReportId, Name, Kind, QueryOrClass) VALUES(@r, @n, @k, @q)",
                             new { r = reportId, n = ds.Name, k = ds.Kind, q = ds.QueryOrClass }, tx);
        }
        foreach (var s in batch.Services)
        {
            conn.Execute(@"INSERT INTO Services(Name, Class, ModelId, SourcePath) VALUES(@n, @c, @m, @p)",
                         new { n = s.Name, c = s.Class, m = modelId, p = s.SourcePath }, tx);
            var serviceId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var op in s.Operations)
                conn.Execute(@"INSERT INTO ServiceOperations(ServiceId, OperationName, MethodName) VALUES(@s, @o, @m)",
                             new { s = serviceId, o = op.OperationName, m = op.MethodName }, tx);
        }
        foreach (var g in batch.ServiceGroups)
        {
            conn.Execute(@"INSERT INTO ServiceGroups(Name, ModelId, SourcePath) VALUES(@n, @m, @p)",
                         new { n = g.Name, m = modelId, p = g.SourcePath }, tx);
            var groupId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var member in g.Members)
                conn.Execute(@"INSERT INTO ServiceGroupMembers(GroupId, ServiceName) VALUES(@g, @s)",
                             new { g = groupId, s = member }, tx);
        }
        foreach (var w in batch.WorkflowTypes)
        {
            conn.Execute(@"INSERT INTO WorkflowTypes(Name, Category, DocumentClass, ModelId, SourcePath)
                           VALUES(@n, @c, @d, @m, @p)",
                         new { n = w.Name, c = w.Category, d = w.DocumentClass, m = modelId, p = w.SourcePath }, tx);
        }
        foreach (var dep in batch.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            conn.Execute(@"INSERT INTO ModelDependencies(ModelId, Target) VALUES(@m, @t)",
                         new { m = modelId, t = dep }, tx);
        }

        tx.Commit();

        // Rebuild the flattened SecurityMap (Role x Duty x Privilege x
        // EntryPoint) for this model's roles/duties. We do this in a short
        // second transaction *after* commit so the join sees both this model's
        // data and any referenced duties/privileges from other models.
        RebuildSecurityMap(conn, modelId);
    }

    private static void RebuildSecurityMap(SqliteConnection conn, long modelId)
    {
        using var tx = conn.BeginTransaction();
        // Clear rows owned by this model's roles.
        conn.Execute(@"DELETE FROM SecurityMap WHERE Role IN (SELECT Name FROM SecurityRoles WHERE ModelId=@m)",
                     new { m = modelId }, tx);
        // Role → Duty → Privilege → EntryPoint
        conn.Execute(@"
            INSERT INTO SecurityMap(Role, Duty, Privilege, EntryPoint, ObjectName, ObjectType)
            SELECT r.Name, rd.Duty, dp.Privilege, ep.ObjectName, ep.ObjectName, ep.ObjectType
            FROM SecurityRoles r
            JOIN SecurityRoleDuties rd ON rd.Role = r.Name
            JOIN SecurityDutyPrivileges dp ON dp.Duty = rd.Duty
            LEFT JOIN SecurityPrivilegeEntryPoints ep ON ep.Privilege = dp.Privilege
            WHERE r.ModelId = @m", new { m = modelId }, tx);
        // Role → Privilege (direct) → EntryPoint
        conn.Execute(@"
            INSERT INTO SecurityMap(Role, Duty, Privilege, EntryPoint, ObjectName, ObjectType)
            SELECT r.Name, NULL, rp.Privilege, ep.ObjectName, ep.ObjectName, ep.ObjectType
            FROM SecurityRoles r
            JOIN SecurityRolePrivileges rp ON rp.Role = r.Name
            LEFT JOIN SecurityPrivilegeEntryPoints ep ON ep.Privilege = rp.Privilege
            WHERE r.ModelId = @m", new { m = modelId }, tx);
        tx.Commit();
    }

    public ExtractCounts CountAll()
    {
        using var conn = Open();
        return new ExtractCounts(
            Models: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Models"),
            Tables: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Tables"),
            Fields: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM TableFields"),
            Classes: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Classes"),
            Methods: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Methods"),
            Edts: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Edts"),
            Enums: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Enums"),
            MenuItems: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM MenuItems"),
            Labels: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Labels"),
            Coc: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM CocExtensions"))
        {
            Forms = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Forms"),
            Extensions = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM ObjectExtensions"),
            EventSubscribers = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM EventSubscribers"),
            Relations = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Relations"),
            Roles = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM SecurityRoles"),
            Duties = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM SecurityDuties"),
            Privileges = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM SecurityPrivileges"),
            Queries = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Queries"),
            Views = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Views"),
            DataEntities = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM DataEntities"),
            Reports = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Reports"),
            Services = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Services"),
            WorkflowTypes = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM WorkflowTypes"),
        };
    }

    /// <summary>
    /// Aggregate counters useful for <c>d365fo stats</c>. Each sub-query hits
    /// a single indexed column so the whole call finishes in tens of ms on a
    /// fully-ingested index.
    /// </summary>
    public IndexStats GetStats(int topN = 10)
    {
        using var conn = Open();

        var perModel = conn.Query<PerModelStat>(@"
            SELECT m.Name AS Model, m.IsCustom AS IsCustom,
                   (SELECT COUNT(*) FROM Tables t WHERE t.ModelId = m.ModelId) AS Tables,
                   (SELECT COUNT(*) FROM Classes c WHERE c.ModelId = m.ModelId) AS Classes,
                   (SELECT COUNT(*) FROM Edts e WHERE e.ModelId = m.ModelId) AS Edts,
                   (SELECT COUNT(*) FROM Enums en WHERE en.ModelId = m.ModelId) AS Enums,
                   (SELECT COUNT(*) FROM MenuItems mi WHERE mi.ModelId = m.ModelId) AS MenuItems,
                   (SELECT COUNT(*) FROM Forms f WHERE f.ModelId = m.ModelId) AS Forms,
                   (SELECT COUNT(*) FROM ObjectExtensions ox WHERE ox.ModelId = m.ModelId) AS Extensions,
                   (SELECT COUNT(*) FROM CocExtensions cx WHERE cx.ModelId = m.ModelId) AS Coc,
                   (SELECT COUNT(*) FROM Labels l WHERE l.ModelId = m.ModelId) AS Labels
            FROM Models m
            ORDER BY m.Name").ToList();

        var topTables = conn.Query<TopTableStat>(@"
            SELECT t.Name AS Name, m.Name AS Model, COUNT(f.FieldId) AS FieldCount
            FROM Tables t
            JOIN Models m ON m.ModelId = t.ModelId
            LEFT JOIN TableFields f ON f.TableId = t.TableId
            GROUP BY t.TableId, t.Name, m.Name
            ORDER BY FieldCount DESC, t.Name
            LIMIT @topN", new { topN }).ToList();

        var topClasses = conn.Query<TopClassStat>(@"
            SELECT c.Name AS Name, m.Name AS Model, COUNT(mt.MethodId) AS MethodCount
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            LEFT JOIN Methods mt ON mt.ClassId = c.ClassId
            GROUP BY c.ClassId, c.Name, m.Name
            ORDER BY MethodCount DESC, c.Name
            LIMIT @topN", new { topN }).ToList();

        var topCoc = conn.Query<TopCocStat>(@"
            SELECT c.TargetClass AS Target, COUNT(*) AS ExtensionCount
            FROM CocExtensions c
            GROUP BY c.TargetClass
            ORDER BY ExtensionCount DESC, c.TargetClass
            LIMIT @topN", new { topN }).ToList();

        return new IndexStats(perModel, topTables, topClasses, topCoc);
    }

    internal SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Per-connection pragmas: foreign_keys is a per-connection setting,
        // journal_mode WAL survives across connections but is cheap to re-assert.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static string LoadEmbeddedSchema()
    {
        var asm = typeof(MetadataRepository).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Schema.sql", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Schema.sql embedded resource missing.");
        using var s = asm.GetManifestResourceStream(resName)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }
}
