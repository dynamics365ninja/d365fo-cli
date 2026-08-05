using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>One field on a scaffolded <c>AxMap</c>; <paramref name="Edt"/> drives the field's concrete subtype.</summary>
public sealed record MapFieldSpec(string Name, string Edt, string? Label = null);

/// <summary>
/// One table a map is wired to, plus the map-field → table-field connections.
/// A mapping with no connections is legal but inert, so callers get a warning-free
/// path only when they name at least one pair.
/// </summary>
public sealed record MapTableMappingSpec(string Table, IReadOnlyList<MapFieldConnection> Connections);

/// <param name="MapField">Field on the map.</param>
/// <param name="TableField">Field on the mapped table; defaults to the same name.</param>
public sealed record MapFieldConnection(string MapField, string? TableField = null)
{
    public string EffectiveTableField => string.IsNullOrWhiteSpace(TableField) ? MapField : TableField!;
}

/// <summary>
/// Scaffolds an <c>AxMap</c> — a shared field template several tables can be
/// addressed through.
/// </summary>
/// <remarks>
/// Ground-truthed against shipped standard-model maps on a real AOS (e.g.
/// <c>ApplicationSuite\Foundation\AxMap\AgreementHeaderDefaultMap.xml</c> and
/// <c>AssetTransMap.xml</c>): fields are <c>&lt;AxMapBaseField&gt;</c> carrying
/// <c>i:type="AxMapField&lt;Suffix&gt;"</c> from the same primitive vocabulary as
/// <c>AxTableField</c>, and mappings are <c>&lt;AxTableMapping&gt;</c> with a
/// <c>&lt;MappingTable&gt;</c> plus <c>&lt;Connections&gt;</c> of
/// <c>MapField</c>/<c>MapFieldTo</c> pairs.
/// </remarks>
public static class MapScaffolder
{
    /// <param name="name">Map name (the AOT <c>&lt;Name&gt;</c> and file stem).</param>
    /// <param name="fields">The map's own fields; at least one is required.</param>
    /// <param name="mappings">Tables this map is wired to. May be empty — mappings are often added later.</param>
    /// <param name="label">Optional label.</param>
    /// <param name="edtBaseTypeResolver">
    /// Optional EDT → primitive base-type callback (typically
    /// <c>MetadataRepository.GetEdt(name)?.BaseType</c>) used to stamp each field's
    /// concrete <c>i:type</c>. Shares <see cref="XppScaffolder"/>'s resolution, so a
    /// map field and a table field on the same EDT cannot disagree. Falls back to a
    /// name heuristic when null or unresolved.
    /// </param>
    public static XDocument Map(
        string name,
        IEnumerable<MapFieldSpec> fields,
        IEnumerable<MapTableMappingSpec>? mappings = null,
        string? label = null,
        Func<string, string?>? edtBaseTypeResolver = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Map name is required.", nameof(name));

        var fieldList = (fields ?? Enumerable.Empty<MapFieldSpec>()).ToList();
        if (fieldList.Count == 0)
            throw new ArgumentException("At least one field is required.", nameof(fields));

        var mappingList = (mappings ?? Enumerable.Empty<MapTableMappingSpec>()).ToList();

        // A mapping may only connect fields the map actually declares — otherwise the
        // map compiles to something that silently never matches.
        var declared = new HashSet<string>(fieldList.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var m in mappingList)
        {
            var unknown = m.Connections?.FirstOrDefault(c => !declared.Contains(c.MapField));
            if (unknown is not null)
                throw new ArgumentException(
                    $"Mapping for table '{m.Table}' connects '{unknown.MapField}', which is not a field on map '{name}'.",
                    nameof(mappings));
        }

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var fieldEls = fieldList.Select(f =>
        {
            if (string.IsNullOrWhiteSpace(f.Name))
                throw new ArgumentException("Every map field needs a name.", nameof(fields));
            if (string.IsNullOrWhiteSpace(f.Edt))
                throw new ArgumentException($"Map field '{f.Name}' needs an EDT — it determines the field's type.", nameof(fields));

            var suffix = XppScaffolder.ConcreteFieldSuffix(f.Edt, edtBaseTypeResolver);
            var el = new XElement("AxMapBaseField",
                new XAttribute(XName.Get("type", xsi.NamespaceName), $"AxMapField{suffix}"),
                new XElement("Name", f.Name),
                new XElement("ExtendedDataType", f.Edt));
            if (!string.IsNullOrEmpty(f.Label)) el.Add(new XElement("Label", f.Label));
            return el;
        });

        var mappingEls = mappingList.Select(m => new XElement("AxTableMapping",
            new XElement("MappingTable", m.Table),
            new XElement("Connections",
                (m.Connections ?? Array.Empty<MapFieldConnection>()).Select(c =>
                    new XElement("AxTableMappingConnection",
                        new XElement("MapField", c.MapField),
                        new XElement("MapFieldTo", c.EffectiveTableField))))));

        var sourceCode = new XElement("SourceCode",
            new XElement("Declaration",
                new XCData($"\npublic class {name} extends common\n{{\n}}\n")),
            new XElement("Methods"));

        return new XDocument(
            new XElement("AxMap",
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XElement("Name", name),
                sourceCode,
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("FieldGroups"),
                new XElement("Fields", fieldEls),
                new XElement("Mappings", mappingEls)));
    }
}
