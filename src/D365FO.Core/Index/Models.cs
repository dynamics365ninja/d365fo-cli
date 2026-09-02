namespace D365FO.Core.Index;

/// <summary>Minimal record types returned by the metadata repository.</summary>
public sealed record ModelInfo(long ModelId, string Name, string? Publisher, string? Layer, bool IsCustom);

public sealed record ModelDependencies(ModelInfo Model, IReadOnlyList<string> DependsOn, IReadOnlyList<string> DependedBy);

/// <summary>Per-model counters for <c>d365fo stats</c>.</summary>
public sealed record PerModelStat(
    string Model, bool IsCustom,
    long Tables, long Classes, long Edts, long Enums,
    long MenuItems, long Forms, long Extensions, long Coc, long Labels);

/// <summary>Top-N table by field count.</summary>
public sealed record TopTableStat(string Name, string Model, long FieldCount);

/// <summary>Top-N class by method count.</summary>
public sealed record TopClassStat(string Name, string Model, long MethodCount);

/// <summary>Top-N Chain-of-Command target class by extension count.</summary>
public sealed record TopCocStat(string Target, long ExtensionCount);

/// <summary>Aggregate roll-up used by <c>d365fo stats</c>.</summary>
public sealed record IndexStats(
    IReadOnlyList<PerModelStat> PerModel,
    IReadOnlyList<TopTableStat> TopTables,
    IReadOnlyList<TopClassStat> TopClasses,
    IReadOnlyList<TopCocStat> TopCocTargets);

/// <summary>Finding from <c>d365fo lint</c>.</summary>
public sealed record LintHit(string TargetName, string Model, string? Detail);

/// <summary>One row of <c>_ExtractionRuns</c> (see ROADMAP §1.3).</summary>
public sealed record ExtractionRunRow(
    long RunId,
    string StartedUtc,
    string Model,
    long ElapsedMs,
    long Tables,
    long Classes,
    long Edts,
    long Enums,
    long Labels,
    bool IsCustom);


public sealed record TableInfo(
    long TableId,
    string Name,
    string Model,
    string? Label,
    string? SourcePath,
    string? SaveDataPerCompany = null,
    string? CacheLookup = null,
    bool OccEnabled = false,
    string? ValidTimeStateFieldType = null,
    string? TableExtends = null,
    string? AOSAuthorization = null,
    string? FormRef = null,
    string? ListPageRef = null,
    bool SystemTable = false);

public sealed record TableFieldInfo(
    string Name,
    string? Type,
    string? EdtName,
    string? Label,
    bool Mandatory);

/// <summary>
/// A table that has a field whose name or EDT matches a lookup — used by
/// <see cref="MetadataRepository.FindTablesByField"/> to answer "which tables
/// contain field X" precisely (as opposed to relation-based heuristics).
/// <para>
/// <see cref="Source"/> is <c>"base"</c> when the field is declared on the base
/// table (<c>TableFields</c>) and <c>"extension"</c> when it is added by an
/// <c>AxTableExtension</c> (<c>ExtensionFields</c>). Extension-added fields land
/// on the same physical SQL column as base fields, so both must be returned to
/// reconcile with a direct database column query. <see cref="ExtensionName"/>
/// names the owning extension for <c>"extension"</c> rows and is null otherwise.
/// </para>
/// </summary>
public sealed record TableFieldMatch(
    string TableName,
    string Model,
    string FieldName,
    string? Type,
    string? EdtName,
    bool Mandatory,
    string Source = "base",
    string? ExtensionName = null);

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
    bool IsStatic)
{
    /// <summary>
    /// Class the method is actually declared on. Null for methods declared on the
    /// class being inspected; set only on entries in
    /// <see cref="ClassDetails.InheritedMethods"/>.
    /// </summary>
    /// <remarks>
    /// Deliberately a property rather than a positional parameter: the queries that
    /// materialize <see cref="MethodInfo"/> through Dapper select only the four
    /// positional columns, and Dapper requires a constructor matching the projection
    /// exactly. Inherited entries are constructed in code instead.
    /// </remarks>
    public string? DeclaringClass { get; init; }
}

