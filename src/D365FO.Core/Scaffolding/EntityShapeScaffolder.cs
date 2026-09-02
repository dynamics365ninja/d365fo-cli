using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>A data entity referenced by a composite entity, and the entities embedded under it.</summary>
/// <param name="Name">Reference name inside the composite (defaults to the entity name).</param>
/// <param name="DataEntity">The <c>AxDataEntityView</c> referenced.</param>
/// <param name="Relation">For an embedded reference: the relation on the child entity that binds it to its parent.</param>
/// <param name="Embedded">Entities embedded under this one, each bound to it by its <see cref="Relation"/>.</param>
public sealed record CompositeEntityReferenceSpec(
    string Name,
    string DataEntity,
    string? Relation,
    IReadOnlyList<CompositeEntityReferenceSpec> Embedded);

/// <summary>One field of an aggregate data entity, mapped onto a measurement's measure or dimension attribute.</summary>
/// <param name="Name">Field name on the entity.</param>
/// <param name="MeasureGroup">Measure group inside the measurement.</param>
/// <param name="ExtendedDataType">EDT that types the field.</param>
/// <param name="Measure">For a measure field: the measure in the group.</param>
/// <param name="Dimension">For a dimension field: the dimension the attribute belongs to.</param>
/// <param name="Attribute">For a dimension field: the dimension attribute.</param>
public sealed record AggregateEntityFieldSpec(
    string Name,
    string MeasureGroup,
    string ExtendedDataType,
    string? Measure = null,
    string? Dimension = null,
    string? Attribute = null)
{
    public bool IsMeasure => !string.IsNullOrWhiteSpace(Measure);
}

