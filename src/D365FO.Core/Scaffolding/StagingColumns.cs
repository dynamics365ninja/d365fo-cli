using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>How a staging column is typed: the table field it mirrors, as that table declares it.</summary>
/// <param name="Kind">The field subtype, e.g. <c>String</c> for <c>AxTableFieldString</c>.</param>
/// <param name="Edt">The field's extended data type, when it has one.</param>
/// <param name="EnumType">The base enum of an enum field.</param>
/// <param name="StringSize">The size the field states for itself.</param>
public sealed record StagingColumnType(string Kind, string? Edt, string? EnumType, string? StringSize = null);

/// <summary>
/// The field types of an entity's source table, read from the table's own file.
/// </summary>
/// <remarks>
/// The index holds a field's EDT but neither its subtype nor its enum, so a staging table typed
/// from it would be guessed — which is how every column used to come out as a string.
/// </remarks>
public static class StagingColumns
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>The fields of the table at <paramref name="tablePath"/> by name; empty when it cannot be read.</summary>
    public static IReadOnlyDictionary<string, StagingColumnType> FromTableFile(string? tablePath)
    {
        var columns = new Dictionary<string, StagingColumnType>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tablePath) || !File.Exists(tablePath)) return columns;
        try
        {
            var fields = XDocument.Load(tablePath).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Fields");
            foreach (var field in fields?.Elements() ?? [])
            {
                var subtype = (string?)field.Attribute(Xsi + "type");
                var name = Child(field, "Name");
                if (name is null || subtype is null || !subtype.StartsWith("AxTableField", StringComparison.Ordinal)) continue;
                columns[name] = new StagingColumnType(
                    subtype["AxTableField".Length..], Child(field, "ExtendedDataType"),
                    Child(field, "EnumType"), Child(field, "StringSize"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            columns.Clear();
        }
        return columns;
    }

    private static string? Child(XElement parent, string name)
        => parent.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value is { Length: > 0 } value ? value : null;
}