/// <param name="Class">Header row for the class itself.</param>
/// <param name="Methods">Methods declared on the class itself.</param>
/// <param name="InheritedMethods">
/// Methods reachable through the <c>extends</c> chain but not redeclared locally,
/// nearest base class first, each stamped with its <c>DeclaringClass</c>. Empty when
/// the class has no base, when the base is outside the index, or when every base
/// method is overridden. Kept separate from <see cref="Methods"/> so callers can
/// still tell "this class defines it" from "it inherits it".
/// </param>
public sealed record ClassDetails(
    ClassInfo Class,
    IReadOnlyList<MethodInfo> Methods,
    IReadOnlyList<MethodInfo> InheritedMethods);

public sealed record EdtInfo(
    string Name,
    string Model,
    string? Extends,
    string? BaseType,
    string? Label,
    long? StringSize,
    string? ReferenceTable = null,
    string? FormHelp = null,
    string? AnalysisUsage = null,
    string? EnumType = null);

public sealed record EnumInfo(string Name, string Model, string? Label);

public sealed record EnumValueInfo(string Name, long? Value, string? Label);

public sealed record EnumDetails(EnumInfo Enum, IReadOnlyList<EnumValueInfo> Values);

public sealed record LabelMatch(string File, string Language, string Key, string? Value);

/// <summary>A method-body full-text hit from the opt-in <c>MethodSourceFts</c> index.</summary>
/// <summary>One object's declaration of a method, from the index.</summary>
/// <param name="Owner">Declaring object.</param>
/// <param name="OwnerKind">class | table.</param>
/// <param name="Model">Model the declaring object belongs to.</param>
/// <param name="Signature">Signature as extracted, or null when the extractor captured none.</param>
public sealed record MethodDeclaration(string Owner, string OwnerKind, string Model, string? Signature);

public sealed record MethodSourceMatch(
    string Kind,
    string ObjectName,
    string MethodName,
    string Model,
    string? SourcePath,
    string? Snippet);

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

/// <param name="ObjectName">Secured AOT object the coverage was computed for.</param>
/// <param name="ObjectType">Kind of that object (Table, Form, Menuitem, …).</param>
/// <param name="Routes">Role → duty → privilege → entry-point paths that grant access.</param>
/// <param name="RowLevel">
/// Record-level (XDS) coverage. Reported explicitly — including when it cannot be
/// determined — rather than left out, so an absent section is never mistaken for
/// "this table is unconstrained".
/// </param>
public sealed record SecurityCoverage(
    string ObjectName,
    string ObjectType,
    IReadOnlyList<SecurityRoute> Routes,
    RowLevelSecurityCoverage RowLevel);

/// <param name="State">
/// One of:
/// <list type="bullet">
/// <item><c>Constrained</c> — at least one AxSecurityPolicy constrains the table;
/// see <paramref name="Policies"/>.</item>
/// <item><c>NotConstrained</c> — the index holds security policies and none of them
/// name this table.</item>
/// <item><c>Unknown</c> — the index holds no security policies at all, so zero
/// matches proves nothing. Re-run <c>d365fo index refresh</c> to populate them.</item>
/// <item><c>NotApplicable</c> — XDS constrains tables; the queried object is not
/// one.</item>
/// </list>
/// </param>
/// <param name="Policies">
/// The policies that constrain the table; empty for every state but
/// <c>Constrained</c>.
/// </param>
public sealed record RowLevelSecurityCoverage(
    string State,
    IReadOnlyList<SecurityPolicyInfo> Policies);

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

// FormPatternRow / FormPatternSummary are exposed as classes (not positional
// records) because Dapper's record-constructor matcher misreads
// Microsoft.Data.Sqlite's COUNT(*) / COALESCE columns as byte[]. Property
// binding by name is robust regardless of the reported reader type.
public sealed class FormPatternRow
{
    public string Name { get; init; } = "";
    public string? Pattern { get; init; }
    public string? PatternVersion { get; init; }
    public string? Style { get; init; }
    public string? TitleDataSource { get; init; }
    public string Model { get; init; } = "";
    public string? SourcePath { get; init; }
    public string? PrimaryTable { get; init; }
    public long DataSourceCount { get; init; }
}

