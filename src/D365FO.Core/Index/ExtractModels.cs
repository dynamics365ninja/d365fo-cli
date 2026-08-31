namespace D365FO.Core.Index;

/// <summary>
/// In-memory bundle of one model's extracted metadata. Produced by the
/// <see cref="Extract.MetadataExtractor"/> and consumed by
/// <see cref="MetadataRepository.ApplyExtract(ExtractBatch)"/>.
/// </summary>
public sealed record ExtractBatch(
    string Model,
    string? Publisher,
    string? Layer,
    bool IsCustom,
    IReadOnlyList<ExtractedTable> Tables,
    IReadOnlyList<ExtractedClass> Classes,
    IReadOnlyList<ExtractedEdt> Edts,
    IReadOnlyList<ExtractedEnum> Enums,
    IReadOnlyList<ExtractedMenuItem> MenuItems,
    IReadOnlyList<ExtractedCoc> CocExtensions,
    IReadOnlyList<ExtractedLabel> Labels)
{
    public IReadOnlyList<ExtractedForm> Forms { get; init; } = Array.Empty<ExtractedForm>();
    public IReadOnlyList<ExtractedObjectExtension> Extensions { get; init; } = Array.Empty<ExtractedObjectExtension>();
    public IReadOnlyList<ExtractedEventSubscriber> EventSubscribers { get; init; } = Array.Empty<ExtractedEventSubscriber>();
    public IReadOnlyList<ExtractedSecurityRole> Roles { get; init; } = Array.Empty<ExtractedSecurityRole>();
    public IReadOnlyList<ExtractedSecurityDuty> Duties { get; init; } = Array.Empty<ExtractedSecurityDuty>();
    public IReadOnlyList<ExtractedSecurityPrivilege> Privileges { get; init; } = Array.Empty<ExtractedSecurityPrivilege>();
    public IReadOnlyList<ExtractedQuery> Queries { get; init; } = Array.Empty<ExtractedQuery>();
    public IReadOnlyList<ExtractedView> Views { get; init; } = Array.Empty<ExtractedView>();
    public IReadOnlyList<ExtractedDataEntity> DataEntities { get; init; } = Array.Empty<ExtractedDataEntity>();
    public IReadOnlyList<ExtractedReport> Reports { get; init; } = Array.Empty<ExtractedReport>();
    public IReadOnlyList<ExtractedService> Services { get; init; } = Array.Empty<ExtractedService>();
    public IReadOnlyList<ExtractedServiceGroup> ServiceGroups { get; init; } = Array.Empty<ExtractedServiceGroup>();
    public IReadOnlyList<ExtractedWorkflowType> WorkflowTypes { get; init; } = Array.Empty<ExtractedWorkflowType>();
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
    /// <summary>AxMap objects found in the model's AxMap folder.</summary>
    public IReadOnlyList<ExtractedMap> Maps { get; init; } = Array.Empty<ExtractedMap>();
    // v17: where each indexed .label.txt physically lives (per file+language),
    // so label search/resolve can confirm a hit against the disk.
    public IReadOnlyList<ExtractedLabelFile> LabelFiles { get; init; } = Array.Empty<ExtractedLabelFile>();

    // v11 additions
    public IReadOnlyList<ExtractedBusinessEvent>    BusinessEvents    { get; init; } = Array.Empty<ExtractedBusinessEvent>();
    public IReadOnlyList<ExtractedSecurityPolicy>   SecurityPolicies  { get; init; } = Array.Empty<ExtractedSecurityPolicy>();
    public IReadOnlyList<ExtractedConfigurationKey> ConfigurationKeys { get; init; } = Array.Empty<ExtractedConfigurationKey>();
    public IReadOnlyList<ExtractedTile>             Tiles             { get; init; } = Array.Empty<ExtractedTile>();
    public IReadOnlyList<ExtractedWorkspace>        Workspaces        { get; init; } = Array.Empty<ExtractedWorkspace>();

    public static ExtractBatch Empty(string model) => new(
        model, null, null, false,
        Array.Empty<ExtractedTable>(),
        Array.Empty<ExtractedClass>(),
        Array.Empty<ExtractedEdt>(),
        Array.Empty<ExtractedEnum>(),
        Array.Empty<ExtractedMenuItem>(),
        Array.Empty<ExtractedCoc>(),
        Array.Empty<ExtractedLabel>());
}

