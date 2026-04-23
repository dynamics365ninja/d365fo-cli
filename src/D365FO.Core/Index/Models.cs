namespace D365FO.Core.Index;

/// <summary>Minimal record types returned by the metadata repository.</summary>
public sealed record ModelInfo(long ModelId, string Name, string? Publisher, string? Layer, bool IsCustom);

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
    IReadOnlyList<RelationInfo> Relations);

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
