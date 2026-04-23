namespace D365FO.Core.Index;

/// <summary>Minimal record types returned by the metadata repository.</summary>
public sealed record ModelInfo(long ModelId, string Name, string? Publisher, string? Layer, bool IsCustom);

public sealed record ModelDependencies(ModelInfo Model, IReadOnlyList<string> DependsOn, IReadOnlyList<string> DependedBy);

public sealed record TableInfo(
    long TableId,
    string Name,
    string Model,
    string? Label,
    string? SourcePath);

public sealed record TableFieldInfo(
    string Name,
    string? Type,
    string? EdtName,
    string? Label,
    bool Mandatory);

public sealed record TableDetails(
    TableInfo Table,
    IReadOnlyList<TableFieldInfo> Fields,
    IReadOnlyList<RelationInfo> Relations,
    IReadOnlyList<TableMethodInfo> Methods,
    IReadOnlyList<TableIndexInfo> Indexes,
    IReadOnlyList<TableDeleteActionInfo> DeleteActions);

public sealed record ClassInfo(
    long ClassId,
    string Name,
    string Model,
    string? Extends,
    bool IsAbstract,
    bool IsFinal,
    string? SourcePath);

public sealed record MethodInfo(
    string Name,
    string? Signature,
    string? ReturnType,
    bool IsStatic);

public sealed record ClassDetails(ClassInfo Class, IReadOnlyList<MethodInfo> Methods);

public sealed record EdtInfo(
    string Name,
    string Model,
    string? Extends,
    string? BaseType,
    string? Label,
    int? StringSize);

public sealed record EnumInfo(string Name, string Model, string? Label);

public sealed record EnumValueInfo(string Name, long? Value, string? Label);

public sealed record EnumDetails(EnumInfo Enum, IReadOnlyList<EnumValueInfo> Values);

public sealed record LabelMatch(string File, string Language, string Key, string? Value);

public sealed record MenuItemInfo(
    string Name,
    string Kind,
    string? Object,
    string? ObjectType,
    string? Label,
    string Model);

public sealed record RelationInfo(string FromTable, string ToTable, string? Cardinality, string? RelationName);

public sealed record CocExtensionInfo(
    string TargetClass,
    string TargetMethod,
    string ExtensionClass,
    string Model);

public sealed record SecurityCoverage(
    string ObjectName,
    string ObjectType,
    IReadOnlyList<SecurityRoute> Routes);

public sealed record SecurityRoute(string Role, string? Duty, string? Privilege, string? EntryPoint);

public sealed record ObjectExtensionInfo(string Kind, string TargetName, string ExtensionName, string Model, string? SourcePath);

public sealed record EventSubscriberInfo(
    string SubscriberClass,
    string SubscriberMethod,
    string SourceKind,
    string SourceObject,
    string? SourceMember,
    string? EventType,
    string Model);

public sealed record FormInfo(long FormId, string Name, string Model, string? SourcePath);
public sealed record FormDataSourceInfo(string Name, string? TableName);
public sealed record FormDetails(FormInfo Form, IReadOnlyList<FormDataSourceInfo> DataSources);

public sealed record SecurityRoleDetails(
    string Name,
    string? Label,
    string Model,
    IReadOnlyList<string> Duties,
    IReadOnlyList<string> Privileges);

public sealed record SecurityDutyDetails(
    string Name,
    string? Label,
    string Model,
    IReadOnlyList<string> Privileges);

public sealed record SecurityPrivilegeDetails(
    string Name,
    string? Label,
    string Model,
    IReadOnlyList<SecurityEntryPointInfo> EntryPoints);

public sealed record SecurityEntryPointInfo(string ObjectName, string? ObjectType, string? ObjectChild, string? AccessLevel);

public sealed record TableMethodInfo(string Name, string? Signature, string? ReturnType, bool IsStatic);
public sealed record TableIndexInfo(string Name, bool AllowDuplicates, bool AlternateKey, string? FieldsCsv);
public sealed record TableDeleteActionInfo(string? Name, string RelatedTable, string? DeleteAction);

public sealed record QueryInfo(long QueryId, string Name, string Model, string? SourcePath);
public sealed record QueryDataSourceInfo(string Name, string? TableName, string? JoinMode, string? ParentDs);
public sealed record QueryDetails(QueryInfo Query, IReadOnlyList<QueryDataSourceInfo> DataSources);

public sealed record ViewInfo(long ViewId, string Name, string Model, string? Label, string? QueryName, string? SourcePath);
public sealed record ViewFieldInfo(string Name, string? DataSource, string? DataField);
public sealed record ViewDetails(ViewInfo View, IReadOnlyList<ViewFieldInfo> Fields);

public sealed record DataEntityInfo(
    long EntityId,
    string Name,
    string Model,
    string? PublicEntityName,
    string? PublicCollectionName,
    string? StagingTable,
    string? QueryName,
    string? Label,
    string? SourcePath);
public sealed record DataEntityFieldInfo(string Name, string? DataSource, string? DataField, bool IsMandatory, bool IsReadOnly);
public sealed record DataEntityDetails(DataEntityInfo Entity, IReadOnlyList<DataEntityFieldInfo> Fields);

public sealed record ReportInfo(long ReportId, string Name, string? Kind, string Model, string? SourcePath);
public sealed record ReportDataSetInfo(string Name, string? Kind, string? QueryOrClass);
public sealed record ReportDetails(ReportInfo Report, IReadOnlyList<ReportDataSetInfo> DataSets);

public sealed record ServiceInfo(long ServiceId, string Name, string? Class, string Model, string? SourcePath);
public sealed record ServiceOperationInfo(string OperationName, string? MethodName);
public sealed record ServiceDetails(ServiceInfo Service, IReadOnlyList<ServiceOperationInfo> Operations);

public sealed record ServiceGroupInfo(long GroupId, string Name, string Model, string? SourcePath);
public sealed record ServiceGroupDetails(ServiceGroupInfo Group, IReadOnlyList<string> Members);

public sealed record WorkflowTypeInfo(string Name, string? Category, string? DocumentClass, string Model, string? SourcePath);