/// <summary>
/// Scaffolds the two data-entity shapes that are not a view over tables:
/// <c>AxCompositeDataEntityView</c> (a header/lines bundle of existing entities, for DMF) and
/// <c>AxAggregateDataEntity</c> (a read-only projection of an aggregate measurement, for the
/// entity store and analytical workspaces).
/// </summary>
/// <remarks>
/// Both are ground-truthed against shipped files (<c>DMFTestCompositeHeaderLineEntity</c>,
/// <c>FMCustomerActivity</c>). The composite's X++ declaration carries the
/// <c>[CompositeDataEntityView]</c> attribute and does <em>not</em> extend <c>common</c>; the
/// aggregate's does. An aggregate field is an <c>AxAggregateDataEntityField</c> whose
/// <c>i:type</c> pins <c>AxAggregateDataEntityMappedField</c>, a subtype declared in the
/// <c>Microsoft.Dynamics.AX.Metadata.V2</c> contract namespace — so the members it adds
/// (<c>Measure</c>, <c>Dimension</c>, <c>Attribute</c>, <c>MeasureGroup</c>,
/// <c>ExtendedDataType</c>) are written in that namespace, exactly as the platform does with its
/// <c>d3p1:</c> prefix. Written unprefixed, the deserializer keeps the field and drops every
/// mapping on it.
/// </remarks>
public static class EntityShapeScaffolder
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace V2 = "Microsoft.Dynamics.AX.Metadata.V2";

    /// <summary>
    /// An <c>AxCompositeDataEntityView</c> over one or more root entities, each with the
    /// entities embedded under it.
    /// </summary>
    public static XDocument CompositeDataEntityView(
        string name,
        IEnumerable<CompositeEntityReferenceSpec> roots,
        string? label = null,
        string? tags = null,
        string? modules = null,
        string? entityCategory = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Composite entity name is required.", nameof(name));
        var rootList = (roots ?? []).ToList();
        if (rootList.Count == 0)
            throw new ArgumentException("A composite entity needs at least one --root entity.", nameof(roots));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rootList)
        {
            if (!string.IsNullOrEmpty(r.Relation))
                throw new ArgumentException($"Root reference '{r.Name}' cannot carry a relation; only embedded entities bind to a parent.", nameof(roots));
            Validate(r, seen, isRoot: true);
        }

        var root = new XElement("AxCompositeDataEntityView",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name),
            new XElement("SourceCode",
                new XElement("Declaration",
                    new XCData($"\n[CompositeDataEntityView]\npublic class {name}\n{{\n}}\n\n")),
                new XElement("Methods")));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        if (!string.IsNullOrEmpty(tags)) root.Add(new XElement("Tags", tags));
        if (!string.IsNullOrEmpty(entityCategory)) root.Add(new XElement("EntityCategory", entityCategory));
        if (!string.IsNullOrEmpty(modules)) root.Add(new XElement("Modules", modules));
        root.Add(new XElement("RootDataEntities", rootList.Select(r => Reference(r, "AxDataEntityViewReferenceRoot"))));
        return new XDocument(root);
    }

    private static void Validate(CompositeEntityReferenceSpec r, HashSet<string> seen, bool isRoot)
    {
        if (string.IsNullOrWhiteSpace(r.DataEntity))
            throw new ArgumentException("Every reference names a data entity.", nameof(r));
        if (string.IsNullOrWhiteSpace(r.Name))
            throw new ArgumentException($"Reference to '{r.DataEntity}' needs a name.", nameof(r));
        if (!seen.Add(r.Name))
            throw new ArgumentException($"Reference name '{r.Name}' is used twice; names are the keys of the composite.", nameof(r));
        if (!isRoot && string.IsNullOrWhiteSpace(r.Relation))
            throw new ArgumentException($"Embedded entity '{r.Name}' needs the relation that binds it to its parent.", nameof(r));
        foreach (var child in r.Embedded ?? [])
            Validate(child, seen, isRoot: false);
    }

    private static XElement Reference(CompositeEntityReferenceSpec r, string elementName)
    {
        var el = new XElement(elementName,
            new XElement("Name", r.Name),
            new XElement("DataEntity", r.DataEntity),
            new XElement("EmbeddedDataEntities",
                (r.Embedded ?? []).Select(c => Reference(c, "AxDataEntityViewReferenceEmbedded"))));
        if (!string.IsNullOrEmpty(r.Relation)) el.Add(new XElement("Relation", r.Relation));
        return el;
    }

    /// <summary>
    /// An <c>AxAggregateDataEntity</c> over <paramref name="measurement"/>: read-only, one
    /// data source named <c>DataSource</c>, the five automatic field groups every shipped one
    /// carries, and a mapped field per measure or dimension attribute.
    /// </summary>
    public static XDocument AggregateDataEntity(
        string name,
        string measurement,
        IEnumerable<AggregateEntityFieldSpec> fields,
        string? label = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Aggregate entity name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(measurement))
            throw new ArgumentException("An aggregate entity projects an aggregate measurement; --measurement is required.", nameof(measurement));
        var fieldList = (fields ?? []).ToList();
        if (fieldList.Count == 0)
            throw new ArgumentException("At least one --measure or --dimension field is required.", nameof(fields));

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in fieldList)
        {
            if (string.IsNullOrWhiteSpace(f.Name)) throw new ArgumentException("Every field needs a name.", nameof(fields));
            if (!names.Add(f.Name)) throw new ArgumentException($"Field '{f.Name}' is declared twice.", nameof(fields));
            if (string.IsNullOrWhiteSpace(f.MeasureGroup))
                throw new ArgumentException($"Field '{f.Name}' needs the measure group it maps into.", nameof(fields));
            if (string.IsNullOrWhiteSpace(f.ExtendedDataType))
                throw new ArgumentException($"Field '{f.Name}' needs an EDT.", nameof(fields));
            var isDimension = !string.IsNullOrWhiteSpace(f.Dimension) || !string.IsNullOrWhiteSpace(f.Attribute);
            if (f.IsMeasure == isDimension)
                throw new ArgumentException($"Field '{f.Name}' maps either a measure or a dimension attribute, not both and not neither.", nameof(fields));
            if (isDimension && (string.IsNullOrWhiteSpace(f.Dimension) || string.IsNullOrWhiteSpace(f.Attribute)))
                throw new ArgumentException($"Dimension field '{f.Name}' needs both the dimension and its attribute.", nameof(fields));
        }

        var root = new XElement("AxAggregateDataEntity",
            new XAttribute(XNamespace.Xmlns + "i", Xsi.NamespaceName),
            new XElement("Name", name),
            new XElement("SourceCode",
                new XElement("Declaration",
                    new XCData($"\npublic class {name} extends common\n{{\n}}\n")),
                new XElement("Methods")));
        if (!string.IsNullOrEmpty(label)) root.Add(new XElement("Label", label));
        root.Add(new XElement("IsReadOnly", "Yes"));
        root.Add(new XElement("AggregateViewDataSource",
            new XElement("Name", "DataSource"),
            new XElement("Measurement", measurement),
            new XElement("MeasureGroups")));
        root.Add(new XElement("FieldGroups",
            AutoGroup("AutoReport"), AutoGroup("AutoLookup"),
            AutoGroup("AutoIdentification", autoPopulate: true),
            AutoGroup("AutoSummary"), AutoGroup("AutoBrowse")));
        root.Add(new XElement("Fields", fieldList.Select(MappedField)));
        root.Add(new XElement("Keys"));
        root.Add(new XElement("Relations"));
        return new XDocument(root);
    }

    private static XElement AutoGroup(string name, bool autoPopulate = false)
    {
        var g = new XElement("AxTableFieldGroup", new XElement("Name", name));
        if (autoPopulate) g.Add(new XElement("AutoPopulate", "Yes"));
        g.Add(new XElement("Fields"));
        return g;
    }

    private static XElement MappedField(AggregateEntityFieldSpec f)
    {
        // Member order is the serializer's: base-type members first (Name), then the V2
        // subtype's members alphabetically — Attribute, Dimension, ExtendedDataType, Measure,
        // MeasureGroup — which is the order every shipped file carries.
        var el = new XElement("AxAggregateDataEntityField",
            new XAttribute(XNamespace.Xmlns + "d3p1", V2.NamespaceName),
            new XAttribute(Xsi + "type", "d3p1:AxAggregateDataEntityMappedField"),
            new XElement("Name", f.Name));
        if (!f.IsMeasure)
        {
            el.Add(new XElement(V2 + "Attribute", f.Attribute));
            el.Add(new XElement(V2 + "Dimension", f.Dimension));
        }
        el.Add(new XElement(V2 + "ExtendedDataType", f.ExtendedDataType));
        if (f.IsMeasure) el.Add(new XElement(V2 + "Measure", f.Measure));
        el.Add(new XElement(V2 + "MeasureGroup", f.MeasureGroup));
        return el;
    }
}
