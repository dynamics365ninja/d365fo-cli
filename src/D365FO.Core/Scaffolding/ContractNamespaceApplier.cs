using System.Xml.Linq;
using D365FO.Core.Metadata;
using D365FO.Core.ObjectTypes;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Puts a scaffolded document into the XML namespace its MetaModel DataContract declares.
/// </summary>
/// <remarks>
/// <para>
/// Most AOT types contract into the empty namespace, so most generated files need nothing.
/// Menu items and tiles are <c>…Metadata.V1</c>, reports and workflow objects <c>…V2</c>,
/// forms <c>…V6</c> — and for those, a file written without the namespace is not merely
/// untidy, it is unreadable: <c>DataContractSerializer</c> fails with "Expecting element X
/// from namespace Y" before it looks at a single property. Menu items, reports and workflow
/// types shipped that way until the namespaces were ground-truthed against
/// <c>Microsoft.Dynamics.AX.Metadata.dll</c> (audit finding G3).
/// </para>
/// <para>
/// Nested objects reset to the empty namespace, because their own contracts declare it —
/// exactly what shipped files show (<c>&lt;AxReportDataSet xmlns=""&gt;</c> inside a V2
/// <c>AxReport</c>, <c>&lt;AxFormDataSource xmlns=""&gt;</c> inside a V6 <c>AxForm</c>).
/// Which members reset is not guessed from the element name: the catalog records, per member,
/// the namespace of the contract that member's value holds. A member named after a type
/// (<c>Ax*</c>) is the obvious case, but plenty are not — an <c>AxReport</c>'s
/// <c>DefaultParameterGroup</c> holds an <c>AxReportParameterGroup</c>, and writing its
/// contents in the report's V2 namespace loses every report parameter.
/// </para>
/// </remarks>
public static class ContractNamespaceApplier
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>
    /// Applies the registry's contract namespace for <paramref name="doc"/>'s root type.
    /// No-op when the type is unknown, contracts into the empty namespace, or the document
    /// already declares a default namespace (a template that ships its own, like the form
    /// templates).
    /// </summary>
    public static void Apply(XDocument doc)
    {
        var root = doc?.Root;
        if (root is null) return;

        DeclareXsiOnRoot(root);

        if (!string.IsNullOrEmpty(root.Name.NamespaceName)) return;

        var type = ObjectTypeRegistry.Find(root.Name.LocalName);
        if (type is null || string.IsNullOrEmpty(type.ContractNamespace)) return;

        Move(root, type.ContractNamespace);
    }

    /// <summary>
    /// Hoists the schema-instance namespace to the root under the prefix shipped files use.
    /// </summary>
    /// <remarks>
    /// Purely cosmetic to the serializer, which reads by namespace and ignores prefixes — but
    /// left alone, a subtype discriminator deep in the tree gets an auto-generated prefix
    /// (<c>p3:type</c>) declared at the point of use, and the file stops looking like AOT to
    /// anyone diffing it against the real thing. Every shipped file declares <c>xmlns:i</c> once,
    /// on the root.
    /// </remarks>
    private static void DeclareXsiOnRoot(XElement root)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";

        var used = root.DescendantsAndSelf()
            .Any(e => e.Attributes().Any(a => a.Name.Namespace == xsi));
        if (!used) return;

        if (root.Attributes().Any(a => a.IsNamespaceDeclaration && a.Value == xsi.NamespaceName)) return;

        root.SetAttributeValue(XNamespace.Xmlns + "i", xsi.NamespaceName);
    }

    /// <summary>
    /// Puts <paramref name="element"/> into <paramref name="ns"/> and each of its members'
    /// contents into whichever namespace that member's contract declares.
    /// </summary>
    private static void Move(XElement element, string ns)
    {
        XNamespace own = ns;
        element.Name = own + element.Name.LocalName;

        var children = element.Elements().ToList();
        if (children.Count == 0) return;

        var contract = MetadataContracts.ForElement(
            element.Name.LocalName,
            element.Attribute(Xsi + "type")?.Value);

        foreach (var child in children)
        {
            var local = child.Name.LocalName;

            // The member element itself belongs to its declaring type's namespace; only what it
            // contains moves — matching <DefaultParameterGroup><Name xmlns="">…</Name></…>.
            var contentNs = MetadataContracts.MemberContract(contract, local)?.Namespace
                // Without a contract to consult, fall back to the shape that is always true of
                // AOT: an element named after a type carries that type, and every nested type
                // in this catalog contracts into the empty namespace.
                ?? (local.StartsWith("Ax", StringComparison.Ordinal) ? string.Empty : ns);

            if (local.StartsWith("Ax", StringComparison.Ordinal))
            {
                // A collection item is the contract object itself, not a member holding one, so
                // its own name moves too.
                Move(child, contentNs);
                continue;
            }

            child.Name = own + local;
            foreach (var grandchild in child.Elements().ToList())
                Move(grandchild, contentNs);
        }
    }
}
