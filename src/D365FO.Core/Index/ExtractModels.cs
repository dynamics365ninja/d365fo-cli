namespace D365FO.Core.Index;

/// <summary>
/// In-memory bundle of one model's extracted metadata. Produced by the
/// <see cref="Extract.MetadataExtractor"/> and consumed by
/// <see cref="MetadataRepository.ApplyExtract"/>.
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

public sealed record ExtractedTable(string Name, string? Label, string? SourcePath, IReadOnlyList<ExtractedTableField> Fields);
public sealed record ExtractedTableField(string Name, string? Type, string? EdtName, string? Label, bool Mandatory);
public sealed record ExtractedClass(string Name, string? Extends, bool IsAbstract, bool IsFinal, string? SourcePath, IReadOnlyList<ExtractedMethod> Methods);
public sealed record ExtractedMethod(string Name, string? Signature, string? ReturnType, bool IsStatic);
public sealed record ExtractedEdt(string Name, string? Extends, string? BaseType, string? Label, int? StringSize);
public sealed record ExtractedEnum(string Name, string? Label, IReadOnlyList<ExtractedEnumValue> Values);
public sealed record ExtractedEnumValue(string Name, int? Value, string? Label);
public sealed record ExtractedMenuItem(string Name, string Kind, string? Object, string? ObjectType, string? Label);
public sealed record ExtractedCoc(string TargetClass, string TargetMethod, string ExtensionClass);
public sealed record ExtractedLabel(string File, string Language, string Key, string? Value);

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
    long Coc);