public sealed record ExtractedTable(string Name, string? Label, string? SourcePath, IReadOnlyList<ExtractedTableField> Fields)
{
    public IReadOnlyList<ExtractedTableRelation> Relations { get; init; } = Array.Empty<ExtractedTableRelation>();
    public IReadOnlyList<ExtractedTableIndex> Indexes { get; init; } = Array.Empty<ExtractedTableIndex>();
    public IReadOnlyList<ExtractedTableDeleteAction> DeleteActions { get; init; } = Array.Empty<ExtractedTableDeleteAction>();
    public IReadOnlyList<ExtractedMethod> Methods { get; init; } = Array.Empty<ExtractedMethod>();
    // v11 gap-fill properties
    public string? SaveDataPerCompany { get; init; }
    public string? CacheLookup { get; init; }
    public bool OccEnabled { get; init; }
    public string? ValidTimeStateFieldType { get; init; }
    public string? TableExtends { get; init; }
    public string? AOSAuthorization { get; init; }
    public string? FormRef { get; init; }
    public string? ListPageRef { get; init; }
    public bool SystemTable { get; init; }
    // v14 property-stats inputs (mined, not persisted on Tables)
    public string? TableGroup { get; init; }
    public string? ClusteredIndex { get; init; }
}
public sealed record ExtractedTableField(string Name, string? Type, string? EdtName, string? Label, bool Mandatory);
public sealed record ExtractedTableRelation(string? Name, string RelatedTable, string? Cardinality, string? RelationshipType);
public sealed record ExtractedTableIndex(string Name, bool AllowDuplicates, bool AlternateKey, IReadOnlyList<string> Fields);
public sealed record ExtractedTableDeleteAction(string? Name, string RelatedTable, string? DeleteAction);

public sealed record ExtractedClass(string Name, string? Extends, bool IsAbstract, bool IsFinal, string? SourcePath, IReadOnlyList<ExtractedMethod> Methods, string? Declaration = null)
{
    public IReadOnlyList<ExtractedClassAttribute> Attributes { get; init; } = Array.Empty<ExtractedClassAttribute>();
    // v12 class-level lint flags
    public bool IsRunBaseBatch { get; init; }
    public bool HasCanGoBatch { get; init; }
    public bool HasPublicInstanceFields { get; init; }
}
public sealed record ExtractedMethod(string Name, string? Signature, string? ReturnType, bool IsStatic,
    bool HasDocComment = false, bool HasTodayCall = false, bool HasDoInsertOrUpdate = false,
    // v12 method-level lint flags
    bool HasInsertInLoop = false, bool HasNestedSelect = false, bool HasForceLiterals = false,
    bool HasForUpdateWithoutUpdate = false, bool HasTryCatchInTts = false, bool HasEmptyLoop = false,
    // v12 table-method-only flag
    bool IsEmptyOverride = false)
{
    /// <summary>
    /// v15: Raw X++ source of the method body. Transient — only populated when
    /// the extractor runs with source capture enabled (<c>index extract
    /// --index-source</c>) and consumed by <c>ApplyExtract</c> to feed the
    /// <c>MethodSourceFts</c> index. Never persisted as a column; null otherwise.
    /// </summary>
    public string? Body { get; init; }
}
public sealed record ExtractedClassAttribute(string? MethodName, string AttributeName, string RawArgs);
public sealed record ExtractedEventSubscriber(
    string SubscriberClass,
    string SubscriberMethod,
    string SourceKind,
    string SourceObject,
    string? SourceMember,
    string? EventType);

public sealed record ExtractedEdt(string Name, string? Extends, string? BaseType, string? Label, int? StringSize)
{
    // v11 gap-fill properties
    public string? ReferenceTable { get; init; }
    public string? FormHelp { get; init; }
    public string? AnalysisUsage { get; init; }
    public string? EnumType { get; init; }
}
public sealed record ExtractedEnum(string Name, string? Label, IReadOnlyList<ExtractedEnumValue> Values);
public sealed record ExtractedEnumValue(string Name, int? Value, string? Label);
public sealed record ExtractedMenuItem(string Name, string Kind, string? Object, string? ObjectType, string? Label);
public sealed record ExtractedCoc(string TargetClass, string TargetMethod, string ExtensionClass);
public sealed record ExtractedLabel(string File, string Language, string Key, string? Value);

/// <summary>One physical <c>.label.txt</c> the extractor read: its logical file id, language, and disk path.</summary>
public sealed record ExtractedLabelFile(string File, string Language, string SourcePath);

/// <summary>A D365FO AxMap object — a shared field template mapped onto multiple tables.</summary>
public sealed record ExtractedMap(string Name, string? Label, string? SourcePath, IReadOnlyList<ExtractedMapField> Fields)
{
    /// <summary>Names of the tables that implement this map.</summary>
    public IReadOnlyList<string> MappedTables { get; init; } = Array.Empty<string>();
}
/// <summary>A field on an <see cref="ExtractedMap"/>.</summary>
public sealed record ExtractedMapField(string Name, string? Type, string? EdtName, string? Label);

