-- D365FO metadata index schema (v1)
-- Mirrors SQLite layout used by the upstream MCP server so both
-- D365FO.Cli and D365FO.Mcp can read the same artifact.
--
-- NOTE: per-connection PRAGMAs (foreign_keys, journal_mode) are applied by
-- MetadataRepository.Open() at connection acquisition time, not here. Mixing
-- them into the schema script causes issues when the script is run inside a
-- transaction (journal_mode is ignored silently inside transactions).

CREATE TABLE IF NOT EXISTS SchemaVersion (
    Version     INTEGER PRIMARY KEY,
    AppliedUtc  TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS Models (
    ModelId             INTEGER PRIMARY KEY AUTOINCREMENT,
    Name                TEXT NOT NULL UNIQUE,
    Publisher           TEXT,
    Layer               TEXT,
    IsCustom            INTEGER NOT NULL DEFAULT 0,
    LastExtractedUtc    TEXT,
    SourceFingerprint   TEXT
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

CREATE TABLE IF NOT EXISTS Enums (
    EnumId      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    Label       TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Enums_Name ON Enums(Name);

CREATE TABLE IF NOT EXISTS EnumValues (
    EnumValueId INTEGER PRIMARY KEY AUTOINCREMENT,
    EnumId      INTEGER NOT NULL,
    Name        TEXT NOT NULL,
    Value       INTEGER,
    Label       TEXT,
    FOREIGN KEY (EnumId) REFERENCES Enums(EnumId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_EnumValues_EnumId ON EnumValues(EnumId);

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

-- v3 additions -----------------------------------------------------------

CREATE TABLE IF NOT EXISTS TableMethods (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TableId         INTEGER NOT NULL,
    Name            TEXT NOT NULL,
    Signature       TEXT,
    IsStatic        INTEGER NOT NULL DEFAULT 0,
    ReturnType      TEXT,
    FOREIGN KEY (TableId) REFERENCES Tables(TableId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_TableMethods_TableId ON TableMethods(TableId, Name);

CREATE TABLE IF NOT EXISTS TableIndexes (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TableId         INTEGER NOT NULL,
    Name            TEXT NOT NULL,
    AllowDuplicates INTEGER NOT NULL DEFAULT 1,
    AlternateKey    INTEGER NOT NULL DEFAULT 0,
    FieldsCsv       TEXT,
    FOREIGN KEY (TableId) REFERENCES Tables(TableId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS TableDeleteActions (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    TableId         INTEGER NOT NULL,
    Name            TEXT,
    RelatedTable    TEXT NOT NULL,
    DeleteAction    TEXT,
    FOREIGN KEY (TableId) REFERENCES Tables(TableId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Forms (
    FormId      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Forms_Name ON Forms(Name);

CREATE TABLE IF NOT EXISTS FormDataSources (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    FormId      INTEGER NOT NULL,
    Name        TEXT NOT NULL,
    TableName   TEXT,
    FOREIGN KEY (FormId) REFERENCES Forms(FormId) ON DELETE CASCADE
);

-- Generic object extensions (TableExtension / FormExtension / EdtExtension /
-- EnumExtension / ViewExtension / MapExtension). Lets callers query
-- "which extensions touch CustTable?" regardless of artifact kind.
CREATE TABLE IF NOT EXISTS ObjectExtensions (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Kind            TEXT NOT NULL,    -- Table/Form/Edt/Enum/View/Map
    TargetName      TEXT NOT NULL,
    ExtensionName   TEXT NOT NULL,
    ModelId         INTEGER NOT NULL,
    SourcePath      TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_ObjExt_Target ON ObjectExtensions(Kind, TargetName);

CREATE TABLE IF NOT EXISTS ClassAttributes (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ClassId         INTEGER NOT NULL,
    MethodName      TEXT,         -- NULL = on the class itself
    AttributeName   TEXT NOT NULL,
    RawArgs         TEXT,
    FOREIGN KEY (ClassId) REFERENCES Classes(ClassId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_ClsAttr_Name ON ClassAttributes(AttributeName);

CREATE TABLE IF NOT EXISTS EventSubscribers (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    SubscriberClass     TEXT NOT NULL,
    SubscriberMethod    TEXT NOT NULL,
    SourceKind          TEXT NOT NULL,    -- Form/FormDataSource/FormControl/Table/DataEvent/Delegate
    SourceObject        TEXT NOT NULL,    -- form/table/class name
    SourceMember        TEXT,             -- datasource / control / delegate method
    EventType           TEXT,             -- Initialized / Clicked / Inserting / ...
    ModelId             INTEGER NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_EvtSub_Source ON EventSubscribers(SourceKind, SourceObject);

CREATE TABLE IF NOT EXISTS SecurityDuties (
    DutyId      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Label       TEXT,
    ModelId     INTEGER NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_SecDuty_Name ON SecurityDuties(Name);

CREATE TABLE IF NOT EXISTS SecurityPrivileges (
    PrivilegeId INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Label       TEXT,
    ModelId     INTEGER NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_SecPriv_Name ON SecurityPrivileges(Name);

CREATE TABLE IF NOT EXISTS SecurityRoleDuties (
    Role    TEXT NOT NULL,
    Duty    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SecRoleDuty ON SecurityRoleDuties(Role);

CREATE TABLE IF NOT EXISTS SecurityDutyPrivileges (
    Duty        TEXT NOT NULL,
    Privilege   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SecDutyPriv ON SecurityDutyPrivileges(Duty);

CREATE TABLE IF NOT EXISTS SecurityRolePrivileges (
    Role        TEXT NOT NULL,
    Privilege   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SecRolePriv ON SecurityRolePrivileges(Role);

CREATE TABLE IF NOT EXISTS SecurityPrivilegeEntryPoints (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Privilege       TEXT NOT NULL,
    ObjectName      TEXT NOT NULL,
    ObjectType      TEXT,
    ObjectChild     TEXT,
    AccessLevel     TEXT
);
CREATE INDEX IF NOT EXISTS IX_SecPrivEP_Priv ON SecurityPrivilegeEntryPoints(Privilege);
CREATE INDEX IF NOT EXISTS IX_SecPrivEP_Obj  ON SecurityPrivilegeEntryPoints(ObjectName, ObjectType);

-- v4 additions -----------------------------------------------------------

CREATE TABLE IF NOT EXISTS Queries (
    QueryId     INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Queries_Name ON Queries(Name);

CREATE TABLE IF NOT EXISTS QueryDataSources (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    QueryId     INTEGER NOT NULL,
    Name        TEXT NOT NULL,
    TableName   TEXT,
    JoinMode    TEXT,
    ParentDs    TEXT,
    FOREIGN KEY (QueryId) REFERENCES Queries(QueryId) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_QueryDs_QueryId ON QueryDataSources(QueryId);

CREATE TABLE IF NOT EXISTS Views (
    ViewId      INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    Label       TEXT,
    QueryName   TEXT,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Views_Name ON Views(Name);

CREATE TABLE IF NOT EXISTS ViewFields (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ViewId      INTEGER NOT NULL,
    Name        TEXT NOT NULL,
    DataSource  TEXT,
    DataField   TEXT,
    FOREIGN KEY (ViewId) REFERENCES Views(ViewId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS DataEntities (
    EntityId        INTEGER PRIMARY KEY AUTOINCREMENT,
    Name            TEXT NOT NULL,
    ModelId         INTEGER NOT NULL,
    PublicEntityName TEXT,
    PublicCollectionName TEXT,
    StagingTable    TEXT,
    QueryName       TEXT,
    Label           TEXT,
    SourcePath      TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_DataEntities_Name ON DataEntities(Name);
CREATE INDEX IF NOT EXISTS IX_DataEntities_Public ON DataEntities(PublicEntityName);

CREATE TABLE IF NOT EXISTS DataEntityFields (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EntityId        INTEGER NOT NULL,
    Name            TEXT NOT NULL,
    DataSource      TEXT,
    DataField       TEXT,
    IsMandatory     INTEGER NOT NULL DEFAULT 0,
    IsReadOnly      INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (EntityId) REFERENCES DataEntities(EntityId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Reports (
    ReportId    INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Kind        TEXT,        -- Ssrs / Rdl / Legacy
    ModelId     INTEGER NOT NULL,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Reports_Name ON Reports(Name);

CREATE TABLE IF NOT EXISTS ReportDataSets (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ReportId    INTEGER NOT NULL,
    Name        TEXT NOT NULL,
    Kind        TEXT,        -- Query / ReportDataProvider / BusinessLogic
    QueryOrClass TEXT,
    FOREIGN KEY (ReportId) REFERENCES Reports(ReportId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS Services (
    ServiceId   INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Class       TEXT,
    ModelId     INTEGER NOT NULL,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_Services_Name ON Services(Name);

CREATE TABLE IF NOT EXISTS ServiceOperations (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ServiceId   INTEGER NOT NULL,
    OperationName TEXT NOT NULL,
    MethodName  TEXT,
    FOREIGN KEY (ServiceId) REFERENCES Services(ServiceId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS ServiceGroups (
    GroupId     INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    ModelId     INTEGER NOT NULL,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_ServiceGroups_Name ON ServiceGroups(Name);

CREATE TABLE IF NOT EXISTS ServiceGroupMembers (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    GroupId     INTEGER NOT NULL,
    ServiceName TEXT NOT NULL,
    FOREIGN KEY (GroupId) REFERENCES ServiceGroups(GroupId) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS WorkflowTypes (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    Name        TEXT NOT NULL,
    Category    TEXT,        -- Approval / Task / Hierarchy
    DocumentClass TEXT,
    ModelId     INTEGER NOT NULL,
    SourcePath  TEXT,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_WorkflowTypes_Name ON WorkflowTypes(Name);

-- Model-to-model references parsed from Descriptor/*.xml. One row per
-- referenced module; Target is the raw module name as it appears in the
-- descriptor (it may or may not match an indexed Models.Name, e.g. when the
-- reference is to a Microsoft package not present in this extraction).
CREATE TABLE IF NOT EXISTS ModelDependencies (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    ModelId     INTEGER NOT NULL,
    Target      TEXT NOT NULL,
    FOREIGN KEY (ModelId) REFERENCES Models(ModelId)
);
CREATE INDEX IF NOT EXISTS IX_ModelDependencies_Model ON ModelDependencies(ModelId);
CREATE INDEX IF NOT EXISTS IX_ModelDependencies_Target ON ModelDependencies(Target);

-- v6 additions -----------------------------------------------------------
-- Full-text search over labels. Content-linked virtual table + triggers keep
-- LabelFts in lock-step with Labels, so `d365fo search label "customer
-- invoice"` drops from a LIKE scan (hundreds of ms on a real workspace) to a
-- rank-sorted MATCH (tens of ms). After a schema upgrade the caller must run
-- `INSERT INTO LabelFts(LabelFts) VALUES('rebuild')` once to backfill from
-- existing Labels rows — EnsureSchema() takes care of that.

CREATE VIRTUAL TABLE IF NOT EXISTS LabelFts USING fts5(
    Value, Key, LabelFile, Language,
    content='Labels', content_rowid='LabelId',
    tokenize = 'unicode61 remove_diacritics 1'
);

CREATE TRIGGER IF NOT EXISTS Labels_ai AFTER INSERT ON Labels BEGIN
    INSERT INTO LabelFts(rowid, Value, Key, LabelFile, Language)
    VALUES (new.LabelId, new.Value, new.Key, new.LabelFile, new.Language);
END;

CREATE TRIGGER IF NOT EXISTS Labels_ad AFTER DELETE ON Labels BEGIN
    INSERT INTO LabelFts(LabelFts, rowid, Value, Key, LabelFile, Language)
    VALUES ('delete', old.LabelId, old.Value, old.Key, old.LabelFile, old.Language);
END;

CREATE TRIGGER IF NOT EXISTS Labels_au AFTER UPDATE ON Labels BEGIN
    INSERT INTO LabelFts(LabelFts, rowid, Value, Key, LabelFile, Language)
    VALUES ('delete', old.LabelId, old.Value, old.Key, old.LabelFile, old.Language);
    INSERT INTO LabelFts(rowid, Value, Key, LabelFile, Language)
    VALUES (new.LabelId, new.Value, new.Key, new.LabelFile, new.Language);
END;

-- v7 additions -----------------------------------------------------------
-- Per-model fingerprints for content-addressed incremental refresh and an
-- ExtractionRuns audit table for `d365fo stats extract-history`. The columns
-- on Models are also injected via ALTER TABLE in EnsureSchema() so pre-v7
-- databases pick them up without a full rebuild.

CREATE TABLE IF NOT EXISTS ExtractionRuns (
    RunId           INTEGER PRIMARY KEY AUTOINCREMENT,
    StartedUtc      TEXT NOT NULL,
    Model           TEXT NOT NULL,
    ElapsedMs       INTEGER NOT NULL,
    Tables          INTEGER NOT NULL DEFAULT 0,
    Classes         INTEGER NOT NULL DEFAULT 0,
    Edts            INTEGER NOT NULL DEFAULT 0,
    Enums           INTEGER NOT NULL DEFAULT 0,
    Labels          INTEGER NOT NULL DEFAULT 0,
    IsCustom        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_ExtractionRuns_Model ON ExtractionRuns(Model);
CREATE INDEX IF NOT EXISTS IX_ExtractionRuns_StartedUtc ON ExtractionRuns(StartedUtc);

