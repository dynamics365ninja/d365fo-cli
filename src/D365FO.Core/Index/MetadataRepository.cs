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
    public const int CurrentSchemaVersion = 2;

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
    /// </summary>
    public void EnsureSchema()
    {
        using var conn = Open();
        var current = conn.ExecuteScalar<long>("PRAGMA user_version");
        if (current == CurrentSchemaVersion) return;

        conn.Execute(SchemaSql.Value);
        conn.Execute($"PRAGMA user_version = {CurrentSchemaVersion}");
        conn.Execute(
            "INSERT OR IGNORE INTO SchemaVersion(Version, AppliedUtc) VALUES(@v, @t)",
            new { v = CurrentSchemaVersion, t = DateTime.UtcNow.ToString("O") });
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

        return new TableDetails(table, fields, relations);
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
        var sql = @"
            SELECT LabelFile AS File, Language, Key, Value
            FROM Labels
            WHERE (Value LIKE @like OR Key LIKE @like)
              AND (@langs IS NULL OR Language IN @langs)
            ORDER BY LabelFile, Key
            LIMIT @limit";
        return conn.Query<LabelMatch>(sql, new { like, langs = languages, limit }).ToList();
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
            WHERE LabelFile = @file AND Language = @lang AND Key = @key
            LIMIT 1", new { file, lang = language, key });
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
            ORDER BY Name
            LIMIT @limit", new { like, limit });
        return rows.Select(r => (r.Kind, r.Name, r.Model)).ToList();
    }

    // ---- writer API used by the extract pipeline ----

    public long UpsertModel(string name, string? publisher, string? layer, bool isCustom)
    {
        using var conn = Open();
        return UpsertModelInternal(conn, null, name, publisher, layer, isCustom);
    }

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
    public void ApplyExtract(ExtractBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        var modelId = UpsertModelInternal(conn, tx, batch.Model, batch.Publisher, batch.Layer, batch.IsCustom);

        conn.Execute("DELETE FROM EnumValues WHERE EnumId IN (SELECT EnumId FROM Enums WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Enums WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM TableFields WHERE TableId IN (SELECT TableId FROM Tables WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Tables WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Methods WHERE ClassId IN (SELECT ClassId FROM Classes WHERE ModelId=@m)", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Classes WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM Edts WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM MenuItems WHERE ModelId=@m", new { m = modelId }, tx);
        conn.Execute("DELETE FROM CocExtensions WHERE ModelId=@m", new { m = modelId }, tx);
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
            Coc: conn.ExecuteScalar<long>("SELECT COUNT(*) FROM CocExtensions"));
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
