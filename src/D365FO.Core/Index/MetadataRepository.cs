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
public sealed partial class MetadataRepository
{
    /// <summary>Current schema version tracked in PRAGMA user_version.</summary>
    /// <remarks>
    /// v19: index on FormDataSources(FormId) — the one foreign key whose child column was
    /// unindexed, so enforcing it rescanned the child table once per deleted form.
    /// &lt;para&gt;v18: indexes on every ModelId and on the parent keys the per-model re-extract
    /// deletes filter by. Pure additions — an existing database picks them up on the next
    /// EnsureSchema, with no re-extract.&lt;/para&gt;
    /// &lt;para&gt;v17: LabelFiles table (per-label-file source paths for the disk check).&lt;/para&gt;
    /// </remarks>
    public const int CurrentSchemaVersion = 19;

    private static readonly Lazy<string> SchemaSql = new(LoadEmbeddedSchema);

    private readonly string _databasePath;
    private readonly string _connectionString;
    /// <summary>
    /// Read-only connection string used by all query-only methods. Using
    /// <see cref="SqliteOpenMode.ReadOnly"/> prevents accidental DB creation
    /// when the path is wrong and avoids acquiring a write-capable lock on
    /// read operations, which is important when the index DB is on a shared
    /// or read-only file system.
    /// </summary>
    private readonly string _readOnlyConnectionString;

    static MetadataRepository()
    {
        // SQLite stores booleans as INTEGER; teach Dapper the conversion once.
        SqlMapper.AddTypeHandler(new SqliteBoolHandler());
    }

    public MetadataRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database path must be provided.", nameof(databasePath));

        _databasePath = Path.GetFullPath(databasePath);

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

        _readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    /// <summary>Absolute path to the SQLite database file.</summary>
    public string DatabasePath => _databasePath;

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

        // Wrap all DDL + migrations in a single transaction so a mid-run crash
        // (CTRL+C, OOM, power failure) cannot leave the DB in a half-migrated
        // state where some ALTER TABLE columns exist but user_version is stale.
        using var tx = conn.BeginTransaction();

        // ---------------------------------------------------------------
        // ALTER TABLE migrations must run BEFORE conn.Execute(SchemaSql)
        // because Schema.sql contains "CREATE INDEX … ON Forms(Pattern)"
        // which fails if the Forms table already exists without that column.
        // Guard every ALTER TABLE with a table-existence check (Count > 0)
        // so that brand-new databases — where tables don't exist yet — are
        // handled safely by the subsequent Schema.sql CREATE TABLE statements.
        // ---------------------------------------------------------------

