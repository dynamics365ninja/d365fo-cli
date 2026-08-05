using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// A data source specification for <see cref="QueryScaffolder.Query"/>.
/// <para>
/// Root data sources have no <see cref="ParentDs"/>. Joined data sources specify
/// the <see cref="ParentDs"/> by name (defaults to the parent's <see cref="Table"/>
/// when only one root exists). <see cref="JoinMode"/> follows the real
/// <c>AxQuerySimpleEmbeddedDataSource.JoinMode</c> enum values:
/// <c>InnerJoin</c>, <c>OuterJoin</c>, <c>ExistsJoin</c>, <c>NoExistsJoin</c>
/// (note: no "t" — confirmed against shipped platform AxQuery XML; this differs
/// from the unrelated <c>SysDaJoinKind::NotExistsJoin</c> spelling).
/// </para>
/// </summary>
public sealed record QueryDataSourceSpec(
    string Table,
    string? Name = null,
    string? ParentDs = null,
    string JoinMode = "InnerJoin");

/// <summary>Scaffolds <c>AxQuery</c> objects with nested data-source joins.</summary>
public static class QueryScaffolder
{
    public static XDocument Query(string name, IEnumerable<QueryDataSourceSpec> dataSources)
    {
        var dsList = (dataSources ?? throw new ArgumentNullException(nameof(dataSources))).ToList();
        if (dsList.Count == 0)
            throw new ArgumentException("At least one data source is required.", nameof(dataSources));

        var roots = dsList.Where(ds => string.IsNullOrEmpty(ds.ParentDs)).ToList();
        var joins  = dsList.Where(ds => !string.IsNullOrEmpty(ds.ParentDs)).ToList();

        // When the caller does not tag any ParentDs we treat everything except the
        // first entry as joined children of the first (the most common quick-use case).
        if (roots.Count == 0)
        {
            roots = [dsList[0]];
            joins  = dsList.Skip(1).Select(ds => ds with { ParentDs = dsList[0].Name ?? dsList[0].Table }).ToList();
        }

        // AxQuery is an abstract MetaModel base, like AxEdt: every shipped query file is
        // <AxQuery xmlns:i="…" i:type="AxQuerySimple">, and without that discriminator the
        // metadata reader throws "Cannot create an abstract class". The datasource elements
        // below already commit to the AxQuerySimple family, so the root has to say so too.
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        return new XDocument(
            new XElement("AxQuery",
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XAttribute(xsi + "type", "AxQuerySimple"),
                new XElement("Name", name),
                new XElement("DataSources",
                    roots.Select(r => BuildRoot(r, joins)))));
    }

    private static XElement BuildRoot(QueryDataSourceSpec ds, List<QueryDataSourceSpec> allJoins)
    {
        var dsName   = ds.Name ?? ds.Table;
        var children = allJoins
            .Where(j => string.Equals(j.ParentDs, dsName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(j.ParentDs, ds.Table, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var el = new XElement("AxQuerySimpleRootDataSource",
            new XElement("Name", dsName),
            new XElement("Table", ds.Table));

        if (children.Count > 0)
            el.Add(new XElement("DataSources", children.Select(c => BuildJoin(c, allJoins))));

        return el;
    }

    private static XElement BuildJoin(QueryDataSourceSpec ds, List<QueryDataSourceSpec> allJoins)
    {
        var dsName   = ds.Name ?? ds.Table;
        var children = allJoins
            .Where(j => string.Equals(j.ParentDs, dsName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(j.ParentDs, ds.Table, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var el = new XElement("AxQuerySimpleEmbeddedDataSource",
            new XElement("Name", dsName),
            new XElement("Table", ds.Table),
            new XElement("JoinMode", ds.JoinMode),
            new XElement("UseRelations", "Yes"));

        if (children.Count > 0)
            el.Add(new XElement("DataSources", children.Select(c => BuildJoin(c, allJoins))));

        return el;
    }
}
