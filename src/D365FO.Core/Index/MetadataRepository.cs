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
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string ConnectionString => _connectionString;

    public void EnsureSchema()
    {
        using var conn = Open();
        var sql = LoadEmbeddedSchema();
        conn.Execute(sql);

        var current = conn.ExecuteScalar<long?>("SELECT MAX(Version) FROM SchemaVersion");
        if (current is null)
        {
            conn.Execute(
                "INSERT INTO SchemaVersion(Version, AppliedUtc) VALUES(@v, @t)",
                new { v = 1, t = DateTime.UtcNow.ToString("O") });
        }
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

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
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
