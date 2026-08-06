using System.Xml.Linq;
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
/// Two shapes trigger the reset: an element named after a contract type (<c>Ax*</c>), and the
/// members listed in <see cref="_emptyNamespaceMembers"/>, whose value type is a contract in
/// the empty namespace even though the member name is not <c>Ax</c>-prefixed.
/// </para>
/// </remarks>
public static class ContractNamespaceApplier
{
    /// <summary>
    /// Members whose children belong to the empty namespace despite a non-<c>Ax</c> name,
    /// keyed by root element. Ground-truthed against shipped files: an
    /// <c>AxWorkflowApproval</c>'s four outcome members each hold an outcome contract that
    /// declares no namespace, so their children carry <c>xmlns=""</c>.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> _emptyNamespaceMembers = new(StringComparer.Ordinal)
    {
        ["AxWorkflowApproval"] = new(StringComparer.Ordinal) { "Approve", "Deny", "Reject", "RequestChange" },
    };

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
        if (!string.IsNullOrEmpty(root.Name.NamespaceName)) return;

        var type = ObjectTypeRegistry.Find(root.Name.LocalName);
        if (type is null || string.IsNullOrEmpty(type.ContractNamespace)) return;

        XNamespace ns = type.ContractNamespace;
        var resetMembers = _emptyNamespaceMembers.TryGetValue(root.Name.LocalName, out var members)
            ? members
            : null;

        Move(root, ns, resetMembers);
    }

    private static void Move(XElement element, XNamespace ns, HashSet<string>? resetMembers)
    {
        element.Name = ns + element.Name.LocalName;

        foreach (var child in element.Elements())
        {
            // A nested contract object starts a subtree that contracts into the empty
            // namespace; so does a member whose value type does, even when its own element
            // name is not Ax-prefixed.
            var resets = child.Name.LocalName.StartsWith("Ax", StringComparison.Ordinal)
                         || (resetMembers is not null && resetMembers.Contains(child.Name.LocalName));

            if (resets)
            {
                // The member element itself stays in the parent's namespace; only what it
                // contains resets — matching <Approve><Name xmlns="">…</Name></Approve>.
                if (child.Name.LocalName.StartsWith("Ax", StringComparison.Ordinal))
                    continue;

                child.Name = ns + child.Name.LocalName;
                continue;
            }

            Move(child, ns, resetMembers);
        }
    }
}