public sealed class FormPatternSummary
{
    public string Pattern { get; init; } = "";
    public long Count { get; init; }
}

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

// ---- AxMap DTOs (v10) ---------------------------------------------------

/// <summary>Summary info about a D365FO AxMap object.</summary>
public sealed record MapInfo(long MapId, string Name, string Model, string? Label, string? SourcePath);

/// <summary>A field defined on an AxMap.</summary>
public sealed record MapFieldInfo(string Name, string? Type, string? EdtName, string? Label);

/// <summary>Full detail of an AxMap including fields and mapped tables.</summary>
public sealed record MapDetails(MapInfo Map, IReadOnlyList<MapFieldInfo> Fields, IReadOnlyList<string> MappedTables);

// ---- v11 DTOs -----------------------------------------------------------

public sealed record BusinessEventInfo(
    long Id,
    string Name,
    string? Category,
    string? ContractClass,
    string Model,
    string? SourcePath);

public sealed record SecurityPolicyInfo(
    long Id,
    string Name,
    string? ConstrainedTable,
    string? PolicyQuery,
    string? OperationType,
    string? ContextType,
    bool IsEnabled,
    bool IsMandatory,
    string Model,
    string? SourcePath);

public sealed record ConfigurationKeyInfo(
    long Id,
    string Name,
    string? Label,
    bool IsEnabled,
    string? ParentKey,
    string? LicenseCode,
    string Model);

public sealed record TileInfo(
    long Id,
    string Name,
    string? MenuItemName,
    string? MenuItemType,
    string? Label,
    string? TileType,
    string Model,
    string? SourcePath);

public sealed record WorkspaceInfo(
    long Id,
    string Name,
    string? Label,
    string Model,
    string? SourcePath);

// ---- Phase 7: developer experience DTOs ---------------------------------

/// <summary>Aggregated per-command timing row from <c>d365fo stats --perf</c>.</summary>
public sealed record CommandTimingRow(string Command, long Calls, double AvgMs, long MaxMs, long MinMs);

/// <summary>Change-impact report from <c>d365fo analyze impact</c>.</summary>
public sealed record ImpactReport(
    string ObjectName,
    IReadOnlyList<object> Direct,
    IReadOnlyList<CocExtensionInfo>    CocWrappers,
    IReadOnlyList<EventSubscriberInfo> EventHandlers,
    IReadOnlyList<ObjectExtensionInfo> Extensions,
    IReadOnlyList<object>              FormDataSources,
    IReadOnlyList<object>              DataEntities,
    IReadOnlyList<object>              Queries);

// ---- Phase 5: integration analysis DTOs ---------------------------------

/// <summary>A single integration-readiness finding from <c>d365fo analyze integration</c>.</summary>
public sealed record IntegrationIssue(string EntityName, string Model, string Code, string Detail);

/// <summary>Aggregated integration surface report from <c>d365fo report integrations</c>.</summary>
public sealed record IntegrationReport(
    IReadOnlyList<DataEntityInfo>    ODataEntities,
    IReadOnlyList<ServiceInfo>       CustomServices,
    IReadOnlyList<BusinessEventInfo> BusinessEvents,
    IReadOnlyList<WorkflowTypeInfo>  WorkflowTypes,
    IReadOnlyList<ClassInfo>         BatchJobs);

// ---- Index maintenance DTOs ----------------------------------------------

/// <summary>Result of <c>d365fo index optimize</c> (VACUUM + ANALYZE).</summary>
public sealed record OptimizeResult(long SizeBeforeBytes, long SizeAfterBytes, long ElapsedMs);

/// <summary>Row counts returned by <c>d365fo daemon warmup</c>.</summary>
public sealed record WarmupResult(
    long Tables,
    long Classes,
    long Methods,
    long Edts,
    long Enums,
    long Labels,
    long Forms,
    long CocExtensions,
    long DataEntities);
