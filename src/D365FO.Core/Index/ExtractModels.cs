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
}
public sealed record ExtractedTableField(string Name, string? Type, string? EdtName, string? Label, bool Mandatory);
public sealed record ExtractedTableRelation(string? Name, string RelatedTable, string? Cardinality, string? RelationshipType);
public sealed record ExtractedTableIndex(string Name, bool AllowDuplicates, bool AlternateKey, IReadOnlyList<string> Fields);
public sealed record ExtractedTableDeleteAction(string? Name, string RelatedTable, string? DeleteAction);

public sealed record ExtractedClass(string Name, string? Extends, bool IsAbstract, bool IsFinal, string? SourcePath, IReadOnlyList<ExtractedMethod> Methods, string? Declaration = null)
{
    public IReadOnlyList<ExtractedClassAttribute> Attributes { get; init; } = Array.Empty<ExtractedClassAttribute>();
}
public sealed record ExtractedMethod(string Name, string? Signature, string? ReturnType, bool IsStatic);
public sealed record ExtractedClassAttribute(string? MethodName, string AttributeName, string RawArgs);
public sealed record ExtractedEventSubscriber(
    string SubscriberClass,
    string SubscriberMethod,
    string SourceKind,
    string SourceObject,
    string? SourceMember,
    string? EventType);

public sealed record ExtractedEdt(string Name, string? Extends, string? BaseType, string? Label, int? StringSize);
public sealed record ExtractedEnum(string Name, string? Label, IReadOnlyList<ExtractedEnumValue> Values);
public sealed record ExtractedEnumValue(string Name, int? Value, string? Label);
public sealed record ExtractedMenuItem(string Name, string Kind, string? Object, string? ObjectType, string? Label);
public sealed record ExtractedCoc(string TargetClass, string TargetMethod, string ExtensionClass);
public sealed record ExtractedLabel(string File, string Language, string Key, string? Value);

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

public sealed record ExtractedObjectExtension(string Kind, string TargetName, string ExtensionName, string? SourcePath);

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