        // v7 migration: add new columns on pre-existing Models tables. SQLite
        // lacks `ADD COLUMN IF NOT EXISTS`, so we check via PRAGMA table_info
        // rather than relying on a benign exception.
        if (current < 7)
        {
            var existingCols = conn.Query<string>("SELECT name FROM pragma_table_info('Models')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (existingCols.Count > 0 && !existingCols.Contains("LastExtractedUtc"))
                conn.Execute("ALTER TABLE Models ADD COLUMN LastExtractedUtc TEXT", transaction: tx);
            if (existingCols.Count > 0 && !existingCols.Contains("SourceFingerprint"))
                conn.Execute("ALTER TABLE Models ADD COLUMN SourceFingerprint TEXT", transaction: tx);
        }
        if (current < 8)
        {
            // v8: Form pattern columns. Backfilled lazily — extractor will
            // populate them on the next `index extract` / `refresh`.
            var formCols = conn.Query<string>("SELECT name FROM pragma_table_info('Forms')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (formCols.Count > 0 && !formCols.Contains("Pattern")) conn.Execute("ALTER TABLE Forms ADD COLUMN Pattern TEXT", transaction: tx);
            if (formCols.Count > 0 && !formCols.Contains("PatternVersion")) conn.Execute("ALTER TABLE Forms ADD COLUMN PatternVersion TEXT", transaction: tx);
            if (formCols.Count > 0 && !formCols.Contains("Style")) conn.Execute("ALTER TABLE Forms ADD COLUMN Style TEXT", transaction: tx);
            if (formCols.Count > 0 && !formCols.Contains("TitleDataSource")) conn.Execute("ALTER TABLE Forms ADD COLUMN TitleDataSource TEXT", transaction: tx);
            var dsCols = conn.Query<string>("SELECT name FROM pragma_table_info('FormDataSources')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (dsCols.Count > 0 && !dsCols.Contains("OrderIndex")) conn.Execute("ALTER TABLE FormDataSources ADD COLUMN OrderIndex INTEGER NOT NULL DEFAULT 0", transaction: tx);
            if (dsCols.Count > 0 && !dsCols.Contains("JoinSource")) conn.Execute("ALTER TABLE FormDataSources ADD COLUMN JoinSource TEXT", transaction: tx);
        }
        if (current < 9)
        {
            // v9: Lint-flag columns on Methods / TableMethods. Populated during
            // extract by scanning <Source> text — no full body storage.
            var methodCols = conn.Query<string>("SELECT name FROM pragma_table_info('Methods')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (methodCols.Count > 0 && !methodCols.Contains("HasDocComment"))    conn.Execute("ALTER TABLE Methods ADD COLUMN HasDocComment    INTEGER NOT NULL DEFAULT 0", transaction: tx);
            if (methodCols.Count > 0 && !methodCols.Contains("HasTodayCall"))     conn.Execute("ALTER TABLE Methods ADD COLUMN HasTodayCall     INTEGER NOT NULL DEFAULT 0", transaction: tx);
            if (methodCols.Count > 0 && !methodCols.Contains("HasDoInsertOrUpdate")) conn.Execute("ALTER TABLE Methods ADD COLUMN HasDoInsertOrUpdate INTEGER NOT NULL DEFAULT 0", transaction: tx);

            var tmCols = conn.Query<string>("SELECT name FROM pragma_table_info('TableMethods')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tmCols.Count > 0 && !tmCols.Contains("HasDocComment"))    conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasDocComment    INTEGER NOT NULL DEFAULT 0", transaction: tx);
            if (tmCols.Count > 0 && !tmCols.Contains("HasTodayCall"))     conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasTodayCall     INTEGER NOT NULL DEFAULT 0", transaction: tx);
            if (tmCols.Count > 0 && !tmCols.Contains("HasDoInsertOrUpdate")) conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasDoInsertOrUpdate INTEGER NOT NULL DEFAULT 0", transaction: tx);
        }

        // Run the full schema SQL after all ADD COLUMN migrations so that
        // CREATE INDEX statements referencing new columns succeed.
        conn.Execute(SchemaSql.Value, transaction: tx);

        if (current < 10)
        {
            // AxMap indexing tables — Maps are field-layout templates used
            // for cross-module address/party data patterns in D365FO.
            var hasMaps = conn.Query<string>("SELECT name FROM pragma_table_info('Maps')", transaction: tx).Any();
            if (!hasMaps)
            {
                conn.Execute(@"
                    CREATE TABLE IF NOT EXISTS Maps (
                        MapId      INTEGER PRIMARY KEY AUTOINCREMENT,
                        Name       TEXT NOT NULL,
                        ModelId    INTEGER NOT NULL,
                        Label      TEXT,
                        SourcePath TEXT,
                        FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
                    );
                    CREATE INDEX IF NOT EXISTS IX_Maps_Name ON Maps(Name);
                    CREATE TABLE IF NOT EXISTS MapFields (
                        MapFieldId INTEGER PRIMARY KEY AUTOINCREMENT,
                        MapId      INTEGER NOT NULL,
                        Name       TEXT NOT NULL,
                        Type       TEXT,
                        EdtName    TEXT,
                        Label      TEXT,
                        FOREIGN KEY (MapId) REFERENCES Maps(MapId) ON DELETE CASCADE
                    );
                    CREATE TABLE IF NOT EXISTS MapTables (
                        MapTableId INTEGER PRIMARY KEY AUTOINCREMENT,
                        MapId      INTEGER NOT NULL,
                        TableName  TEXT NOT NULL,
                        FOREIGN KEY (MapId) REFERENCES Maps(MapId) ON DELETE CASCADE
                    );", transaction: tx);
            }
        }

        if (current < 11)
        {
            // v11: Table property gap-fill — new columns on pre-existing Tables/Edts.
            var tableCols = conn.Query<string>("SELECT name FROM pragma_table_info('Tables')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tableCols.Count > 0)
            {
                if (!tableCols.Contains("SaveDataPerCompany"))      conn.Execute("ALTER TABLE Tables ADD COLUMN SaveDataPerCompany TEXT", transaction: tx);
                if (!tableCols.Contains("CacheLookup"))             conn.Execute("ALTER TABLE Tables ADD COLUMN CacheLookup TEXT", transaction: tx);
                if (!tableCols.Contains("OccEnabled"))              conn.Execute("ALTER TABLE Tables ADD COLUMN OccEnabled INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!tableCols.Contains("ValidTimeStateFieldType")) conn.Execute("ALTER TABLE Tables ADD COLUMN ValidTimeStateFieldType TEXT", transaction: tx);
                if (!tableCols.Contains("TableExtends"))            conn.Execute("ALTER TABLE Tables ADD COLUMN TableExtends TEXT", transaction: tx);
                if (!tableCols.Contains("AOSAuthorization"))        conn.Execute("ALTER TABLE Tables ADD COLUMN AOSAuthorization TEXT", transaction: tx);
                if (!tableCols.Contains("FormRef"))                 conn.Execute("ALTER TABLE Tables ADD COLUMN FormRef TEXT", transaction: tx);
                if (!tableCols.Contains("ListPageRef"))             conn.Execute("ALTER TABLE Tables ADD COLUMN ListPageRef TEXT", transaction: tx);
                if (!tableCols.Contains("SystemTable"))             conn.Execute("ALTER TABLE Tables ADD COLUMN SystemTable INTEGER NOT NULL DEFAULT 0", transaction: tx);
            }

            var edtCols = conn.Query<string>("SELECT name FROM pragma_table_info('Edts')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (edtCols.Count > 0)
            {
                if (!edtCols.Contains("ReferenceTable")) conn.Execute("ALTER TABLE Edts ADD COLUMN ReferenceTable TEXT", transaction: tx);
                if (!edtCols.Contains("FormHelp"))       conn.Execute("ALTER TABLE Edts ADD COLUMN FormHelp TEXT", transaction: tx);
                if (!edtCols.Contains("AnalysisUsage"))  conn.Execute("ALTER TABLE Edts ADD COLUMN AnalysisUsage TEXT", transaction: tx);
                if (!edtCols.Contains("EnumType"))       conn.Execute("ALTER TABLE Edts ADD COLUMN EnumType TEXT", transaction: tx);
            }
        }

        if (current < 12)
        {
            // v12: Additional lint-flag columns on Methods, TableMethods, and Classes.
            var methodCols12 = conn.Query<string>("SELECT name FROM pragma_table_info('Methods')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (methodCols12.Count > 0)
            {
                if (!methodCols12.Contains("HasInsertInLoop"))           conn.Execute("ALTER TABLE Methods ADD COLUMN HasInsertInLoop           INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!methodCols12.Contains("HasNestedSelect"))           conn.Execute("ALTER TABLE Methods ADD COLUMN HasNestedSelect           INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!methodCols12.Contains("HasForceLiterals"))          conn.Execute("ALTER TABLE Methods ADD COLUMN HasForceLiterals          INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!methodCols12.Contains("HasForUpdateWithoutUpdate")) conn.Execute("ALTER TABLE Methods ADD COLUMN HasForUpdateWithoutUpdate INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!methodCols12.Contains("HasTryCatchInTts"))          conn.Execute("ALTER TABLE Methods ADD COLUMN HasTryCatchInTts          INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!methodCols12.Contains("HasEmptyLoop"))              conn.Execute("ALTER TABLE Methods ADD COLUMN HasEmptyLoop              INTEGER NOT NULL DEFAULT 0", transaction: tx);
            }

            var tmCols12 = conn.Query<string>("SELECT name FROM pragma_table_info('TableMethods')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (tmCols12.Count > 0)
            {
                if (!tmCols12.Contains("IsEmptyOverride"))  conn.Execute("ALTER TABLE TableMethods ADD COLUMN IsEmptyOverride  INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!tmCols12.Contains("HasInsertInLoop"))  conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasInsertInLoop  INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!tmCols12.Contains("HasNestedSelect"))  conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasNestedSelect  INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!tmCols12.Contains("HasForceLiterals")) conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasForceLiterals INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!tmCols12.Contains("HasTryCatchInTts")) conn.Execute("ALTER TABLE TableMethods ADD COLUMN HasTryCatchInTts INTEGER NOT NULL DEFAULT 0", transaction: tx);
            }

            var classCols12 = conn.Query<string>("SELECT name FROM pragma_table_info('Classes')", transaction: tx)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (classCols12.Count > 0)
            {
                if (!classCols12.Contains("IsRunBaseBatch"))          conn.Execute("ALTER TABLE Classes ADD COLUMN IsRunBaseBatch          INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!classCols12.Contains("HasCanGoBatch"))           conn.Execute("ALTER TABLE Classes ADD COLUMN HasCanGoBatch           INTEGER NOT NULL DEFAULT 0", transaction: tx);
                if (!classCols12.Contains("HasPublicInstanceFields")) conn.Execute("ALTER TABLE Classes ADD COLUMN HasPublicInstanceFields INTEGER NOT NULL DEFAULT 0", transaction: tx);
            }
        }

        conn.Execute($"PRAGMA user_version = {CurrentSchemaVersion}", transaction: tx);
        conn.Execute(
            "INSERT OR IGNORE INTO SchemaVersion(Version, AppliedUtc) VALUES(@v, @t)",
            new { v = CurrentSchemaVersion, t = DateTime.UtcNow.ToString("O") }, transaction: tx);

        tx.Commit();

        // FTS5 rebuild is intentionally outside the transaction: rebuilding the
        // virtual table inside a transaction can cause subtle lock contention on
        // some SQLite builds. A crash here leaves the FTS5 shadow tables in an
        // out-of-sync state that the next EnsureSchema() run will fix via rebuild.
        //
        // v6 added the FTS5 table + INSERT/DELETE/UPDATE triggers that keep it
        // in lock-step with Labels incrementally. Databases at v6+ never need a
        // full rebuild on schema upgrade — the triggers have kept the index current.
        // Only pre-v6 databases require a one-time backfill because they have
        // existing Labels rows that predate the triggers.
        if (current < 6)
        {
            try
            {
                conn.Execute("INSERT INTO LabelFts(LabelFts) VALUES('rebuild')");
            }
            catch (Microsoft.Data.Sqlite.SqliteException)
            {
                // Host SQLite build lacks FTS5 — degrade gracefully; SearchLabels()
                // stays on the LIKE fallback path.
            }
        }
        return true;
    }

    /// <summary>
    /// Search classes by name (LIKE wildcard).
    /// </summary>
    /// <remarks>
    /// Every object-level <c>Search*</c> method orders by <c>m.IsCustom DESC</c>
    /// before the name. On a production AOT a prefix query like "Cust" matches
    /// hundreds of Microsoft objects; ordering by name alone buries the handful
    /// the customer/ISV actually authored — and because the ordering happens
    /// before <c>LIMIT</c>, those are the rows that get truncated away entirely.
    /// <c>Models.IsCustom</c> is <c>NOT NULL DEFAULT 0</c>, so <c>DESC</c> means
    /// exactly "custom first, then Microsoft, each alphabetical". Label and
    /// method-source search are excluded: they rank by FTS5 relevance and have no
    /// model join to order on.
    /// </remarks>
    public IReadOnlyList<ClassInfo> SearchClasses(string query, string? model = null, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        var sql = @"
            SELECT c.ClassId, c.Name, m.Name AS Model, c.ExtendsName AS Extends,
                   c.IsAbstract, c.IsFinal, c.SourcePath
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.Name LIKE @like ESCAPE '!'
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY m.IsCustom DESC, c.Name
            LIMIT @limit";
        return conn.Query<ClassInfo>(sql, new { like, model, limit }).ToList();
    }

    /// <summary>Maximum <c>extends</c> hops walked when collecting inherited methods.</summary>
    /// <remarks>
    /// Real D365FO hierarchies are a handful of levels deep; the bound exists so a
    /// cyclic <c>ExtendsName</c> chain in a malformed index cannot spin the recursive
    /// CTE forever.
    /// </remarks>
    private const int MaxExtendsDepth = 32;

    /// <remarks>
    /// The chain depth is deliberately not projected: it comes out of the recursive
    /// CTE with no declared type, and on an empty result set Microsoft.Data.Sqlite
    /// reports such a column as <c>byte[]</c>, which Dapper then cannot bind (the same
    /// typeless-column problem the FTS5 queries work around). Depth ordering is applied
    /// in SQL instead, and the caller relies on that order.
    /// </remarks>
    private sealed record InheritedMethodRow(
        string Name, string? Signature, string? ReturnType, bool IsStatic,
        string DeclaringClass);

    public ClassDetails? GetClassDetails(string name)
    {
        using var conn = OpenReadOnly();
        // Execute all queries in one round-trip via QueryMultiple to avoid
        // sequential open/close cycles on the connection pool.
        //
        // The third query walks the extends chain: a class lookup that reads only
        // locally-declared members reports a subclass as having almost no methods,
        // when in practice most of its surface is inherited. Locally-declared names
        // are excluded so an override is attributed to the class that overrides it.
        using var multi = conn.QueryMultiple(@"
            SELECT c.ClassId, c.Name, m.Name AS Model, c.ExtendsName AS Extends,
                   c.IsAbstract, c.IsFinal, c.SourcePath
            FROM Classes c JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.Name = @name LIMIT 1;

            SELECT mt.Name, mt.Signature, mt.ReturnType, mt.IsStatic
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            WHERE c.Name = @name ORDER BY mt.Name;

            WITH RECURSIVE Ancestors(Name, Depth) AS (
                SELECT c.ExtendsName, 1
                FROM Classes c
                WHERE c.Name = @name AND COALESCE(c.ExtendsName, '') <> ''
                UNION ALL
                SELECT c.ExtendsName, a.Depth + 1
                FROM Ancestors a
                JOIN Classes c ON c.Name = a.Name
                WHERE COALESCE(c.ExtendsName, '') <> '' AND a.Depth < @maxDepth
            )
            SELECT mt.Name, mt.Signature, mt.ReturnType, mt.IsStatic,
                   c.Name AS DeclaringClass
            FROM Ancestors a
            JOIN Classes c ON c.Name = a.Name
            JOIN Methods mt ON mt.ClassId = c.ClassId
            WHERE mt.Name NOT IN (
                SELECT own.Name FROM Methods own
                JOIN Classes oc ON oc.ClassId = own.ClassId
                WHERE oc.Name = @name)
            ORDER BY a.Depth, mt.Name", new { name, maxDepth = MaxExtendsDepth });

        var cls = multi.ReadFirstOrDefault<ClassInfo>();
        if (cls is null) return null;
        var methods = multi.Read<MethodInfo>().ToList();

        // Rows arrive ordered by chain depth, so a method redeclared at several levels
        // resolves to the nearest declaration simply by keeping the first one seen.
        var inherited = multi.Read<InheritedMethodRow>()
            .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new MethodInfo(r.Name, r.Signature, r.ReturnType, r.IsStatic)
            {
                DeclaringClass = r.DeclaringClass,
            })
            .ToList();

        return new ClassDetails(cls, methods, inherited);
    }

    public TableDetails? GetTableDetails(string name)
    {
        using var conn = OpenReadOnly();
        var table = conn.QueryFirstOrDefault<TableInfo>(@"
            SELECT t.TableId, t.Name, m.Name AS Model, t.Label, t.SourcePath,
                   t.SaveDataPerCompany, t.CacheLookup, t.OccEnabled, t.ValidTimeStateFieldType,
                   t.TableExtends, t.AOSAuthorization, t.FormRef, t.ListPageRef, t.SystemTable
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

    /// <summary>
    /// One EDT by name, case-insensitively — X++ identifiers are, and callers
    /// pass names an LLM produced. Where the name exists in several models the
    /// custom one wins, so the answer does not depend on insertion order.
    /// Served by <c>IX_Edts_Name_NoCase</c>, the same index
    /// <see cref="FindEdtsExact"/> uses.
    /// </summary>
    public EdtInfo? GetEdt(string name)
    {
        using var conn = OpenReadOnly();
        return conn.QueryFirstOrDefault<EdtInfo>(@"
            SELECT e.Name, m.Name AS Model, e.ExtendsName AS Extends,
                   e.BaseType, e.Label, e.StringSize,
                   e.ReferenceTable, e.FormHelp, e.AnalysisUsage, e.EnumType
            FROM Edts e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name = @name COLLATE NOCASE
            ORDER BY m.IsCustom DESC, m.Name COLLATE NOCASE
            LIMIT 1", new { name });
    }

    /// <summary>
    /// <see cref="GetEdt"/>, with an inherited <c>StringSize</c> resolved. The AOT stores an
    /// EDT exactly as its own XML declares it and fills in nothing it inherits, so a derived
    /// string EDT that declares no size used to read back as unset — while the real answer is
    /// up the extends chain (<c>ItemId</c> is really 20, <c>ItemFreeTxt</c> 1000; upstream
    /// measured 310 of 564 string fields (55%) wrong across ten core tables). A derived EDT
    /// that DECLARES its own size is authoritative and is left untouched; only the unset case
    /// is filled in, and <c>InheritedFrom</c> names the ancestor that supplied it.
    /// </summary>
    public (EdtInfo Edt, string? StringSizeInheritedFrom)? GetEdtResolved(string name)
    {
        var edt = GetEdt(name);
        if (edt is null) return null;
        if (edt.StringSize is not null) return (edt, null);

        var current = edt.Extends;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { edt.Name };
        for (var depth = 0; depth < InheritanceWalkLimit && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (!visited.Add(current!)) break;
            var ancestor = GetEdt(current!);
            if (ancestor is null) break;
            if (ancestor.StringSize is not null)
                return (edt with { StringSize = ancestor.StringSize }, ancestor.Name);
            current = ancestor.Extends;
        }

        return (edt, null);
    }

    /// <summary>
    /// Every EDT named exactly <paramref name="name"/> (case-insensitive),
    /// across all models. Served by <c>IX_Edts_Name_NoCase</c> — the BINARY
    /// <c>IX_Edts_Name</c> cannot answer a <c>COLLATE NOCASE</c> comparison.
    /// </summary>
    internal IReadOnlyList<EdtInfo> FindEdtsExact(string name, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        return conn.Query<EdtInfo>(@"
            SELECT e.Name, m.Name AS Model, e.ExtendsName AS Extends,
                   e.BaseType, e.Label, e.StringSize,
                   e.ReferenceTable, e.FormHelp, e.AnalysisUsage, e.EnumType
            FROM Edts e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name = @name COLLATE NOCASE
            ORDER BY m.IsCustom DESC, m.Name COLLATE NOCASE, e.Name COLLATE NOCASE
            LIMIT @limit",
            new { name, limit }).ToList();
    }

    /// <summary>
    /// Index rows store the <em>base</em> object an extension targets (e.g.
    /// <c>CustTable</c>), but callers and LLMs often pass the full extension
    /// name (<c>CustTable.Extension</c>, <c>CustTable.MyModelSuffix</c>). AOT
    /// object names never contain a dot, so a dot unambiguously marks an
    /// extension suffix — strip it to the base before matching.
    /// </summary>
    private static string BaseExtensionTarget(string target)
    {
        if (string.IsNullOrEmpty(target)) return target;
        var dot = target.IndexOf('.');
        return dot > 0 ? target.Substring(0, dot) : target;
    }

    public IReadOnlyList<CocExtensionInfo> FindCocExtensions(string targetClass, string? targetMethod = null)
    {
        targetClass = BaseExtensionTarget(targetClass);
        using var conn = OpenReadOnly();
        var sql = @"
            SELECT c.TargetClass, c.TargetMethod, c.ExtensionClass, m.Name AS Model
            FROM CocExtensions c JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.TargetClass = @cls
              AND (@method IS NULL OR c.TargetMethod = @method)
            ORDER BY c.TargetMethod, c.ExtensionClass";
        return conn.Query<CocExtensionInfo>(sql, new { cls = targetClass, method = targetMethod }).ToList();
    }

    /// <summary>
    /// Disk paths of every language variant of <paramref name="labelFile"/> (v17). Empty on a
    /// database extracted before v17 or when the table is absent — the disk check then reports
    /// "could not verify" rather than a verdict.
    /// </summary>
    public IReadOnlyList<string> GetLabelFilePaths(string labelFile)
    {
        try
        {
            using var conn = OpenReadOnly();
            return conn.Query<string>(
                "SELECT SourcePath FROM LabelFiles WHERE LabelFile=@f COLLATE NOCASE",
                new { f = labelFile }).ToList();
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return Array.Empty<string>();
        }
    }

    public IReadOnlyList<LabelMatch> SearchLabels(string query, IReadOnlyCollection<string>? languages = null, int limit = 100)
    {
        limit = ClampLimit(limit, 100);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        var langsLower = languages?.Select(l => l.ToLowerInvariant()).ToList();
        string sql;
        if (langsLower is null || langsLower.Count == 0)
        {
            sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE (Value LIKE @like ESCAPE '!' OR Key LIKE @like ESCAPE '!')
            ORDER BY LabelFile, Key
            LIMIT @limit";
            return conn.Query<LabelMatch>(sql, new { like, limit }).ToList();
        }
        else
        {
            sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE (Value LIKE @like ESCAPE '!' OR Key LIKE @like ESCAPE '!')
              AND LOWER(Language) IN @langs
            ORDER BY LabelFile, Key
            LIMIT @limit";
            return conn.Query<LabelMatch>(sql, new { like, langs = langsLower, limit }).ToList();
        }
    }

    /// <summary>
    /// FTS5-backed label search. Falls back to <see cref="SearchLabels"/>
    /// when the host SQLite build lacks FTS5. Supports standard FTS5 query
    /// syntax — phrases in quotes, NEAR, column filters like
    /// <c>Value:customer Key:invoice</c>.
    /// </summary>
    public IReadOnlyList<LabelMatch> SearchLabelsFts(string query, IReadOnlyCollection<string>? languages = null, int limit = 100)
    {
        limit = ClampLimit(limit, 100);
        using var conn = OpenReadOnly();
        var langsLower = languages?.Select(l => l.ToLowerInvariant()).ToList();
        try
        {
            string sql;
            if (langsLower is null || langsLower.Count == 0)
            {
                sql = @"
                SELECT LabelFile AS File, Language, Key, Value
                FROM LabelFts
                WHERE LabelFts MATCH @q
                ORDER BY rank
                LIMIT @limit";
                return ReadLabelMatches(conn.Query(sql, new { q = query, limit }));
            }
            else
            {
                sql = @"
                SELECT LabelFile AS File, Language, Key, Value
                FROM LabelFts
                WHERE LabelFts MATCH @q
                  AND LOWER(Language) IN @langs
                ORDER BY rank
                LIMIT @limit";
                return ReadLabelMatches(conn.Query(sql, new { q = query, langs = langsLower, limit }));
            }
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Either FTS5 is absent or the query string isn't valid FTS syntax
            // — degrade to LIKE so callers always get *some* results.
            var results = SearchLabels(query, languages, limit);
            // Attach a sentinel so callers can detect the fallback occurred.
            if (results is List<LabelMatch> list)
            {
                list.Insert(0, new LabelMatch(
                    "__warning__", "", "FTS_FALLBACK",
                    "FTS5 unavailable or invalid query syntax; results may be incomplete (fell back to LIKE)."));
            }
            return results;
        }
    }

    /// <summary>
    /// Maps rows from the <c>LabelFts</c> virtual table into <see cref="LabelMatch"/>.
    /// FTS5 columns carry no declared SQLite type, so on a zero-row result set
    /// Dapper's strongly-typed <c>Query&lt;LabelMatch&gt;</c> infers every column as
    /// <c>byte[]</c> and fails to find a matching constructor (see issue #115).
    /// Querying as <c>dynamic</c> and converting by hand sidesteps that
    /// materialization entirely, for both the empty and non-empty cases.
    /// </summary>
    private static List<LabelMatch> ReadLabelMatches(IEnumerable<dynamic> rows) =>
        rows.Select(row => new LabelMatch(
                (string)Convert.ChangeType(row.File, typeof(string)),
                (string)Convert.ChangeType(row.Language, typeof(string)),
                (string)Convert.ChangeType(row.Key, typeof(string)),
                row.Value is null ? null : (string)Convert.ChangeType(row.Value, typeof(string))))
            .ToList();

    /// <summary>
    /// Number of method bodies currently in the opt-in <c>MethodSourceFts</c>
    /// index, optionally restricted to one model. Returns 0 when source indexing
    /// was never run or the host SQLite build lacks FTS5. Callers use this to
    /// decide between the fast FTS path and the on-demand disk scan.
    /// </summary>
    public long CountMethodSource(string? model = null)
    {
        using var conn = OpenReadOnly();
        try
        {
            return string.IsNullOrEmpty(model)
                ? conn.ExecuteScalar<long>("SELECT count(*) FROM MethodSourceFts")
                : conn.ExecuteScalar<long>("SELECT count(*) FROM MethodSourceFts WHERE Model=@m", new { m = model });
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return 0;
        }
    }

    /// <summary>
    /// FTS5-backed search over X++ method bodies (the opt-in v15
    /// <c>MethodSourceFts</c> index, populated via <c>index extract --index-source</c>).
    /// Supports standard FTS5 query syntax. Returns an empty list when the index
    /// is empty or the host SQLite build lacks FTS5 — callers that need a result
    /// regardless should fall back to scanning source on disk. The
    /// <see cref="MethodSourceMatch.Snippet"/> highlights the matched region.
    /// </summary>
    public IReadOnlyList<MethodSourceMatch> SearchMethodSource(string query, int limit = 100, string? model = null, string? kind = null)
    {
        limit = ClampLimit(limit, 100);
        using var conn = OpenReadOnly();
        try
        {
            // FTS5 columns are typeless; Dapper infers byte[] (especially for an
            // empty result set) and fails to bind the string record. Read with a
            // raw reader and GetString instead, which coerces regardless of type.
            // Model/Kind are UNINDEXED but still stored, so filtering them next to
            // the MATCH is cheap and keeps the LIMIT semantics correct.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT Kind, ObjectName, MethodName, Model, SourcePath,
                       snippet(MethodSourceFts, 0, '«', '»', ' … ', 12) AS Snippet
                FROM MethodSourceFts
                WHERE MethodSourceFts MATCH $q
                  AND ($model IS NULL OR Model = $model)
                  AND ($kind  IS NULL OR Kind  = $kind)
                ORDER BY rank
                LIMIT $limit";
            cmd.Parameters.AddWithValue("$q", query);
            cmd.Parameters.AddWithValue("$limit", limit);
            cmd.Parameters.AddWithValue("$model", (object?)model ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var results = new List<MethodSourceMatch>();
            while (reader.Read())
            {
                results.Add(new MethodSourceMatch(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
            return results;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return Array.Empty<MethodSourceMatch>();
        }
    }

    public MenuItemInfo? GetMenuItem(string name)
    {
        using var conn = OpenReadOnly();
        return conn.QueryFirstOrDefault<MenuItemInfo>(@"
            SELECT mi.Name, mi.Kind, mi.Object, mi.ObjectType, mi.Label, m.Name AS Model
            FROM MenuItems mi JOIN Models m ON m.ModelId = mi.ModelId
            WHERE mi.Name = @name LIMIT 1", new { name });
    }

    public IReadOnlyList<RelationInfo> GetTableRelations(string table)
    {
        using var conn = OpenReadOnly();
        return conn.Query<RelationInfo>(@"
            SELECT FromTable, ToTable, Cardinality, RelationName
            FROM Relations WHERE FromTable = @n OR ToTable = @n",
            new { n = table }).ToList();
    }

    public SecurityCoverage GetSecurityCoverage(string objectName, string objectType)
    {
        using var conn = OpenReadOnly();
        var routes = conn.Query<SecurityRoute>(@"
            SELECT Role, Duty, Privilege, EntryPoint
            FROM SecurityMap
            WHERE ObjectName = @n AND ObjectType = @t
            ORDER BY Role, Duty, Privilege",
            new { n = objectName, t = objectType }).ToList();
        return new SecurityCoverage(objectName, objectType, routes,
            ReadRowLevelSecurity(conn, objectName, objectType));
    }

    /// <summary>
    /// Record-level (XDS) coverage for a table. Distinguishes "no policy constrains
    /// this table" from "this index knows nothing about policies" — reporting the
    /// latter as a silent empty result would read as an assurance the data cannot
    /// support.
    /// </summary>
    private static RowLevelSecurityCoverage ReadRowLevelSecurity(
        System.Data.IDbConnection conn, string objectName, string objectType)
    {
        var empty = Array.Empty<SecurityPolicyInfo>();

        // XDS constrains tables; for a menu item / form / class there is no
        // record-level answer to give.
        if (!string.Equals(objectType, "Table", StringComparison.OrdinalIgnoreCase))
            return new RowLevelSecurityCoverage("NotApplicable", empty);

        var policies = conn.Query<SecurityPolicyInfo>(@"
            SELECT sp.Id, sp.Name, sp.ConstrainedTable, sp.PolicyQuery, sp.OperationType,
                   sp.ContextType, sp.IsEnabled, sp.IsMandatory, m.Name AS Model, sp.SourcePath
            FROM SecurityPolicies sp JOIN Models m ON m.ModelId = sp.ModelId
            WHERE sp.ConstrainedTable = @n
            ORDER BY sp.Name", new { n = objectName }).ToList();

        if (policies.Count > 0)
            return new RowLevelSecurityCoverage("Constrained", policies);

        // Zero matches only means "unconstrained" when policies were indexed at all;
        // an index built before AxSecurityPolicy extraction has none of either.
        var indexed = conn.ExecuteScalar<long>("SELECT COUNT(*) FROM SecurityPolicies");
        return new RowLevelSecurityCoverage(indexed > 0 ? "NotConstrained" : "Unknown", empty);
    }

    public IReadOnlyList<ObjectExtensionInfo> FindExtensions(string targetName, string? kind = null)
    {
        targetName = BaseExtensionTarget(targetName);
        using var conn = OpenReadOnly();
        return conn.Query<ObjectExtensionInfo>(@"
            SELECT e.Kind, e.TargetName, e.ExtensionName, m.Name AS Model, e.SourcePath
            FROM ObjectExtensions e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.TargetName = @t AND (@k IS NULL OR e.Kind = @k)
            ORDER BY e.Kind, e.ExtensionName",
            new { t = targetName, k = kind }).ToList();
    }

    public IReadOnlyList<EventSubscriberInfo> FindEventSubscribers(string sourceObject, string? sourceKind = null)
    {
        sourceObject = BaseExtensionTarget(sourceObject);
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        // Driving datasource = OrderIndex 0 (or any when none populated yet).
        var sql = @"
            SELECT f.Name, f.Pattern, f.PatternVersion, f.Style, f.TitleDataSource,
                   m.Name AS Model, f.SourcePath,
                   (SELECT TableName FROM FormDataSources d
                     WHERE d.FormId = f.FormId
                     ORDER BY d.OrderIndex, d.Id LIMIT 1) AS PrimaryTable,
                   (SELECT COUNT(*) FROM FormDataSources d WHERE d.FormId = f.FormId) AS DataSourceCount
            FROM Forms f JOIN Models m ON m.ModelId = f.ModelId
            WHERE (@pat IS NULL OR f.Pattern LIKE @patLike ESCAPE '!')
              AND (@tbl IS NULL OR EXISTS (
                    SELECT 1 FROM FormDataSources d
                    WHERE d.FormId = f.FormId AND d.TableName = @tbl))
              AND (@mdl IS NULL OR m.Name = @mdl)
            ORDER BY f.Pattern, f.Name
            LIMIT @lim";
        // Prefix match on a caller-supplied pattern name — escape the wildcards so that
        // e.g. "Simple_List" matches that literal name and not "SimpleXList".
        var patLike = pattern is null ? null : EscapeLike(pattern) + "%";
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
        using var conn = OpenReadOnly();
        return conn.Query<FormPatternSummary>(@"
            SELECT CAST(COALESCE(NULLIF(Pattern, ''), '(none)') AS TEXT) AS Pattern,
                   CAST(COUNT(*) AS INTEGER) AS Count
            FROM Forms
            GROUP BY COALESCE(NULLIF(Pattern, ''), '(none)')
            ORDER BY Count DESC, Pattern").ToList();
    }

    public FormDetails? GetForm(string name)
    {
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
        return conn.Query<TableMethodInfo>(@"
            SELECT tm.Name, tm.Signature, tm.ReturnType, tm.IsStatic
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            WHERE t.Name = @n ORDER BY tm.Name", new { n = table }).ToList();
    }

    public IReadOnlyList<TableIndexInfo> GetTableIndexes(string table)
    {
        using var conn = OpenReadOnly();
        return conn.Query<TableIndexInfo>(@"
            SELECT ti.Name, ti.AllowDuplicates, ti.AlternateKey, ti.FieldsCsv
            FROM TableIndexes ti
            JOIN Tables t ON t.TableId = ti.TableId
            WHERE t.Name = @n ORDER BY ti.Name", new { n = table }).ToList();
    }

    public IReadOnlyList<TableDeleteActionInfo> GetTableDeleteActions(string table)
    {
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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

    /// <summary>
    /// Returns methods (class or table) that call <c>today()</c>
    /// — should use <c>DateTimeUtil::getToday(...)</c> instead (BP: BPUpgradeCodeToday).
    /// </summary>
    public IReadOnlyList<LintHit> FindTodayCallMethods(bool onlyCustomModels = true)
    {
        using var conn = OpenReadOnly();
        // Union class methods and table methods.
        var sql = @"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'today()' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasTodayCall = 1
              AND (@custom = 0 OR m.IsCustom = 1)
            UNION ALL
            SELECT (t.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'today()' AS Detail
            FROM TableMethods mt
            JOIN Tables t ON t.TableId = mt.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE mt.HasTodayCall = 1
              AND (@custom = 0 OR m.IsCustom = 1)
            ORDER BY TargetName";
        return conn.Query(sql, new { custom = onlyCustomModels ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Returns methods that call <c>doInsert()</c>, <c>doUpdate()</c>, or <c>doDelete()</c>
    /// (bypasses table overrides — reserved for migration scripts).
    /// </summary>
    public IReadOnlyList<LintHit> FindDoInsertOrUpdateMethods(bool onlyCustomModels = true)
    {
        using var conn = OpenReadOnly();
        var sql = @"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'doInsert/doUpdate/doDelete' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasDoInsertOrUpdate = 1
              AND (@custom = 0 OR m.IsCustom = 1)
            UNION ALL
            SELECT (t.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'doInsert/doUpdate/doDelete' AS Detail
            FROM TableMethods mt
            JOIN Tables t ON t.TableId = mt.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE mt.HasDoInsertOrUpdate = 1
              AND (@custom = 0 OR m.IsCustom = 1)
            ORDER BY TargetName";
        return conn.Query(sql, new { custom = onlyCustomModels ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Returns public/protected methods that lack a <c>/// &lt;summary&gt;</c> doc comment
    /// (BP: BPXmlDocNoDocumentationComments).
    /// </summary>
    public IReadOnlyList<LintHit> FindMissingDocCommentMethods(bool onlyCustomModels = true)
    {
        using var conn = OpenReadOnly();
        var sql = @"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'missing doc-comment' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasDocComment = 0
              AND (@custom = 0 OR m.IsCustom = 1)
            UNION ALL
            SELECT (t.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'missing doc-comment' AS Detail
            FROM TableMethods mt
            JOIN Tables t ON t.TableId = mt.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE mt.HasDocComment = 0
              AND (@custom = 0 OR m.IsCustom = 1)
            ORDER BY TargetName";
        return conn.Query(sql, new { custom = onlyCustomModels ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    // ---- v12 lint methods ----

    /// <summary>
    /// Methods that use "while select" two or more times (nested queries).
    /// </summary>
    public IReadOnlyList<LintHit> FindNestedSelectMethods(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        var rows = new List<LintHit>();
        rows.AddRange(conn.Query(@"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'Class method' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasNestedSelect = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY c.Name, mt.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        rows.AddRange(conn.Query(@"
            SELECT (t.Name || '::' || tm.Name) AS TargetName, m.Name AS Model, 'Table method' AS Detail
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE tm.HasNestedSelect = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name, tm.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        return rows;
    }

    /// <summary>
    /// Methods that call .insert() inside a loop.
    /// </summary>
    public IReadOnlyList<LintHit> FindInsertInLoopMethods(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        var rows = new List<LintHit>();
        rows.AddRange(conn.Query(@"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'Class method' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasInsertInLoop = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY c.Name, mt.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        rows.AddRange(conn.Query(@"
            SELECT (t.Name || '::' || tm.Name) AS TargetName, m.Name AS Model, 'Table method' AS Detail
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE tm.HasInsertInLoop = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name, tm.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        return rows;
    }

    /// <summary>
    /// Methods with a try block inside a ttsbegin/ttscommit scope.
    /// </summary>
    public IReadOnlyList<LintHit> FindTtsTryCatchMethods(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        var rows = new List<LintHit>();
        rows.AddRange(conn.Query(@"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'Class method' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasTryCatchInTts = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY c.Name, mt.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        rows.AddRange(conn.Query(@"
            SELECT (t.Name || '::' || tm.Name) AS TargetName, m.Name AS Model, 'Table method' AS Detail
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE tm.HasTryCatchInTts = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name, tm.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        return rows;
    }

    /// <summary>
    /// Table methods that override a base method but have an empty body.
    /// </summary>
    public IReadOnlyList<LintHit> FindEmptyTableMethodOverrides(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        return conn.Query(@"
            SELECT (t.Name || '::' || tm.Name) AS TargetName, m.Name AS Model, 'Empty method override' AS Detail
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE tm.IsEmptyOverride = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name, tm.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Classes extending RunBaseBatch that do not have a canGoBatch() method returning true.
    /// </summary>
    public IReadOnlyList<LintHit> FindRunBaseBatchWithoutCanGoBatch(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        return conn.Query(@"
            SELECT c.Name AS TargetName, m.Name AS Model, 'Extends RunBaseBatch but canGoBatch() not detected' AS Detail
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.IsRunBaseBatch = 1
              AND c.HasCanGoBatch = 0
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY c.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Methods that use the forceLiterals keyword (bypasses query param safety).
    /// </summary>
    public IReadOnlyList<LintHit> FindForceLiteralsMethods(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        var rows = new List<LintHit>();
        rows.AddRange(conn.Query(@"
            SELECT (c.Name || '::' || mt.Name) AS TargetName, m.Name AS Model, 'Class method' AS Detail
            FROM Methods mt
            JOIN Classes c ON c.ClassId = mt.ClassId
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE mt.HasForceLiterals = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY c.Name, mt.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        rows.AddRange(conn.Query(@"
            SELECT (t.Name || '::' || tm.Name) AS TargetName, m.Name AS Model, 'Table method' AS Detail
            FROM TableMethods tm
            JOIN Tables t ON t.TableId = tm.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE tm.HasForceLiterals = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name, tm.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)));
        return rows;
    }

    /// <summary>
    /// Classes that have public instance fields (should be private/protected with accessors).
    /// </summary>
    public IReadOnlyList<LintHit> FindPublicInstanceFieldClasses(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        return conn.Query(@"
            SELECT c.Name AS TargetName, m.Name AS Model, 'Class has public instance fields' AS Detail
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.HasPublicInstanceFields = 1
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY c.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Tables where CacheLookup is set but the value may not be appropriate
    /// (e.g. EntireTable on a frequently-updated table). Flags tables with
    /// CacheLookup = 'EntireTable' as a simple conservative heuristic.
    /// </summary>
    public IReadOnlyList<LintHit> FindCacheLookupMismatches(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        return conn.Query(@"
            SELECT t.Name AS TargetName, m.Name AS Model, ('CacheLookup=' || t.CacheLookup) AS Detail
            FROM Tables t
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE t.CacheLookup IS NOT NULL
              AND t.CacheLookup <> ''
              AND t.CacheLookup <> 'None'
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Relations (FK) that do not have a corresponding TableDeleteAction entry.
    /// </summary>
    public IReadOnlyList<LintHit> FindMissingDeleteActions(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        return conn.Query(@"
            SELECT t.Name AS TargetName, m.Name AS Model, ('Relation to ' || r.ToTable) AS Detail
            FROM Relations r
            JOIN Tables t ON t.Name = r.FromTable
            JOIN Models m ON m.ModelId = t.ModelId
            LEFT JOIN TableDeleteActions da ON da.TableId = t.TableId AND da.RelatedTable = r.ToTable
            WHERE da.Id IS NULL
              AND (@only = 0 OR m.IsCustom = 1)
            ORDER BY t.Name, r.ToTable", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    /// <summary>
    /// Tables that have at least one unique index but no AlternateKey index.
    /// </summary>
    public IReadOnlyList<LintHit> FindTablesWithoutAlternateKey(bool onlyCustom = true)
    {
        using var conn = OpenReadOnly();
        return conn.Query(@"
            SELECT DISTINCT t.Name AS TargetName, m.Name AS Model, 'Has unique index but no AlternateKey' AS Detail
            FROM TableIndexes ix
            JOIN Tables t ON t.TableId = ix.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE ix.AllowDuplicates = 0
              AND (@only = 0 OR m.IsCustom = 1)
              AND NOT EXISTS (
                SELECT 1 FROM TableIndexes ix2
                WHERE ix2.TableId = t.TableId AND ix2.AlternateKey = 1
              )
            ORDER BY t.Name", new { only = onlyCustom ? 1 : 0 })
            .Select(r => new LintHit((string)r.TargetName, (string)r.Model, (string?)r.Detail)).ToList();
    }

    // ---- v4: queries / views / data entities / reports / services / workflow ----

    public IReadOnlyList<QueryInfo> SearchQueries(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<QueryInfo>(@"
            SELECT q.QueryId, q.Name, m.Name AS Model, q.SourcePath
            FROM Queries q JOIN Models m ON m.ModelId = q.ModelId
            WHERE q.Name LIKE @like ESCAPE '!' ORDER BY m.IsCustom DESC, q.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public QueryDetails? GetQuery(string name)
    {
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<ViewInfo>(@"
            SELECT v.ViewId, v.Name, m.Name AS Model, v.Label, v.QueryName, v.SourcePath
            FROM Views v JOIN Models m ON m.ModelId = v.ModelId
            WHERE v.Name LIKE @like ESCAPE '!' ORDER BY m.IsCustom DESC, v.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public ViewDetails? GetView(string name)
    {
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<DataEntityInfo>(@"
            SELECT e.EntityId, e.Name, m.Name AS Model, e.PublicEntityName, e.PublicCollectionName,
                   e.StagingTable, e.QueryName, e.Label, e.SourcePath
            FROM DataEntities e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name LIKE @like ESCAPE '!' OR e.PublicEntityName LIKE @like ESCAPE '!' OR e.PublicCollectionName LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, e.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public DataEntityDetails? GetDataEntity(string name)
    {
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<ReportInfo>(@"
            SELECT r.ReportId, r.Name, r.Kind, m.Name AS Model, r.SourcePath
            FROM Reports r JOIN Models m ON m.ModelId = r.ModelId
            WHERE r.Name LIKE @like ESCAPE '!' ORDER BY m.IsCustom DESC, r.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public ReportDetails? GetReport(string name)
    {
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<ServiceInfo>(@"
            SELECT s.ServiceId, s.Name, s.Class, m.Name AS Model, s.SourcePath
            FROM Services s JOIN Models m ON m.ModelId = s.ModelId
            WHERE s.Name LIKE @like ESCAPE '!' ORDER BY m.IsCustom DESC, s.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public ServiceDetails? GetService(string name)
    {
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<WorkflowTypeInfo>(@"
            SELECT w.Name, w.Category, w.DocumentClass, m.Name AS Model, w.SourcePath
            FROM WorkflowTypes w JOIN Models m ON m.ModelId = w.ModelId
            WHERE w.Name LIKE @like ESCAPE '!' OR w.DocumentClass LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, w.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    // ---- AxMap queries (v10) ----

    /// <summary>Search for AxMap objects by name (LIKE wildcard).</summary>
    public IReadOnlyList<MapInfo> SearchMaps(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<MapInfo>(@"
            SELECT mp.MapId, mp.Name, m.Name AS Model, mp.Label, mp.SourcePath
            FROM Maps mp JOIN Models m ON m.ModelId = mp.ModelId
            WHERE mp.Name LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, mp.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    /// <summary>Returns full details of an AxMap including fields and mapped tables.</summary>
    public MapDetails? GetMap(string name)
    {
        using var conn = OpenReadOnly();
        using var multi = conn.QueryMultiple(@"
            SELECT mp.MapId, mp.Name, m.Name AS Model, mp.Label, mp.SourcePath
            FROM Maps mp JOIN Models m ON m.ModelId = mp.ModelId
            WHERE mp.Name = @name LIMIT 1;
            SELECT f.Name, f.Type, f.EdtName, f.Label
            FROM MapFields f JOIN Maps mp ON mp.MapId = f.MapId
            WHERE mp.Name = @name ORDER BY f.Name;
            SELECT mt.TableName FROM MapTables mt
            JOIN Maps mp ON mp.MapId = mt.MapId
            WHERE mp.Name = @name ORDER BY mt.TableName", new { name });
        var map = multi.ReadFirstOrDefault<MapInfo>();
        if (map is null) return null;
        var fields = multi.Read<MapFieldInfo>().ToList();
        var tables = multi.Read<string>().ToList();
        return new MapDetails(map, fields, tables);
    }

    // ---- v11 query methods ----

    public IReadOnlyList<BusinessEventInfo> SearchBusinessEvents(string query, string? category = null, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<BusinessEventInfo>(@"
            SELECT be.Id, be.Name, be.Category, be.ContractClass, m.Name AS Model, be.SourcePath
            FROM BusinessEvents be JOIN Models m ON m.ModelId = be.ModelId
            WHERE (be.Name LIKE @like ESCAPE '!' OR be.ContractClass LIKE @like ESCAPE '!')
              AND (@category IS NULL OR be.Category LIKE @catLike ESCAPE '!')
            ORDER BY m.IsCustom DESC, be.Name LIMIT @limit",
            new { like, category, catLike = category is not null ? $"%{EscapeLike(category)}%" : null, limit }).ToList();
    }

    public BusinessEventInfo? GetBusinessEvent(string name)
    {
        using var conn = OpenReadOnly();
        return conn.QueryFirstOrDefault<BusinessEventInfo>(@"
            SELECT be.Id, be.Name, be.Category, be.ContractClass, m.Name AS Model, be.SourcePath
            FROM BusinessEvents be JOIN Models m ON m.ModelId = be.ModelId
            WHERE be.Name = @name LIMIT 1", new { name });
    }

    public IReadOnlyList<SecurityPolicyInfo> SearchSecurityPolicies(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<SecurityPolicyInfo>(@"
            SELECT sp.Id, sp.Name, sp.ConstrainedTable, sp.PolicyQuery, sp.OperationType, sp.ContextType,
                   sp.IsEnabled, sp.IsMandatory, m.Name AS Model, sp.SourcePath
            FROM SecurityPolicies sp JOIN Models m ON m.ModelId = sp.ModelId
            WHERE sp.Name LIKE @like ESCAPE '!' OR sp.ConstrainedTable LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, sp.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public SecurityPolicyInfo? GetSecurityPolicy(string name)
    {
        using var conn = OpenReadOnly();
        return conn.QueryFirstOrDefault<SecurityPolicyInfo>(@"
            SELECT sp.Id, sp.Name, sp.ConstrainedTable, sp.PolicyQuery, sp.OperationType, sp.ContextType,
                   sp.IsEnabled, sp.IsMandatory, m.Name AS Model, sp.SourcePath
            FROM SecurityPolicies sp JOIN Models m ON m.ModelId = sp.ModelId
            WHERE sp.Name = @name LIMIT 1", new { name });
    }

    public IReadOnlyList<ConfigurationKeyInfo> SearchConfigurationKeys(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<ConfigurationKeyInfo>(@"
            SELECT ck.Id, ck.Name, ck.Label, ck.IsEnabled, ck.ParentKey, ck.LicenseCode, m.Name AS Model
            FROM ConfigurationKeys ck JOIN Models m ON m.ModelId = ck.ModelId
            WHERE ck.Name LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, ck.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public IReadOnlyList<TileInfo> SearchTiles(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<TileInfo>(@"
            SELECT t.Id, t.Name, t.MenuItemName, t.MenuItemType, t.Label, t.TileType, m.Name AS Model, t.SourcePath
            FROM Tiles t JOIN Models m ON m.ModelId = t.ModelId
            WHERE t.Name LIKE @like ESCAPE '!' OR t.MenuItemName LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, t.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    public IReadOnlyList<WorkspaceInfo> SearchWorkspaces(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<WorkspaceInfo>(@"
            SELECT ws.Id, ws.Name, ws.Label, m.Name AS Model, ws.SourcePath
            FROM Workspaces ws JOIN Models m ON m.ModelId = ws.ModelId
            WHERE ws.Name LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, ws.Name LIMIT @limit",
            new { like, limit }).ToList();
    }

    // ---- Phase 7: developer experience ----

    /// <summary>
    /// Find all batch job classes: RunBaseBatch subclasses and
    /// SysOperationServiceController subclasses. Requires Phase 4 v12 flags.
    /// </summary>
    public IReadOnlyList<ClassInfo> FindBatchJobs(string? model = null)
    {
        using var conn = OpenReadOnly();
        return conn.Query<ClassInfo>(@"
            SELECT c.ClassId, c.Name, m.Name AS Model, c.ExtendsName AS Extends,
                   c.IsAbstract, c.IsFinal, c.SourcePath
            FROM Classes c JOIN Models m ON m.ModelId = c.ModelId
            WHERE (c.IsRunBaseBatch = 1
               OR c.ExtendsName LIKE '%SysOperationServiceController%'
               OR c.ExtendsName LIKE '%RunBase%')
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY c.Name",
            new { model }).ToList();
    }

    /// <summary>
    /// Per-kind SQL fragments used by <see cref="FindUsagesFiltered"/>.
    /// Key is the canonical kind name in lower-case.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> KindSqlFragments =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["table"]      = "SELECT 'Table' AS Kind, t.Name AS Name, m.Name AS Model FROM Tables t JOIN Models m ON m.ModelId=t.ModelId WHERE t.Name LIKE @like ESCAPE '!'",
            ["class"]      = "SELECT 'Class' AS Kind, c.Name AS Name, m.Name AS Model FROM Classes c JOIN Models m ON m.ModelId=c.ModelId WHERE c.Name LIKE @like ESCAPE '!' OR c.ExtendsName LIKE @like ESCAPE '!'",
            ["edt"]        = "SELECT 'EDT' AS Kind, e.Name AS Name, m.Name AS Model FROM Edts e JOIN Models m ON m.ModelId=e.ModelId WHERE e.Name LIKE @like ESCAPE '!' OR e.ExtendsName LIKE @like ESCAPE '!'",
            ["enum"]       = "SELECT 'Enum' AS Kind, e.Name AS Name, m.Name AS Model FROM Enums e JOIN Models m ON m.ModelId=e.ModelId WHERE e.Name LIKE @like ESCAPE '!'",
            ["menuitem"]   = "SELECT 'MenuItem' AS Kind, mi.Name AS Name, m.Name AS Model FROM MenuItems mi JOIN Models m ON m.ModelId=mi.ModelId WHERE mi.Name LIKE @like ESCAPE '!' OR mi.Object LIKE @like ESCAPE '!'",
            ["form"]       = "SELECT 'Form' AS Kind, f.Name AS Name, m.Name AS Model FROM Forms f JOIN Models m ON m.ModelId=f.ModelId WHERE f.Name LIKE @like ESCAPE '!'",
            ["query"]      = "SELECT 'Query' AS Kind, q.Name AS Name, m.Name AS Model FROM Queries q JOIN Models m ON m.ModelId=q.ModelId WHERE q.Name LIKE @like ESCAPE '!'",
            ["view"]       = "SELECT 'View' AS Kind, v.Name AS Name, m.Name AS Model FROM Views v JOIN Models m ON m.ModelId=v.ModelId WHERE v.Name LIKE @like ESCAPE '!'",
            ["dataentity"] = "SELECT 'DataEntity' AS Kind, de.Name AS Name, m.Name AS Model FROM DataEntities de JOIN Models m ON m.ModelId=de.ModelId WHERE de.Name LIKE @like ESCAPE '!' OR de.PublicEntityName LIKE @like ESCAPE '!'",
            ["report"]     = "SELECT 'Report' AS Kind, r.Name AS Name, m.Name AS Model FROM Reports r JOIN Models m ON m.ModelId=r.ModelId WHERE r.Name LIKE @like ESCAPE '!'",
            ["service"]    = "SELECT 'Service' AS Kind, s.Name AS Name, m.Name AS Model FROM Services s JOIN Models m ON m.ModelId=s.ModelId WHERE s.Name LIKE @like ESCAPE '!'",
            ["workflow"]   = "SELECT 'Workflow' AS Kind, w.Name AS Name, m.Name AS Model FROM WorkflowTypes w JOIN Models m ON m.ModelId=w.ModelId WHERE w.Name LIKE @like ESCAPE '!'",
            ["map"]        = "SELECT 'Map' AS Kind, mp.Name AS Name, m.Name AS Model FROM Maps mp JOIN Models m ON m.ModelId=mp.ModelId WHERE mp.Name LIKE @like ESCAPE '!'",
        };

    /// <summary>
    /// Variant of <see cref="FindUsages"/> that filters to specific object kinds.
    /// <paramref name="kinds"/> should be values like "Table", "Class", "EDT".
    /// When empty or null, falls back to all kinds.
    /// Unlike the old implementation this builds a targeted SQL query so only
    /// the requested tables are scanned — much faster when searching a single kind.
    /// </summary>
    public IReadOnlyList<(string Kind, string Name, string Model)> FindUsagesFiltered(
        string needle, IReadOnlyList<string>? kinds, int limit = 100)
    {
        limit = ClampLimit(limit, 100);
        if (kinds is null || kinds.Count == 0)
            return FindUsages(needle, limit);

        var fragments = kinds
            .Select(k => k.Trim().ToLowerInvariant())
            .Where(k => KindSqlFragments.ContainsKey(k))
            .Select(k => KindSqlFragments[k])
            .ToList();

        // If all requested kinds are unknown fall back to broad search so we
        // don't silently return zero results due to a typo in --kind.
        if (fragments.Count == 0)
            return FindUsages(needle, limit);

        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(needle)}%";
        var sql = string.Join("\nUNION ALL\n", fragments) + "\nORDER BY Name\nLIMIT @limit";
        var rows = conn.Query<UsageRow>(sql, new { like, limit });
        return rows.Select(r => (r.Kind, r.Name, r.Model)).ToList();
    }

    /// <summary>
    /// Produce a change-impact report for an AOT object: CoC wrappers, event handlers,
    /// extensions, form datasources, data entities, queries, and reports that reference it.
    /// </summary>
    public ImpactReport AnalyzeImpact(string objectName)
    {
        using var conn = OpenReadOnly();

        var coc = FindCocExtensions(objectName);
        var handlers = FindEventSubscribers(objectName);
        var extensions = FindExtensions(objectName);

        // Forms that use the object as a datasource (by table name).
        var formDs = conn.Query<FormDataSourceRow>(@"
            SELECT f.Name AS FormName, m.Name AS Model
            FROM FormDataSources fds
            JOIN Forms f ON f.FormId = fds.FormId
            JOIN Models m ON m.ModelId = f.ModelId
            WHERE fds.TableName = @n",
            new { n = objectName }).ToList();

        // Data entities backed by this table via QueryName or staging.
        var entities = conn.Query<DataEntityInfo>(@"
            SELECT de.EntityId, de.Name, m.Name AS Model, de.PublicEntityName,
                   de.PublicCollectionName, de.StagingTable, de.QueryName, de.Label, de.SourcePath
            FROM DataEntities de JOIN Models m ON m.ModelId = de.ModelId
            WHERE de.StagingTable = @n OR de.QueryName = @n",
            new { n = objectName }).ToList();

        // Queries that join the object as a datasource table.
        var queries = conn.Query<QueryDataSourceRow>(@"
            SELECT q.Name AS QueryName, m.Name AS Model
            FROM QueryDataSources qds
            JOIN Queries q ON q.QueryId = qds.QueryId
            JOIN Models m ON m.ModelId = q.ModelId
            WHERE qds.TableName = @n",
            new { n = objectName }).ToList();

        return new ImpactReport(
            objectName,
            Direct:   coc.Cast<object>().Concat(extensions).Concat(handlers).ToList(),
            CocWrappers: coc.ToList(),
            EventHandlers: handlers.ToList(),
            Extensions: extensions.ToList(),
            FormDataSources: formDs.Select(r => new { r.FormName, r.Model }).Cast<object>().ToList(),
            DataEntities: entities.Cast<object>().ToList(),
            Queries: queries.Select(r => new { r.QueryName, r.Model }).Cast<object>().ToList());
    }

    private sealed class FormDataSourceRow { public string FormName { get; set; } = ""; public string Model { get; set; } = ""; }
    private sealed class QueryDataSourceRow { public string QueryName { get; set; } = ""; public string Model { get; set; } = ""; }

    // ---- Phase 7.4: command performance counters ----

    public void RecordCommandTiming(string command, long elapsedMs)
    {
        using var conn = Open();
        conn.Execute(@"INSERT INTO CommandTimings(Command, ElapsedMs, ExecutedUtc)
                       VALUES(@c, @ms, @ts)",
            new { c = command, ms = elapsedMs, ts = DateTime.UtcNow.ToString("O") });
    }

    public IReadOnlyList<CommandTimingRow> GetCommandTimings(int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        return conn.Query<CommandTimingRow>(@"
            SELECT Command,
                   COUNT(*) AS Calls,
                   AVG(ElapsedMs) AS AvgMs,
                   MAX(ElapsedMs) AS MaxMs,
                   MIN(ElapsedMs) AS MinMs
            FROM CommandTimings
            GROUP BY Command
            ORDER BY AvgMs DESC
            LIMIT @limit", new { limit }).ToList();
    }

    // ---- Phase 5: integration analysis ----

    /// <summary>
    /// Cross-checks all indexed data entities for integration-readiness issues.
    /// Optional <paramref name="model"/> restricts to a single model.
    /// </summary>
    public IReadOnlyList<IntegrationIssue> AnalyzeIntegration(string? model = null)
    {
        using var conn = OpenReadOnly();

        // Pull all entities (optionally filtered by model).
        var entities = conn.Query<DataEntityInfo>(@"
            SELECT e.EntityId, e.Name, m.Name AS Model, e.PublicEntityName, e.PublicCollectionName,
                   e.StagingTable, e.QueryName, e.Label, e.SourcePath
            FROM DataEntities e JOIN Models m ON m.ModelId = e.ModelId
            WHERE (@model IS NULL OR m.Name = @model)
            ORDER BY e.Name",
            new { model }).ToList();

        var issues = new List<IntegrationIssue>();

        // Detect duplicate PublicEntityName values.
        var publicNames = entities
            .Where(e => !string.IsNullOrEmpty(e.PublicEntityName))
            .GroupBy(e => e.PublicEntityName!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);
        foreach (var g in publicNames)
            foreach (var e in g)
                issues.Add(new IntegrationIssue(e.Name, e.Model, "DUPLICATE_PUBLIC_NAME",
                    $"PublicEntityName '{g.Key}' is shared with {g.Count() - 1} other entity(ies)."));

        foreach (var e in entities)
        {
            // Missing PublicCollectionName when OData-exposed.
            if (!string.IsNullOrEmpty(e.PublicEntityName) && string.IsNullOrEmpty(e.PublicCollectionName))
                issues.Add(new IntegrationIssue(e.Name, e.Model, "MISSING_COLLECTION_NAME",
                    "Entity has PublicEntityName but no PublicCollectionName — OData collection route will be missing."));

            // Missing staging table reference.
            if (e.StagingTable is null && !string.IsNullOrEmpty(e.PublicEntityName))
                issues.Add(new IntegrationIssue(e.Name, e.Model, "NO_STAGING_TABLE",
                    "OData-exposed entity has no StagingTable — DMF import/export will not be available."));

            // Zero fields mapped.
            var fieldCount = conn.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM DataEntityFields WHERE EntityId = @id", new { id = e.EntityId });
            if (fieldCount == 0)
                issues.Add(new IntegrationIssue(e.Name, e.Model, "NO_FIELDS",
                    "Entity has no fields mapped — likely incomplete scaffold."));

            // No mandatory fields.
            if (fieldCount > 0)
            {
                var mandatoryCount = conn.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM DataEntityFields WHERE EntityId = @id AND IsMandatory = 1",
                    new { id = e.EntityId });
                if (mandatoryCount == 0)
                    issues.Add(new IntegrationIssue(e.Name, e.Model, "NO_MANDATORY_FIELDS",
                        "Entity has no mandatory fields — key identification may be ambiguous for data management."));
            }
        }

        return issues.OrderBy(i => i.EntityName).ToList();
    }

    /// <summary>
    /// Builds a full integration surface report: OData entities, custom services,
    /// business events, workflow types, and batch jobs.
    /// Optional <paramref name="model"/> restricts all lists to one model.
    /// </summary>
    public IntegrationReport GetIntegrationReport(string? model = null)
    {
        using var conn = OpenReadOnly();

        var odataEntities = conn.Query<DataEntityInfo>(@"
            SELECT e.EntityId, e.Name, m.Name AS Model, e.PublicEntityName, e.PublicCollectionName,
                   e.StagingTable, e.QueryName, e.Label, e.SourcePath
            FROM DataEntities e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.PublicEntityName IS NOT NULL
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY e.Name", new { model }).ToList();

        var services = conn.Query<ServiceInfo>(@"
            SELECT s.ServiceId, s.Name, s.Class, m.Name AS Model, s.SourcePath
            FROM Services s JOIN Models m ON m.ModelId = s.ModelId
            WHERE @model IS NULL OR m.Name = @model
            ORDER BY s.Name", new { model }).ToList();

        var businessEvents = conn.Query<BusinessEventInfo>(@"
            SELECT be.Id, be.Name, be.Category, be.ContractClass, m.Name AS Model, be.SourcePath
            FROM BusinessEvents be JOIN Models m ON m.ModelId = be.ModelId
            WHERE @model IS NULL OR m.Name = @model
            ORDER BY be.Category, be.Name", new { model }).ToList();

        var workflowTypes = conn.Query<WorkflowTypeInfo>(@"
            SELECT w.Name, w.Category, w.DocumentClass, m.Name AS Model, w.SourcePath
            FROM WorkflowTypes w JOIN Models m ON m.ModelId = w.ModelId
            WHERE @model IS NULL OR m.Name = @model
            ORDER BY w.Name", new { model }).ToList();

        var batchJobs = conn.Query<ClassInfo>(@"
            SELECT c.ClassId, c.Name, m.Name AS Model, c.ExtendsName AS Extends,
                   c.IsAbstract, c.IsFinal, c.SourcePath
            FROM Classes c JOIN Models m ON m.ModelId = c.ModelId
            WHERE c.IsRunBaseBatch = 1
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY c.Name", new { model }).ToList();

        return new IntegrationReport(odataEntities, services, businessEvents, workflowTypes, batchJobs);
    }

    public IReadOnlyList<ModelInfo> ListModels()
    {
        using var conn = OpenReadOnly();
        return conn.Query<ModelInfo>(@"
            SELECT ModelId, Name, Publisher, Layer, IsCustom
            FROM Models ORDER BY Name").ToList();
    }

    public ModelDependencies? GetModelDependencies(string name)
    {
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
        var rows = conn.Query<(string Source, string Target)>(@"
            SELECT m.Name AS Source, d.Target AS Target
            FROM ModelDependencies d
            JOIN Models m ON m.ModelId = d.ModelId");
        // Use per-node HashSet<string> for O(1) duplicate detection instead of
        // List.Contains which is O(n) per edge, giving O(n²) overall for large graphs.
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            if (!seen.TryGetValue(r.Source, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seen[r.Source] = set;
            }
            set.Add(r.Target);
        }
        var graph = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in seen) graph[kv.Key] = kv.Value.ToList();
        // Ensure every model is a node even if it has no outgoing edges.
        foreach (var m in conn.Query<string>("SELECT Name FROM Models"))
        {
            if (!graph.ContainsKey(m)) graph[m] = Array.Empty<string>();
        }
        return graph;
    }

    // ---- additional read operations ----

    /// <summary>
    /// Tables that actually declare a field named <paramref name="name"/> (exact,
    /// case-insensitive) or whose field uses an EDT named <paramref name="name"/>
    /// (exact, case-insensitive, walking the EdtName column only — not relation
    /// FK targets). This answers "which tables contain field X" precisely; use
    /// this instead of <c>find refs</c> or relation lookups, which answer a
    /// different question (source-code references / FK relation targets) and
    /// silently inflate the result set with unrelated tables. See issue #101.
    /// </summary>
    public IReadOnlyList<TableFieldMatch> FindTablesByField(string name, string? model = null, int limit = 200)
    {
        limit = ClampLimit(limit, 200);
        using var conn = OpenReadOnly();

        // Base-table fields (TableFields). Queried as real columns so the SQLite
        // provider reports column types correctly; provenance is stamped in C#.
        var results = new List<TableFieldMatch>();
        foreach (var r in conn.Query(@"
            SELECT t.Name AS TableName, m.Name AS Model, f.Name AS FieldName,
                   f.Type AS Type, f.EdtName AS EdtName, f.Mandatory AS Mandatory
            FROM TableFields f
            JOIN Tables t ON t.TableId = f.TableId
            JOIN Models m ON m.ModelId = t.ModelId
            WHERE (f.Name = @name COLLATE NOCASE OR f.EdtName = @name COLLATE NOCASE)
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY t.Name
            LIMIT @limit", new { name, model, limit }))
        {
            results.Add(new TableFieldMatch(
                (string)r.TableName, (string)r.Model, (string)r.FieldName,
                (string?)r.Type, (string?)r.EdtName,
                r.Mandatory is not null && Convert.ToBoolean(r.Mandatory),
                Source: "base"));
        }

        // Extension-added fields (ExtensionFields). An AxTableExtension field
        // materialises as a real column on the target table's physical SQL table,
        // so omitting these made `find fields` under-report versus a direct
        // database column query (e.g. CustAccount added to ContactPerson via
        // ContactPerson.<Ext>). The @model filter matches the extension's owning
        // model here (vs the base table's model above).
        foreach (var r in conn.Query(@"
            SELECT ef.TargetTable AS TableName, m.Name AS Model, ef.Name AS FieldName,
                   ef.Type AS Type, ef.EdtName AS EdtName, ef.ExtensionName AS ExtensionName
            FROM ExtensionFields ef
            JOIN Models m ON m.ModelId = ef.ModelId
            WHERE (ef.Name = @name COLLATE NOCASE OR ef.EdtName = @name COLLATE NOCASE)
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY ef.TargetTable
            LIMIT @limit", new { name, model, limit }))
        {
            results.Add(new TableFieldMatch(
                (string)r.TableName, (string)r.Model, (string)r.FieldName,
                (string?)r.Type, (string?)r.EdtName, Mandatory: false,
                Source: "extension", ExtensionName: (string?)r.ExtensionName));
        }

        return results
            .OrderBy(x => x.TableName, StringComparer.Ordinal)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public IReadOnlyList<TableInfo> SearchTables(string query, string? model = null, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<TableInfo>(@"
            SELECT t.TableId, t.Name, m.Name AS Model, t.Label, t.SourcePath,
                   t.SaveDataPerCompany, t.CacheLookup, t.OccEnabled, t.ValidTimeStateFieldType,
                   t.TableExtends, t.AOSAuthorization, t.FormRef, t.ListPageRef, t.SystemTable
            FROM Tables t JOIN Models m ON m.ModelId = t.ModelId
            WHERE t.Name LIKE @like ESCAPE '!'
              AND (@model IS NULL OR m.Name = @model)
            ORDER BY m.IsCustom DESC, t.Name
            LIMIT @limit", new { like, model, limit }).ToList();
    }

    public IReadOnlyList<EdtInfo> SearchEdts(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<EdtInfo>(@"
            SELECT e.Name, m.Name AS Model, e.ExtendsName AS Extends,
                   e.BaseType, e.Label, e.StringSize,
                   e.ReferenceTable, e.FormHelp, e.AnalysisUsage, e.EnumType
            FROM Edts e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, e.Name
            LIMIT @limit", new { like, limit }).ToList();
    }

    public IReadOnlyList<EnumInfo> SearchEnums(string query, int limit = 50)
    {
        limit = ClampLimit(limit, 50);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(query)}%";
        return conn.Query<EnumInfo>(@"
            SELECT e.Name, m.Name AS Model, e.Label
            FROM Enums e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name LIKE @like ESCAPE '!'
            ORDER BY m.IsCustom DESC, e.Name
            LIMIT @limit", new { like, limit }).ToList();
    }

    public EnumDetails? GetEnum(string name)
    {
        using var conn = OpenReadOnly();
        var en = conn.QueryFirstOrDefault<EnumHeaderRow>(@"
            SELECT e.EnumId AS EnumId, e.Name AS Name, m.Name AS Model, e.Label AS Label
            FROM Enums e JOIN Models m ON m.ModelId = e.ModelId
            WHERE e.Name = @name LIMIT 1", new { name });
        if (en is null) return null;
        var values = conn.Query<EnumValueInfo>(@"
            SELECT Name, Value, Label
            FROM EnumValues WHERE EnumId = @id
            ORDER BY COALESCE(Value, EnumValueId)", new { id = en.EnumId }).ToList();
        // Rows extracted before UseEnumValue=No synthesis store NULL — the
        // runtime value of such members is their declaration order.
        for (var i = 0; i < values.Count; i++)
            if (values[i].Value is null) values[i] = values[i] with { Value = i };
        return new EnumDetails(new EnumInfo(en.Name, en.Model, en.Label), values);
    }

    private sealed record EnumHeaderRow(long EnumId, string Name, string Model, string? Label);
    private sealed class UsageRow
    {
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
    }

    public LabelMatch? GetLabel(string file, string language, string key)
    {
        using var conn = OpenReadOnly();
        return conn.QueryFirstOrDefault<LabelMatch>(@"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE LabelFile = @file COLLATE NOCASE
              AND Language  = @lang COLLATE NOCASE
              AND Key       = @key  COLLATE NOCASE
            LIMIT 1", new { file, lang = language, key });
    }

    /// <summary>
    /// Resolve a label token like "@SYS12345" or "@AccountsPayableMobile:Amount"
    /// (the '@' is optional). Tries to split the label file from the key and return
    /// one row per requested language.
    /// </summary>
    /// <remarks>
    /// Both token shapes ship in the AOT. The legacy one packs the file into the
    /// alphabetic prefix ("@SYS12345"); the current one separates the two with a colon
    /// ("@AccountsPayableMobile:Amount") and its key need contain no digit at all — the
    /// prefix-then-digits split alone resolved <em>nothing</em> for it, which is what the
    /// shipped packages are almost entirely written in (every &lt;Label&gt; element of
    /// AccountsPayableMobile, for one).
    /// </remarks>
    public IReadOnlyList<LabelMatch> ResolveLabel(string token, IReadOnlyCollection<string>? languages = null)
    {
        if (string.IsNullOrWhiteSpace(token)) return Array.Empty<LabelMatch>();
        var raw = token.TrimStart('@').Trim();
        if (raw.Length == 0) return Array.Empty<LabelMatch>();

        string prefix, suffix;
        var colon = raw.IndexOf(':');
        if (colon > 0 && colon < raw.Length - 1)
        {
            // "AccountsPayableMobile:Amount" -> file="AccountsPayableMobile", key="Amount".
            // The key half is only ever looked up inside its own file: a bare "Amount"
            // matches a dozen label files, and answering with the wrong file's text is
            // worse than not answering.
            prefix = raw.Substring(0, colon);
            suffix = raw.Substring(colon + 1);
        }
        else
        {
            // Split on first digit: "SYS12345" -> prefix="SYS", suffix="12345".
            int splitIdx = 0;
            while (splitIdx < raw.Length && char.IsLetter(raw[splitIdx])) splitIdx++;
            prefix = splitIdx > 0 ? raw.Substring(0, splitIdx) : raw;
            suffix = splitIdx < raw.Length ? raw.Substring(splitIdx) : string.Empty;
        }

        // Normalize to lowercase so both 'en-US' (Windows filesystem) and 'en-us'
        // (Linux filesystem, Microsoft packages) match the caller's request.
        var langsLower = languages?.Select(l => l.ToLowerInvariant()).ToList();

        // D365FO label extractors store the Key in two formats depending on the
        // extractor version: either as a bare numeric-suffix ("12345") or as the
        // full token WITH the @ sigil ("@SYS12345"). We try all three variants so
        // that both storage formats resolve correctly.
        var rawWithAt = "@" + raw;   // e.g. "@SYS11307"

        using var conn = OpenReadOnly();
        // Try two shapes: Key == raw (e.g. "SYS12345") in file=prefix,
        // or Key == suffix (e.g. "12345") in file=prefix. Fall back to
        // exact key match across all files for odd prefixes.
        //
        // NOTE: Dapper's list expansion rewrites "IN @langs" to "IN (@langs0, @langs1, ...)"
        // which breaks a combined "@langs IS NULL" check (SQLite: "row value misused").
        // We branch the SQL in C# instead.
        string sql;
        if (langsLower is null || langsLower.Count == 0)
        {
            sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE (LabelFile = @prefix AND Key IN (@raw, @rawWithAt, @suffix))
               OR Key = @raw
               OR Key = @rawWithAt
            ORDER BY LabelFile, Language";
            return conn.Query<LabelMatch>(sql, new { prefix, raw, rawWithAt, suffix }).ToList();
        }
        else
        {
            sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE LOWER(Language) IN @langs
              AND (
                    (LabelFile = @prefix AND Key IN (@raw, @rawWithAt, @suffix))
                 OR Key = @raw
                 OR Key = @rawWithAt
              )
            ORDER BY LabelFile, Language";
            return conn.Query<LabelMatch>(sql, new { langs = langsLower, prefix, raw, rawWithAt, suffix }).ToList();
        }
    }

    /// <summary>
    /// Find any index entity whose name contains the given substring. Used by
    /// `d365fo find usages` to approximate a cross-object search without
    /// loading X++ source itself.
    /// </summary>
    public IReadOnlyList<(string Kind, string Name, string Model)> FindUsages(string needle, int limit = 100)
    {
        limit = ClampLimit(limit, 100);
        using var conn = OpenReadOnly();
        var like = $"%{EscapeLike(needle)}%";
        var rows = conn.Query<UsageRow>(@"
            SELECT 'Table' AS Kind, t.Name AS Name, m.Name AS Model FROM Tables t JOIN Models m ON m.ModelId=t.ModelId WHERE t.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Class', c.Name, m.Name FROM Classes c JOIN Models m ON m.ModelId=c.ModelId WHERE c.Name LIKE @like ESCAPE '!' OR c.ExtendsName LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'EDT',   e.Name, m.Name FROM Edts e JOIN Models m ON m.ModelId=e.ModelId WHERE e.Name LIKE @like ESCAPE '!' OR e.ExtendsName LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Enum',  e.Name, m.Name FROM Enums e JOIN Models m ON m.ModelId=e.ModelId WHERE e.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'MenuItem', mi.Name, m.Name FROM MenuItems mi JOIN Models m ON m.ModelId=mi.ModelId WHERE mi.Name LIKE @like ESCAPE '!' OR mi.Object LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Form',  f.Name, m.Name FROM Forms f JOIN Models m ON m.ModelId=f.ModelId WHERE f.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Query', q.Name, m.Name FROM Queries q JOIN Models m ON m.ModelId=q.ModelId WHERE q.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'View',  v.Name, m.Name FROM Views v JOIN Models m ON m.ModelId=v.ModelId WHERE v.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'DataEntity', de.Name, m.Name FROM DataEntities de JOIN Models m ON m.ModelId=de.ModelId WHERE de.Name LIKE @like ESCAPE '!' OR de.PublicEntityName LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Report', r.Name, m.Name FROM Reports r JOIN Models m ON m.ModelId=r.ModelId WHERE r.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Service', s.Name, m.Name FROM Services s JOIN Models m ON m.ModelId=s.ModelId WHERE s.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Workflow', w.Name, m.Name FROM WorkflowTypes w JOIN Models m ON m.ModelId=w.ModelId WHERE w.Name LIKE @like ESCAPE '!'
            UNION ALL
            SELECT 'Map', mp.Name, m.Name FROM Maps mp JOIN Models m ON m.ModelId=mp.ModelId WHERE mp.Name LIKE @like ESCAPE '!'
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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();
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
        limit = ClampLimit(limit, 200);
        using var conn = OpenReadOnly();
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
    /// When <paramref name="indexMethodSource"/> is true and the extracted
    /// methods carry their <see cref="ExtractedMethod.Body"/> (opt-in via
    /// <c>index extract --index-source</c>), method bodies are also written to
    /// the <c>MethodSourceFts</c> full-text index. The model's existing FTS rows
    /// are always cleared first, so re-extracting without the flag cleans up
    /// stale source previously indexed for that model.
    /// </summary>
    public void ApplyExtract(ExtractBatch batch, string? sourceFingerprint, bool indexMethodSource = false)
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
        conn.Execute("DELETE FROM ExtensionFields WHERE ModelId=@m", new { m = modelId }, tx);
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
        // Delete Maps children explicitly: ON DELETE CASCADE is present on fresh schemas but
        // may be absent on DBs created before this migration, so we cascade manually.
        conn.Execute("DELETE FROM MapFields WHERE MapId IN (SELECT MapId FROM Maps WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM MapTables WHERE MapId IN (SELECT MapId FROM Maps WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Maps WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM BusinessEvents WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM SecurityPolicies WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ConfigurationKeys WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Tiles WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Workspaces WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM ModelDependencies WHERE ModelId=@m", new { m = modelId }, tx);
        // Method-body FTS (v15) is keyed by Model name. Always clear this model's
        // rows up front so that re-extracting it — with or without --index-source —
        // never leaves stale source behind. Guarded: a SQLite build lacking FTS5
        // (and thus the MethodSourceFts table) degrades to a no-op.
        try { conn.Execute("DELETE FROM MethodSourceFts WHERE Model=@n", new { n = batch.Model }, tx); }
        catch (Microsoft.Data.Sqlite.SqliteException) { indexMethodSource = false; }
        // Labels are keyed by file+lang, not model; we delete by file instead.
        foreach (var file in batch.Labels.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase))
            conn.Execute("DELETE FROM Labels WHERE LabelFile=@f", new { f = file }, tx);
        // LabelFiles mirrors that keying (v17). Replaced per physical file id so a
        // re-extract never accumulates stale paths.
        foreach (var file in batch.LabelFiles.Select(l => l.File).Distinct(StringComparer.OrdinalIgnoreCase))
            conn.Execute("DELETE FROM LabelFiles WHERE LabelFile=@f", new { f = file }, tx);
        foreach (var lf in batch.LabelFiles)
            conn.Execute("INSERT INTO LabelFiles(LabelFile, Language, SourcePath) VALUES(@f, @lg, @p)",
                new { f = lf.File, lg = lf.Language, p = lf.SourcePath }, tx);

        // TableFields and TableMethods are high-volume inserts. Use prepared
        // SqliteCommands with parameter rebinding for ~3-5x throughput vs
        // Dapper's per-row anonymous-object reflection (same technique as Labels).
        // Tables main insert — prepared statement (30k+ rows for large models).
        using var tableCmd = conn.CreateCommand();
        tableCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        tableCmd.CommandText = @"INSERT INTO Tables(Name, ModelId, Label, SourcePath,
            SaveDataPerCompany, CacheLookup, OccEnabled, ValidTimeStateFieldType, TableExtends,
            AOSAuthorization, FormRef, ListPageRef, SystemTable)
            VALUES($n, $m, $l, $p, $sdc, $cl, $occ, $vts, $tx, $aos, $fr, $lpr, $st)";
        var tblName    = tableCmd.Parameters.Add("$n",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tblModelId = tableCmd.Parameters.Add("$m",   Microsoft.Data.Sqlite.SqliteType.Integer);
        var tblLabel   = tableCmd.Parameters.Add("$l",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tblPath    = tableCmd.Parameters.Add("$p",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tblSdc     = tableCmd.Parameters.Add("$sdc", Microsoft.Data.Sqlite.SqliteType.Text);
        var tblCl      = tableCmd.Parameters.Add("$cl",  Microsoft.Data.Sqlite.SqliteType.Text);
        var tblOcc     = tableCmd.Parameters.Add("$occ", Microsoft.Data.Sqlite.SqliteType.Integer);
        var tblVts     = tableCmd.Parameters.Add("$vts", Microsoft.Data.Sqlite.SqliteType.Text);
        var tblTx      = tableCmd.Parameters.Add("$tx",  Microsoft.Data.Sqlite.SqliteType.Text);
        var tblAos     = tableCmd.Parameters.Add("$aos", Microsoft.Data.Sqlite.SqliteType.Text);
        var tblFr      = tableCmd.Parameters.Add("$fr",  Microsoft.Data.Sqlite.SqliteType.Text);
        var tblLpr     = tableCmd.Parameters.Add("$lpr", Microsoft.Data.Sqlite.SqliteType.Text);
        var tblSt      = tableCmd.Parameters.Add("$st",  Microsoft.Data.Sqlite.SqliteType.Integer);
        tableCmd.Prepare();

        using var tableFieldCmd = conn.CreateCommand();
        tableFieldCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        tableFieldCmd.CommandText = "INSERT INTO TableFields(TableId, Name, Type, EdtName, Label, Mandatory) VALUES($tid, $n, $ty, $e, $l, $md)";
        var tfTid       = tableFieldCmd.Parameters.Add("$tid", Microsoft.Data.Sqlite.SqliteType.Integer);
        var tfName      = tableFieldCmd.Parameters.Add("$n",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tfType      = tableFieldCmd.Parameters.Add("$ty",  Microsoft.Data.Sqlite.SqliteType.Text);
        var tfEdt       = tableFieldCmd.Parameters.Add("$e",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tfLabel     = tableFieldCmd.Parameters.Add("$l",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tfMandatory = tableFieldCmd.Parameters.Add("$md",  Microsoft.Data.Sqlite.SqliteType.Integer);
        tableFieldCmd.Prepare();

        using var tableMtdCmd = conn.CreateCommand();
        tableMtdCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        tableMtdCmd.CommandText = "INSERT INTO TableMethods(TableId, Name, Signature, IsStatic, ReturnType, HasDocComment, HasTodayCall, HasDoInsertOrUpdate, IsEmptyOverride) VALUES($tid, $n, $s, $st, $rt, $hd, $ht, $hi, $ieo)";
        var tmTid         = tableMtdCmd.Parameters.Add("$tid", Microsoft.Data.Sqlite.SqliteType.Integer);
        var tmName        = tableMtdCmd.Parameters.Add("$n",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tmSig         = tableMtdCmd.Parameters.Add("$s",   Microsoft.Data.Sqlite.SqliteType.Text);
        var tmStatic      = tableMtdCmd.Parameters.Add("$st",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var tmRet         = tableMtdCmd.Parameters.Add("$rt",  Microsoft.Data.Sqlite.SqliteType.Text);
        var tmHasDoc      = tableMtdCmd.Parameters.Add("$hd",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var tmHasToday    = tableMtdCmd.Parameters.Add("$ht",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var tmHasDoInsert = tableMtdCmd.Parameters.Add("$hi",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var tmIsEmptyOvr  = tableMtdCmd.Parameters.Add("$ieo", Microsoft.Data.Sqlite.SqliteType.Integer);
        tableMtdCmd.Prepare();

        // v15: opt-in method-body FTS. Prepared once, reused for both table and
        // class methods. Only created when indexing is requested so the no-flag
        // path pays nothing.
        Microsoft.Data.Sqlite.SqliteCommand? ftsCmd = null;
        Microsoft.Data.Sqlite.SqliteParameter? ftsBody = null, ftsKind = null, ftsObj = null, ftsMethod = null, ftsModel = null, ftsPath = null;
        if (indexMethodSource)
        {
            ftsCmd = conn.CreateCommand();
            ftsCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            ftsCmd.CommandText = "INSERT INTO MethodSourceFts(Body, Kind, ObjectName, MethodName, Model, SourcePath) VALUES($b, $k, $o, $mn, $md, $p)";
            ftsBody   = ftsCmd.Parameters.Add("$b",  Microsoft.Data.Sqlite.SqliteType.Text);
            ftsKind   = ftsCmd.Parameters.Add("$k",  Microsoft.Data.Sqlite.SqliteType.Text);
            ftsObj    = ftsCmd.Parameters.Add("$o",  Microsoft.Data.Sqlite.SqliteType.Text);
            ftsMethod = ftsCmd.Parameters.Add("$mn", Microsoft.Data.Sqlite.SqliteType.Text);
            ftsModel  = ftsCmd.Parameters.Add("$md", Microsoft.Data.Sqlite.SqliteType.Text);
            ftsPath   = ftsCmd.Parameters.Add("$p",  Microsoft.Data.Sqlite.SqliteType.Text);
            ftsCmd.Prepare();
        }

        // Local helper: index one method body if it carries source text. No-op
        // when indexing is off or the method has no captured body.
        void IndexBody(string kind, string objectName, string? sourcePath, string method, string? body)
        {
            if (ftsCmd is null || string.IsNullOrEmpty(body)) return;
            ftsBody!.Value   = body;
            ftsKind!.Value   = kind;
            ftsObj!.Value    = objectName;
            ftsMethod!.Value = method;
            ftsModel!.Value  = batch.Model;
            ftsPath!.Value   = (object?)sourcePath ?? DBNull.Value;
            ftsCmd.ExecuteNonQuery();
        }

        foreach (var t in batch.Tables)
        {
            tblName.Value    = t.Name;
            tblModelId.Value = modelId;
            tblLabel.Value   = (object?)t.Label           ?? DBNull.Value;
            tblPath.Value    = (object?)t.SourcePath      ?? DBNull.Value;
            tblSdc.Value     = (object?)t.SaveDataPerCompany ?? DBNull.Value;
            tblCl.Value      = (object?)t.CacheLookup     ?? DBNull.Value;
            tblOcc.Value     = t.OccEnabled ? 1 : 0;
            tblVts.Value     = (object?)t.ValidTimeStateFieldType ?? DBNull.Value;
            tblTx.Value      = (object?)t.TableExtends    ?? DBNull.Value;
            tblAos.Value     = (object?)t.AOSAuthorization ?? DBNull.Value;
            tblFr.Value      = (object?)t.FormRef          ?? DBNull.Value;
            tblLpr.Value     = (object?)t.ListPageRef      ?? DBNull.Value;
            tblSt.Value      = t.SystemTable ? 1 : 0;
            tableCmd.ExecuteNonQuery();
            var tableId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            tfTid.Value = tableId;
            foreach (var f in t.Fields)
            {
                tfName.Value      = f.Name;
                tfType.Value      = (object?)f.Type    ?? DBNull.Value;
                tfEdt.Value       = (object?)f.EdtName ?? DBNull.Value;
                tfLabel.Value     = (object?)f.Label   ?? DBNull.Value;
                tfMandatory.Value = f.Mandatory ? 1 : 0;
                tableFieldCmd.ExecuteNonQuery();
            }
            tmTid.Value = tableId;
            foreach (var mtd in t.Methods)
            {
                tmName.Value        = mtd.Name;
                tmSig.Value         = (object?)mtd.Signature  ?? DBNull.Value;
                tmStatic.Value      = mtd.IsStatic ? 1 : 0;
                tmRet.Value         = (object?)mtd.ReturnType ?? DBNull.Value;
                tmHasDoc.Value      = mtd.HasDocComment ? 1 : 0;
                tmHasToday.Value    = mtd.HasTodayCall ? 1 : 0;
                tmHasDoInsert.Value = mtd.HasDoInsertOrUpdate ? 1 : 0;
                tmIsEmptyOvr.Value  = mtd.IsEmptyOverride ? 1 : 0;
                tableMtdCmd.ExecuteNonQuery();
                IndexBody("Table", t.Name, t.SourcePath, mtd.Name, mtd.Body);
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

        // Methods are the highest-volume insert (~500k+ across all models). Apply
        // the same prepared-statement optimization as Labels for ~3-5x throughput.
        // Classes + ClassAttributes + Methods: all highest-volume for large models.
        // Prepared statements eliminate per-row Dapper reflection overhead (~50k classes
        // × ~10 methods in ApplicationSuite/Foundation).
        using var classCmd = conn.CreateCommand();
        classCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        classCmd.CommandText = "INSERT INTO Classes(Name, ModelId, ExtendsName, IsAbstract, IsFinal, SourcePath, IsRunBaseBatch, HasCanGoBatch, HasPublicInstanceFields) VALUES($n, $m, $e, $a, $f, $p, $rb, $cgb, $pif)";
        var clsName     = classCmd.Parameters.Add("$n",   Microsoft.Data.Sqlite.SqliteType.Text);
        var clsModel    = classCmd.Parameters.Add("$m",   Microsoft.Data.Sqlite.SqliteType.Integer);
        var clsExtends  = classCmd.Parameters.Add("$e",   Microsoft.Data.Sqlite.SqliteType.Text);
        var clsAbstract = classCmd.Parameters.Add("$a",   Microsoft.Data.Sqlite.SqliteType.Integer);
        var clsFinal    = classCmd.Parameters.Add("$f",   Microsoft.Data.Sqlite.SqliteType.Integer);
        var clsPath     = classCmd.Parameters.Add("$p",   Microsoft.Data.Sqlite.SqliteType.Text);
        var clsRunBase  = classCmd.Parameters.Add("$rb",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var clsCanGo    = classCmd.Parameters.Add("$cgb", Microsoft.Data.Sqlite.SqliteType.Integer);
        var clsPubField = classCmd.Parameters.Add("$pif", Microsoft.Data.Sqlite.SqliteType.Integer);
        classCmd.Prepare();

        using var attrCmd = conn.CreateCommand();
        attrCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        attrCmd.CommandText = "INSERT INTO ClassAttributes(ClassId, MethodName, AttributeName, RawArgs) VALUES($c, $m, $n, $a)";
        var attrCid  = attrCmd.Parameters.Add("$c", Microsoft.Data.Sqlite.SqliteType.Integer);
        var attrMeth = attrCmd.Parameters.Add("$m", Microsoft.Data.Sqlite.SqliteType.Text);
        var attrName = attrCmd.Parameters.Add("$n", Microsoft.Data.Sqlite.SqliteType.Text);
        var attrArgs = attrCmd.Parameters.Add("$a", Microsoft.Data.Sqlite.SqliteType.Text);
        attrCmd.Prepare();

        using var methodCmd = conn.CreateCommand();
        methodCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
        methodCmd.CommandText = "INSERT INTO Methods(ClassId, Name, Signature, IsStatic, ReturnType, HasDocComment, HasTodayCall, HasDoInsertOrUpdate, HasInsertInLoop, HasNestedSelect, HasForceLiterals, HasForUpdateWithoutUpdate, HasTryCatchInTts, HasEmptyLoop) VALUES($cid, $n, $s, $st, $rt, $hd, $ht, $hi, $hil, $hns, $hfl, $hfu, $htt, $hel)";
        var mtdCid         = methodCmd.Parameters.Add("$cid", Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdName        = methodCmd.Parameters.Add("$n",   Microsoft.Data.Sqlite.SqliteType.Text);
        var mtdSig         = methodCmd.Parameters.Add("$s",   Microsoft.Data.Sqlite.SqliteType.Text);
        var mtdStatic      = methodCmd.Parameters.Add("$st",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdRet         = methodCmd.Parameters.Add("$rt",  Microsoft.Data.Sqlite.SqliteType.Text);
        var mtdHasDoc      = methodCmd.Parameters.Add("$hd",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdHasToday    = methodCmd.Parameters.Add("$ht",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdHasDoInsert = methodCmd.Parameters.Add("$hi",  Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdInsertLoop  = methodCmd.Parameters.Add("$hil", Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdNestedSel   = methodCmd.Parameters.Add("$hns", Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdForceLit    = methodCmd.Parameters.Add("$hfl", Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdForUpdNoUpd = methodCmd.Parameters.Add("$hfu", Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdTtsTry      = methodCmd.Parameters.Add("$htt", Microsoft.Data.Sqlite.SqliteType.Integer);
        var mtdEmptyLoop   = methodCmd.Parameters.Add("$hel", Microsoft.Data.Sqlite.SqliteType.Integer);
        methodCmd.Prepare();

        foreach (var c in batch.Classes)
        {
            clsName.Value     = c.Name;
            clsModel.Value    = modelId;
            clsExtends.Value  = (object?)c.Extends    ?? DBNull.Value;
            clsAbstract.Value = c.IsAbstract ? 1 : 0;
            clsFinal.Value    = c.IsFinal    ? 1 : 0;
            clsPath.Value     = (object?)c.SourcePath ?? DBNull.Value;
            clsRunBase.Value  = c.IsRunBaseBatch ? 1 : 0;
            clsCanGo.Value    = c.HasCanGoBatch ? 1 : 0;
            clsPubField.Value = c.HasPublicInstanceFields ? 1 : 0;
            classCmd.ExecuteNonQuery();
            var classId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            mtdCid.Value = classId;
            foreach (var mtd in c.Methods)
            {
                mtdName.Value        = mtd.Name;
                mtdSig.Value         = (object?)mtd.Signature  ?? DBNull.Value;
                mtdStatic.Value      = mtd.IsStatic ? 1 : 0;
                mtdRet.Value         = (object?)mtd.ReturnType ?? DBNull.Value;
                mtdHasDoc.Value      = mtd.HasDocComment ? 1 : 0;
                mtdHasToday.Value    = mtd.HasTodayCall ? 1 : 0;
                mtdHasDoInsert.Value = mtd.HasDoInsertOrUpdate ? 1 : 0;
                mtdInsertLoop.Value  = mtd.HasInsertInLoop ? 1 : 0;
                mtdNestedSel.Value   = mtd.HasNestedSelect ? 1 : 0;
                mtdForceLit.Value    = mtd.HasForceLiterals ? 1 : 0;
                mtdForUpdNoUpd.Value = mtd.HasForUpdateWithoutUpdate ? 1 : 0;
                mtdTtsTry.Value      = mtd.HasTryCatchInTts ? 1 : 0;
                mtdEmptyLoop.Value   = mtd.HasEmptyLoop ? 1 : 0;
                methodCmd.ExecuteNonQuery();
                IndexBody("Class", c.Name, c.SourcePath, mtd.Name, mtd.Body);
            }
            attrCid.Value = classId;
            foreach (var a in c.Attributes)
            {
                attrMeth.Value = (object?)a.MethodName    ?? DBNull.Value;
                attrName.Value = a.AttributeName;
                attrArgs.Value = (object?)a.RawArgs       ?? DBNull.Value;
                attrCmd.ExecuteNonQuery();
            }
        }

        foreach (var e in batch.Edts)
        {
            conn.Execute(@"INSERT INTO Edts(Name, ModelId, ExtendsName, BaseType, Label, StringSize, ReferenceTable, FormHelp, AnalysisUsage, EnumType)
                           VALUES(@n, @m, @e, @b, @l, @s, @rt, @fh, @au, @et)",
                         new { n = e.Name, m = modelId, e = e.Extends, b = e.BaseType, l = e.Label, s = e.StringSize,
                               rt = e.ReferenceTable, fh = e.FormHelp, au = e.AnalysisUsage, et = e.EnumType }, tx);
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

        // Labels are the highest-volume insert (100k+ for large models).
        // Use a prepared SqliteCommand with parameter rebinding for ~3-5x
        // throughput vs Dapper's per-row anonymous-object reflection.
        if (batch.Labels.Count > 0)
        {
            using var labelCmd = conn.CreateCommand();
            labelCmd.Transaction = (Microsoft.Data.Sqlite.SqliteTransaction)tx;
            labelCmd.CommandText = "INSERT INTO Labels(LabelFile, Language, Key, Value) VALUES($f, $lg, $k, $v)";
            var pFile = labelCmd.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Text);
            var pLang = labelCmd.Parameters.Add("$lg", Microsoft.Data.Sqlite.SqliteType.Text);
            var pKey = labelCmd.Parameters.Add("$k", Microsoft.Data.Sqlite.SqliteType.Text);
            var pVal = labelCmd.Parameters.Add("$v", Microsoft.Data.Sqlite.SqliteType.Text);
            labelCmd.Prepare();
            foreach (var l in batch.Labels)
            {
                pFile.Value = l.File;
                pLang.Value = l.Language;
                pKey.Value = l.Key;
                pVal.Value = (object?)l.Value ?? DBNull.Value;
                labelCmd.ExecuteNonQuery();
            }
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
            foreach (var f in ext.AddedFields)
            {
                conn.Execute(@"INSERT INTO ExtensionFields(TargetTable, Name, Type, EdtName, ExtensionName, ModelId)
                               VALUES(@t, @n, @ty, @e, @x, @m)",
                             new { t = ext.TargetName, n = f.Name, ty = f.Type, e = f.EdtName, x = ext.ExtensionName, m = modelId }, tx);
            }
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
        foreach (var map in batch.Maps)
        {
            conn.Execute(@"INSERT INTO Maps(Name, ModelId, Label, SourcePath)
                           VALUES(@n, @m, @l, @p)",
                         new { n = map.Name, m = modelId, l = map.Label, p = map.SourcePath }, tx);
            var mapId = conn.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: tx);
            foreach (var f in map.Fields)
            {
                conn.Execute(@"INSERT INTO MapFields(MapId, Name, Type, EdtName, Label)
                               VALUES(@mid, @n, @ty, @e, @l)",
                             new { mid = mapId, n = f.Name, ty = f.Type, e = f.EdtName, l = f.Label }, tx);
            }
            foreach (var tname in map.MappedTables)
            {
                conn.Execute("INSERT INTO MapTables(MapId, TableName) VALUES(@mid, @t)",
                             new { mid = mapId, t = tname }, tx);
            }
        }
        foreach (var be in batch.BusinessEvents)
        {
            conn.Execute(@"INSERT INTO BusinessEvents(Name, Category, ContractClass, ModelId, SourcePath)
                           VALUES(@n, @c, @cc, @m, @p)",
                         new { n = be.Name, c = be.Category, cc = be.ContractClass, m = modelId, p = be.SourcePath }, tx);
        }
        foreach (var sp in batch.SecurityPolicies)
        {
            conn.Execute(@"INSERT INTO SecurityPolicies(Name, ConstrainedTable, PolicyQuery, OperationType, ContextType, IsEnabled, IsMandatory, ModelId, SourcePath)
                           VALUES(@n, @ct, @pq, @ot, @cx, @ie, @im, @m, @p)",
                         new { n = sp.Name, ct = sp.ConstrainedTable, pq = sp.PolicyQuery, ot = sp.OperationType,
                               cx = sp.ContextType, ie = sp.IsEnabled ? 1 : 0, im = sp.IsMandatory ? 1 : 0,
                               m = modelId, p = sp.SourcePath }, tx);
        }
        foreach (var ck in batch.ConfigurationKeys)
        {
            conn.Execute(@"INSERT INTO ConfigurationKeys(Name, Label, IsEnabled, ParentKey, LicenseCode, ModelId)
                           VALUES(@n, @l, @ie, @pk, @lc, @m)",
                         new { n = ck.Name, l = ck.Label, ie = ck.IsEnabled ? 1 : 0, pk = ck.ParentKey, lc = ck.LicenseCode, m = modelId }, tx);
        }
        foreach (var tile in batch.Tiles)
        {
            conn.Execute(@"INSERT INTO Tiles(Name, MenuItemName, MenuItemType, Label, TileType, ModelId, SourcePath)
                           VALUES(@n, @mn, @mt, @l, @tt, @m, @p)",
                         new { n = tile.Name, mn = tile.MenuItemName, mt = tile.MenuItemType, l = tile.Label,
                               tt = tile.TileType, m = modelId, p = tile.SourcePath }, tx);
        }
        foreach (var ws in batch.Workspaces)
        {
            conn.Execute(@"INSERT INTO Workspaces(Name, Label, ModelId, SourcePath)
                           VALUES(@n, @l, @m, @p)",
                         new { n = ws.Name, l = ws.Label, m = modelId, p = ws.SourcePath }, tx);
        }

        foreach (var dep in batch.Dependencies.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            conn.Execute(@"INSERT INTO ModelDependencies(ModelId, Target) VALUES(@m, @t)",
                         new { m = modelId, t = dep }, tx);
        }

        MinePropertyStats(conn, tx, batch);

        ftsCmd?.Dispose();

        tx.Commit();

        // Keep the WAL bounded across models without paying a checkpoint per model.
        MaybeCheckpointWal(conn);

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
        using var conn = OpenReadOnly();
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
        using var conn = OpenReadOnly();

        var perModel = conn.Query(@"
            SELECT m.Name AS Model, m.IsCustom AS IsCustom,
                   COALESCE(t.Cnt, 0) AS Tables,
                   COALESCE(c.Cnt, 0) AS Classes,
                   COALESCE(e.Cnt, 0) AS Edts,
                   COALESCE(en.Cnt, 0) AS Enums,
                   COALESCE(mi.Cnt, 0) AS MenuItems,
                   COALESCE(f.Cnt, 0) AS Forms,
                   COALESCE(ox.Cnt, 0) AS Extensions,
                   COALESCE(cx.Cnt, 0) AS Coc,
                   0 AS Labels
            FROM Models m
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM Tables GROUP BY ModelId) t ON t.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM Classes GROUP BY ModelId) c ON c.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM Edts GROUP BY ModelId) e ON e.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM Enums GROUP BY ModelId) en ON en.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM MenuItems GROUP BY ModelId) mi ON mi.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM Forms GROUP BY ModelId) f ON f.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM ObjectExtensions GROUP BY ModelId) ox ON ox.ModelId = m.ModelId
            LEFT JOIN (SELECT ModelId, COUNT(*) AS Cnt FROM CocExtensions GROUP BY ModelId) cx ON cx.ModelId = m.ModelId
            ORDER BY m.Name")
            .Select(r => new PerModelStat(
                (string)r.Model, r.IsCustom == 1L,
                (long)r.Tables, (long)r.Classes, (long)r.Edts,
                (long)r.Enums, (long)r.MenuItems, (long)r.Forms,
                (long)r.Extensions, (long)r.Coc, 0L)).ToList();

        var topTables = conn.Query(@"
            SELECT t.Name AS Name, m.Name AS Model, COUNT(f.FieldId) AS FieldCount
            FROM Tables t
            JOIN Models m ON m.ModelId = t.ModelId
            LEFT JOIN TableFields f ON f.TableId = t.TableId
            GROUP BY t.TableId, t.Name, m.Name
            ORDER BY FieldCount DESC, t.Name
            LIMIT @topN", new { topN })
            .Select(r => new TopTableStat((string)r.Name, (string)r.Model, (long)r.FieldCount)).ToList();

        var topClasses = conn.Query(@"
            SELECT c.Name AS Name, m.Name AS Model, COUNT(mt.MethodId) AS MethodCount
            FROM Classes c
            JOIN Models m ON m.ModelId = c.ModelId
            LEFT JOIN Methods mt ON mt.ClassId = c.ClassId
            GROUP BY c.ClassId, c.Name, m.Name
            ORDER BY MethodCount DESC, c.Name
            LIMIT @topN", new { topN })
            .Select(r => new TopClassStat((string)r.Name, (string)r.Model, (long)r.MethodCount)).ToList();

        var topCoc = conn.Query(@"
            SELECT c.TargetClass AS Target, COUNT(*) AS ExtensionCount
            FROM CocExtensions c
            GROUP BY c.TargetClass
            ORDER BY ExtensionCount DESC, c.TargetClass
            LIMIT @topN", new { topN })
            .Select(r => new TopCocStat((string)r.Target, (long)r.ExtensionCount)).ToList();

        return new IndexStats(perModel, topTables, topClasses, topCoc);
    }

    /// <summary>
    /// How much WAL we tolerate between checkpoints during a bulk extract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A checkpoint copies the WAL back into the database file and fsyncs it, and it was run
    /// after every model. That charged even a trivial model the full latency — a model with 76
    /// classes and no forms spent 982 ms of its 1.2 s write on the checkpoint — and it wrote a
    /// page once per model that dirtied it instead of once per run. Over a ten-model sample it
    /// was 60 s of an 83 s extract; batching brought that to 26 s and the run to 60 s.
    /// </para>
    /// <para>
    /// Bounding the WAL is the only thing the per-model checkpoint was there for, so a size
    /// threshold does the same job. The file peaks at the threshold plus one model's
    /// transaction (88 MB on that sample) — the same worst case the old code had while a large
    /// model was still uncommitted.
    /// </para>
    /// </remarks>
    private const long WalCheckpointThresholdBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Checkpoint the WAL back into the database, but only once it has grown past
    /// <see cref="WalCheckpointThresholdBytes"/>. Call <see cref="CheckpointWal"/> at the end
    /// of a bulk run to flush whatever is left.
    /// </summary>
    private void MaybeCheckpointWal(SqliteConnection conn)
    {
        // Reading the file length does not touch the database, so a below-threshold model
        // pays a stat() rather than a checkpoint.
        var wal = new FileInfo(_databasePath + "-wal");
        if (!wal.Exists || wal.Length < WalCheckpointThresholdBytes) return;
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    /// <summary>
    /// Flush the WAL back into the database file and truncate it. Bulk callers
    /// (<c>index extract</c> / <c>index refresh</c>) run this once when they are done, so the
    /// database on disk is complete and the WAL does not outlive the run.
    /// </summary>
    public void CheckpointWal()
    {
        using var conn = Open();
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
    }

    internal SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // Per-connection pragmas: foreign_keys is a per-connection setting,
        // journal_mode WAL survives across connections but is cheap to re-assert.
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA cache_size = -65536; PRAGMA wal_autocheckpoint = 0;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Escapes SQLite LIKE wildcard characters (<c>%</c>, <c>_</c>, and the
    /// chosen escape character <c>!</c>) in a user-supplied query fragment so
    /// that they are treated as literals.
    /// Use together with <c>ESCAPE '!'</c> in the SQL statement.
    /// </summary>
    private static string EscapeLike(string value) =>
        value.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");

    /// <summary>
    /// Clamps a caller-supplied row limit into a sane range. SQLite treats a
    /// NEGATIVE <c>LIMIT</c> as "no limit at all", so passing <c>--limit -1</c>
    /// through unchecked turns a bounded lookup into a full-index dump — the
    /// exact token blow-up the result caps exist to prevent. Zero and negative
    /// values fall back to <paramref name="fallback"/>; anything above
    /// <see cref="MaxRowLimit"/> is capped.
    /// </summary>
    internal static int ClampLimit(int limit, int fallback = 50) =>
        limit <= 0 ? fallback : Math.Min(limit, MaxRowLimit);

    /// <summary>Hard ceiling on rows any single query may return.</summary>
    public const int MaxRowLimit = 1000;

    /// <summary>
    /// Opens a read-only connection to the index database. Used by all pure
    /// query methods to avoid acquiring a write-capable lock unnecessarily.
    /// The database must already exist — callers reach this only after
    /// <see cref="EnsureSchema"/> has created it.
    /// </summary>
    private SqliteConnection OpenReadOnly()
    {
        var conn = new SqliteConnection(_readOnlyConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON;";
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

    // ---- Index maintenance ----

    /// <summary>
    /// Runs <c>PRAGMA wal_checkpoint(TRUNCATE)</c>, <c>VACUUM</c>, and
    /// <c>ANALYZE</c> on the index database. VACUUM reclaims space freed by
    /// DELETE-heavy ApplyExtract cycles; ANALYZE refreshes query-planner
    /// statistics so FTS5 and multi-table JOINs stay fast.
    /// Returns the file sizes before and after VACUUM and elapsed time.
    /// </summary>
    public OptimizeResult Optimize()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long sizeBefore = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
        using var conn = Open();
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        conn.Execute("VACUUM");
        conn.Execute("ANALYZE");
        sw.Stop();
        long sizeAfter = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
        return new OptimizeResult(sizeBefore, sizeAfter, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Checkpoints the WAL and writes a clean defragmented copy of the index
    /// to <paramref name="destinationPath"/> using SQLite's
    /// <c>VACUUM INTO</c> command (requires SQLite 3.27+; bundled version is
    /// sufficient). The source DB is not modified or locked beyond the normal
    /// checkpoint.
    /// </summary>
    public void VacuumInto(string destinationPath)
    {
        using var conn = Open();
        conn.Execute("PRAGMA wal_checkpoint(TRUNCATE)");
        conn.Execute($"VACUUM INTO '{destinationPath.Replace("'", "''")}'");
    }

    /// <summary>
    /// Pre-warms the SQLite page cache by issuing a <c>SELECT count(*)</c> on
    /// each major table. Counts are cheap (B-tree root page only) but force
    /// the OS page cache to load the most-accessed index pages so subsequent
    /// queries complete without cold-start I/O latency.
    /// </summary>
    public WarmupResult Warmup()
    {
        using var conn = OpenReadOnly();
        return new WarmupResult(
            Tables:    conn.ExecuteScalar<long>("SELECT count(*) FROM Tables"),
            Classes:   conn.ExecuteScalar<long>("SELECT count(*) FROM Classes"),
            Methods:   conn.ExecuteScalar<long>("SELECT count(*) FROM Methods"),
            Edts:      conn.ExecuteScalar<long>("SELECT count(*) FROM Edts"),
            Enums:     conn.ExecuteScalar<long>("SELECT count(*) FROM Enums"),
            Labels:    conn.ExecuteScalar<long>("SELECT count(*) FROM Labels"),
            Forms:     conn.ExecuteScalar<long>("SELECT count(*) FROM Forms"),
            CocExtensions: conn.ExecuteScalar<long>("SELECT count(*) FROM CocExtensions"),
            DataEntities: conn.ExecuteScalar<long>("SELECT count(*) FROM DataEntities"));
    }
}
