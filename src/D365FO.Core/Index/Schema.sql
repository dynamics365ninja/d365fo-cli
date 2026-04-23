-- D365FO metadata index schema (v1)
-- Mirrors SQLite layout used by the upstream MCP server so both
-- D365FO.Cli and D365FO.Mcp can read the same artifact.

PRAGMA foreign_keys = ON;
PRAGMA journal_mode = WAL;

CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version     INTEGER PRIMARY KEY,
    AppliedUtc  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Models (
    ModelId     INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL UNIQUE,
    Publisher   TEXT,
    Layer       TEXT,
    IsCustom    INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS Tables (
    TableId     INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    Label       TEXT,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Tables_Name ON Tables(Name);

CREATE TABLE IF NOT EXISTS TableFields (
    FieldId     INTEGER PRIMARY KEY AUTOINCREMENT,
    TableId     INTEGER NOT NULL,
    Name        TEXT NOT NULL,
    Type        TEXT,
    EdtName     TEXT,
    Label       TEXT,
    Mandatory   INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (TableId) REFERENCES Tables(TableId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_TableFields_TableId ON TableFields(TableId);

CREATE TABLE IF NOT EXISTS Classes (
    ClassId     INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    ExtendsName TEXT,
    IsAbstract  INTEGER NOT NULL DEFAULT 0,
    IsFinal     INTEGER NOT NULL DEFAULT 0,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Classes_Name ON Classes(Name);

CREATE TABLE IF NOT EXISTS Methods (
    MethodId        INTEGER PRIMARY KEY AUTOINCREMENT,
    ClassId         INTEGER NOT NULL,
    Name            TEXT NOT NULL,
    Signature       TEXT,
    IsStatic        INTEGER NOT NULL DEFAULT 0,
    ReturnType      TEXT,
    FOREIGN KEY (ClassId) REFERENCES Classes(ClassId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_Methods_ClassId_Name ON Methods(ClassId, Name);

CREATE TABLE IF NOT EXISTS CocExtensions (
    CocId           INTEGER PRIMARY KEY AUTOINCREMENT,
    TargetClass     TEXT NOT NULL,
    TargetMethod    TEXT NOT NULL,
    ExtensionClass  TEXT NOT NULL,
    ModelId         INTEGER NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Coc_Target ON CocExtensions(TargetClass, TargetMethod);

CREATE TABLE IF NOT EXISTS Edts (
    EdtId       INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    ExtendsName TEXT,
    BaseType    TEXT,
    Label       TEXT,
    StringSize  INTEGER,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Edts_Name ON Edts(Name);

CREATE TABLE IF NOT EXISTS Labels (
    LabelId     INTEGER PRIMARY KEY AUTOINCREMENT,
    LabelFile   TEXT NOT NULL,
    Language    TEXT NOT NULL,
    Key         TEXT NOT NULL,
    Value       TEXT
);
CREATE INDEX IF NOT EXISTS IX_Labels_Key ON Labels(LabelFile, Language, Key);
CREATE INDEX IF NOT EXISTS IX_Labels_Value ON Labels(Value);

CREATE TABLE IF NOT EXISTS MenuItems (
    MenuItemId  INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Kind        TEXT NOT NULL,       -- Display/Action/Output
    Object      TEXT,
    ObjectType  TEXT,                -- Form/Class/Report/Job
    Label       TEXT,
    ModelId     INTEGER NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_MenuItems_Name ON MenuItems(Name);

CREATE TABLE IF NOT EXISTS Relations (
    RelationId      INTEGER PRIMARY KEY AUTOINCREMENT,
    FromTable       TEXT NOT NULL,
    ToTable         TEXT NOT NULL,
    Cardinality     TEXT,
    RelationName    TEXT
);
CREATE INDEX IF NOT EXISTS IX_Relations_From ON Relations(FromTable);
CREATE INDEX IF NOT EXISTS IX_Relations_To   ON Relations(ToTable);

CREATE TABLE IF NOT EXISTS SecurityRoles (
    RoleId      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Label       TEXT,
    ModelId     INTEGER NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE TABLE IF NOT EXISTS SecurityMap (
    MapId       INTEGER PRIMARY KEY AUTOINCREMENT,
    Role        TEXT NOT NULL,
    Duty        TEXT,
    Privilege   TEXT,
    EntryPoint  TEXT,
    ObjectName  TEXT,
    ObjectType  TEXT
);
CREATE INDEX IF NOT EXISTS IX_Sec_Object ON SecurityMap(ObjectName, ObjectType);