public sealed record ExtractedForm(string Name, string? SourcePath, IReadOnlyList<ExtractedFormDataSource> DataSources)
{
    /// <summary>v8: <c>&lt;Design&gt;&lt;Pattern&gt;</c> — null when the form has no Microsoft pattern applied.</summary>
    public string? Pattern { get; init; }
    public string? PatternVersion { get; init; }
    public string? Style { get; init; }
    public string? TitleDataSource { get; init; }
}
public sealed record ExtractedFormDataSource(string Name, string? Table)
{
    public string? JoinSource { get; init; }
}

public sealed record ExtractedObjectExtension(string Kind, string TargetName, string ExtensionName, string? SourcePath)
{
    /// <summary>Fields added by an AxTableExtension (empty for other kinds).</summary>
    public IReadOnlyList<ExtractedTableField> AddedFields { get; init; } = Array.Empty<ExtractedTableField>();
}

public sealed record ExtractedSecurityRole(
    string Name,
    string? Label,
    IReadOnlyList<string> Duties,
    IReadOnlyList<string> Privileges);

public sealed record ExtractedSecurityDuty(
    string Name,
    string? Label,
    IReadOnlyList<string> Privileges);

public sealed record ExtractedSecurityPrivilege(
    string Name,
    string? Label,
    IReadOnlyList<ExtractedSecurityEntryPoint> EntryPoints);

public sealed record ExtractedSecurityEntryPoint(
    string ObjectName,
    string? ObjectType,
    string? ObjectChild,
    string? AccessLevel);

public sealed record ExtractedQuery(string Name, string? SourcePath, IReadOnlyList<ExtractedQueryDataSource> DataSources);
public sealed record ExtractedQueryDataSource(string Name, string? Table, string? JoinMode, string? ParentDs);

public sealed record ExtractedView(string Name, string? Label, string? QueryName, string? SourcePath, IReadOnlyList<ExtractedViewField> Fields);
public sealed record ExtractedViewField(string Name, string? DataSource, string? DataField);

public sealed record ExtractedDataEntity(
    string Name,
    string? PublicEntityName,
    string? PublicCollectionName,
    string? StagingTable,
    string? QueryName,
    string? Label,
    string? SourcePath,
    IReadOnlyList<ExtractedDataEntityField> Fields);
public sealed record ExtractedDataEntityField(string Name, string? DataSource, string? DataField, bool IsMandatory, bool IsReadOnly);

public sealed record ExtractedReport(string Name, string Kind, string? SourcePath, IReadOnlyList<ExtractedReportDataSet> DataSets);
public sealed record ExtractedReportDataSet(string Name, string? Kind, string? QueryOrClass);

public sealed record ExtractedService(string Name, string? Class, string? SourcePath, IReadOnlyList<ExtractedServiceOperation> Operations);
public sealed record ExtractedServiceOperation(string OperationName, string? MethodName);
public sealed record ExtractedServiceGroup(string Name, string? SourcePath, IReadOnlyList<string> Members);

public sealed record ExtractedWorkflowType(string Name, string? Category, string? DocumentClass, string? SourcePath);

// ---- v11 extract records ------------------------------------------------

/// <summary>A D365FO Business Event (class extending BusinessEventsBase).</summary>
public sealed record ExtractedBusinessEvent(
    string Name,
    string? Category,
    string? ContractClass,
    string? SourcePath);

/// <summary>An AxSecurityPolicy (XDS row-level security policy).</summary>
public sealed record ExtractedSecurityPolicy(
    string Name,
    string? ConstrainedTable,
    string? PolicyQuery,
    string? OperationType,
    string? ContextType,
    bool IsEnabled,
    bool IsMandatory,
    string? SourcePath);

/// <summary>An AxConfigurationKey gating feature availability.</summary>
public sealed record ExtractedConfigurationKey(
    string Name,
    string? Label,
    bool IsEnabled,
    string? ParentKey,
    string? LicenseCode);

/// <summary>An AxTile used in navigation tile panels.</summary>
public sealed record ExtractedTile(
    string Name,
    string? MenuItemName,
    string? MenuItemType,
    string? Label,
    string? TileType,
    string? SourcePath);

/// <summary>An AxWorkspace navigation descriptor.</summary>
public sealed record ExtractedWorkspace(
    string Name,
    string? Label,
    string? SourcePath);

public sealed record ExtractCounts(
    long Models,
    long Tables,
    long Fields,
    long Classes,
    long Methods,
    long Edts,
    long Enums,
    long MenuItems,
    long Labels,
    long Coc)
{
    public long Forms { get; init; }
    public long Extensions { get; init; }
    public long EventSubscribers { get; init; }
    public long Relations { get; init; }
    public long Roles { get; init; }
    public long Duties { get; init; }
    public long Privileges { get; init; }
    public long Queries { get; init; }
    public long Views { get; init; }
    public long DataEntities { get; init; }
    public long Reports { get; init; }
    public long Services { get; init; }
    public long WorkflowTypes { get; init; }
}
