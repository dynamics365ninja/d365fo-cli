using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// One field on a scaffolded <c>AxView</c>.
/// <para>
/// A <b>bound</b> field projects a column of a query data source: supply
/// <see cref="DataSource"/> and <see cref="DataField"/>. A <b>computed</b> field is
/// backed by an X++ method returning a SQL expression: supply
/// <see cref="ViewMethod"/> and the primitive <see cref="ComputedType"/> it returns.
/// The two forms are mutually exclusive.
/// </para>
/// </summary>
public sealed record ViewFieldSpec(
    string Name,
    string? DataSource = null,
    string? DataField = null,
    string? ViewMethod = null,
    string? ComputedType = null)
{
    public bool IsComputed => !string.IsNullOrWhiteSpace(ViewMethod);
}

/// <summary>
/// Scaffolds an <c>AxView</c> — a read-only, query-backed projection.
/// </summary>
/// <remarks>
/// Element order and the polymorphic field discriminators are ground-truthed against
/// shipped standard-model views on a real AOS (e.g.
/// <c>ApplicationSuite\Foundation\AxView\ActivityListOpenStatusView.xml</c>,
/// <c>AssetBookTableFiscalCalendarView.xml</c>): bound fields are always
/// <c>i:type="AxViewFieldBound"</c> — there is no per-primitive bound subtype — while
/// computed fields carry <c>AxViewFieldComputed&lt;Type&gt;</c> plus a
/// <c>&lt;ViewMethod&gt;</c>. Views deliberately emit no <c>&lt;Ranges&gt;</c> and no
/// invented <c>&lt;Title*&gt;</c>: ranges belong on the backing query, and a title the
/// caller did not ask for is metadata that cannot be justified.
/// </remarks>
public static class ViewScaffolder
{
    /// <summary>Suffixes a computed view field may declare, mirroring the AOT's own set.</summary>
    private static readonly HashSet<string> ComputedSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "String", "Int", "Int64", "Real", "Date", "UtcDateTime", "Enum",
    };

    /// <param name="name">View name (the AOT <c>&lt;Name&gt;</c> and file stem).</param>
    /// <param name="query">
    /// Backing <c>AxQuery</c> name. Required — a view without a query has nothing to
    /// project and will not build.
    /// </param>
    /// <param name="fields">Projected fields; at least one is required.</param>
    /// <param name="label">Optional label; emitted as <c>&lt;Label&gt;</c> when set.</param>
    /// <param name="configurationKey">Optional <c>AxConfigurationKey</c> gating the view.</param>
    public static XDocument View(
        string name,
        string query,
        IEnumerable<ViewFieldSpec> fields,
        string? label = null,
        string? configurationKey = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("View name is required.", nameof(name));
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException(
                "A view must name its backing AxQuery — without one there is nothing to project.", nameof(query));

        var fieldList = (fields ?? Enumerable.Empty<ViewFieldSpec>()).ToList();
        if (fieldList.Count == 0)
            throw new ArgumentException("At least one field is required.", nameof(fields));

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var fieldEls = fieldList.Select(f => BuildField(f, xsi));

        // <SourceCode> holds the view's own class declaration; the AOT stamps every
        // view with one extending `common`, even when it has no methods.
        var sourceCode = new XElement("SourceCode",
            new XElement("Declaration",
                new XCData($"\npublic class {name} extends common\n{{\n}}\n")),
            new XElement("Methods"));

        return new XDocument(
            new XElement("AxView",
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XElement("Name", name),
                sourceCode,
                string.IsNullOrEmpty(configurationKey) ? null : new XElement("ConfigurationKey", configurationKey),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("Query", query),
                // The five default field groups the AOT stamps on every view, same as
                // it does on tables (issue #110). Left empty: VS does not auto-populate
                // them, the developer assigns fields later in the designer.
                new XElement("FieldGroups",
                    BuildEmptyFieldGroup("AutoReport"),
                    BuildEmptyFieldGroup("AutoLookup"),
                    BuildEmptyFieldGroup("AutoIdentification", autoPopulate: true),
                    BuildEmptyFieldGroup("AutoSummary"),
                    BuildEmptyFieldGroup("AutoBrowse")),
                new XElement("Fields", fieldEls),
                new XElement("Indexes"),
                new XElement("Mappings"),
                new XElement("Relations"),
                new XElement("StateMachines"),
                new XElement("ViewMetadata",
                    new XElement("Name", "Metadata"),
                    new XElement("SourceCode", new XElement("Methods")),
                    new XElement("DataSources"))));
    }

    private static XElement BuildField(ViewFieldSpec f, XNamespace xsi)
    {
        if (string.IsNullOrWhiteSpace(f.Name))
            throw new ArgumentException("Every view field needs a name.", nameof(f));

        // NOTE: shipped views also carry a redundant xmlns="" on each field element.
        // It re-declares the (already empty) default namespace, so it is a no-op the
        // reader does not need — and XLinq will not emit it on a document that has no
        // default namespace to reset. Deliberately omitted, not forgotten.
        if (f.IsComputed)
        {
            var suffix = f.ComputedType;
            if (string.IsNullOrWhiteSpace(suffix) || !ComputedSuffixes.Contains(suffix!))
                throw new ArgumentException(
                    $"Computed view field '{f.Name}' needs an explicit type — one of " +
                    $"{string.Join(", ", ComputedSuffixes.Order(StringComparer.Ordinal))}. " +
                    "The AOT encodes it in the field's i:type and cannot infer it from the method.",
                    nameof(f));

            var canonical = ComputedSuffixes.First(s => string.Equals(s, suffix, StringComparison.OrdinalIgnoreCase));
            return new XElement("AxViewField",
                new XAttribute(XName.Get("type", xsi.NamespaceName), $"AxViewFieldComputed{canonical}"),
                new XElement("Name", f.Name),
                new XElement("ViewMethod", f.ViewMethod));
        }

        if (string.IsNullOrWhiteSpace(f.DataSource) || string.IsNullOrWhiteSpace(f.DataField))
            throw new ArgumentException(
                $"Bound view field '{f.Name}' needs both a data source and a data field " +
                "(or a view method, to make it computed).", nameof(f));

        return new XElement("AxViewField",
            new XAttribute(XName.Get("type", xsi.NamespaceName), "AxViewFieldBound"),
            new XElement("Name", f.Name),
            new XElement("DataField", f.DataField),
            new XElement("DataSource", f.DataSource));
    }

    private static XElement BuildEmptyFieldGroup(string name, bool autoPopulate = false) =>
        new XElement("AxTableFieldGroup",
            new XElement("Name", name),
            autoPopulate ? new XElement("AutoPopulate", "Yes") : null,
            new XElement("Fields"));
}
