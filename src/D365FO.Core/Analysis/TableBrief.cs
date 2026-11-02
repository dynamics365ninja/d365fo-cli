using System.Text.Json.Nodes;
using D365FO.Core.Index;

namespace D365FO.Core.Analysis;

/// <summary>
/// The brief shape of a table: its properties, each field's name, type and EDT, index names
/// with their fields, and relation names with the related table. <c>get table --brief</c> and
/// MCP <c>get_object_info(objectType=table, detail=brief)</c> return it.
/// </summary>
/// <remarks>
/// The full answer is the right one when an agent is about to change a table, and the wrong
/// one when it only needs to know what the table holds. Measured on a real installation:
/// <c>get table</c> for one 60-field custom table returned 42.7k characters through the
/// bridge, of which fields were 24k, field groups 12k and relations 7k — mostly per-field
/// property bags and relation constraints nobody asked about. The brief keeps the facts a
/// caller needs to write a select or a join and drops the rest. The default output is
/// unchanged: existing callers depend on its shape.
/// </remarks>
public static class TableBrief
{
    /// <summary>The value of <c>detail</c> in a brief answer.</summary>
    public const string Detail = "brief";

    /// <summary>Brief shape from the index.</summary>
    public static object FromIndex(TableDetails details) => new
    {
        detail = Detail,
        table = details.Table,
        fields = details.Fields.Select(f => new { name = f.Name, type = f.Type, edt = f.EdtName }).ToList(),
        indexes = details.Indexes.Select(i => new
        {
            name = i.Name,
            fields = SplitCsv(i.FieldsCsv),
            unique = i.AllowDuplicates ? (bool?)null : true,
        }).ToList(),
        // The index also records relations other tables declare TO this one; the brief is the
        // table's own shape, so only the relations it declares.
        relations = details.Relations
            .Where(r => string.Equals(r.FromTable, details.Table.Name, StringComparison.OrdinalIgnoreCase))
            .Select(r => new { name = r.RelationName, relatedTable = r.ToTable })
            .ToList(),
        methodCount = details.Methods.Count,
    };

    /// <summary>
    /// Brief shape from the bridge payload (the serialised AxTable, PascalCase members).
    /// <paramref name="indexed"/> supplies each field's type, which the payload does not carry
    /// (it is the concrete <c>AxTableField*</c> subclass); without it only enum fields are typed.
    /// </summary>
    public static JsonObject FromBridge(JsonObject payload, TableDetails? indexed)
    {
        var types = indexed?.Fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Type, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var properties = new JsonObject();
        foreach (var (key, value) in payload)
            if (value is JsonValue) properties[key] = value.DeepClone();

        var fields = new JsonArray();
        foreach (var field in Items(payload, "Fields"))
        {
            var name = Text(field, "Name");
            var enumType = Text(field, "EnumType");
            var type = name is not null && types.TryGetValue(name, out var t) ? t : null;
            fields.Add(Compact(new JsonObject
            {
                ["name"] = name,
                ["type"] = type ?? (enumType is not null ? "Enum" : null),
                ["edt"] = Text(field, "ExtendedDataType"),
                ["enumType"] = enumType,
            }));
        }

        var indexes = new JsonArray();
        foreach (var index in Items(payload, "Indexes"))
        {
            var columns = new JsonArray();
            foreach (var column in Items(index, "Fields"))
                if ((Text(column, "DataField") ?? Text(column, "Name")) is { } c) columns.Add(c);
            indexes.Add(Compact(new JsonObject
            {
                ["name"] = Text(index, "Name"),
                ["fields"] = columns,
                ["unique"] = string.Equals(Text(index, "AllowDuplicates"), "Yes", StringComparison.OrdinalIgnoreCase)
                    ? null : (JsonNode)true,
            }));
        }

        var relations = new JsonArray();
        foreach (var relation in Items(payload, "Relations"))
            relations.Add(Compact(new JsonObject
            {
                ["name"] = Text(relation, "Name"),
                ["relatedTable"] = Text(relation, "RelatedTable"),
            }));

        return new JsonObject
        {
            ["detail"] = Detail,
            ["properties"] = properties,
            ["fields"] = fields,
            ["indexes"] = indexes,
            ["relations"] = relations,
            ["methodCount"] = Items(payload, "Methods").Count(),
        };
    }

    private static IReadOnlyList<string> SplitCsv(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonNode? Member(JsonObject obj, string name) =>
        obj.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static IEnumerable<JsonObject> Items(JsonObject obj, string name) =>
        Member(obj, name) is JsonArray array ? array.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    private static string? Text(JsonObject obj, string name) =>
        Member(obj, name) is JsonValue value && value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    /// <summary>Drop null members, as the index answer does (D365Json ignores nulls).</summary>
    private static JsonObject Compact(JsonObject obj)
    {
        foreach (var key in obj.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            obj.Remove(key);
        return obj;
    }
}
