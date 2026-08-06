using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public enum PolicyOperation { All, Select }

public enum PolicyContextType { RoleName, ContextString }

/// <summary>
/// Scaffolds an <c>AxSecurityPolicy</c> (XDS / extensible data security policy) for
/// D365FO. Binds a policy query to the table it constrains, with configurable operation
/// scope and context type.
/// </summary>
/// <remarks>
/// Element names and order are ground-truthed against the shipped policies in
/// <c>ApplicationSuite\Foundation\AxSecurityPolicy</c>. The trap this encodes:
/// <c>ConstrainedTable</c> is a <c>NoYes</c> flag meaning "the primary table is
/// constrained", <em>not</em> the table's name — the table goes in <c>PrimaryTable</c>.
/// Writing the name there produced a document the metadata provider refuses outright
/// ("Invalid enum value 'FmVehicle' … into type NoYes").
/// </remarks>
public static class SecurityPolicyScaffolder
{
    /// <summary>
    /// One entry in a policy's <c>ConstrainedTables</c> tree.
    /// </summary>
    /// <param name="Name">The table (or entity) the policy reaches.</param>
    /// <param name="Constrained">
    /// Whether the policy actually restricts this table, or merely traverses it to get to one
    /// that is. A tree of tables all marked constrained is not the same policy as a tree where
    /// only the leaves are.
    /// </param>
    /// <param name="Children">Tables reached through this one — the collection nests.</param>
    public sealed record ConstrainedEntity(
        string Name,
        bool Constrained = true,
        IReadOnlyList<ConstrainedEntity>? Children = null);

    /// <summary>
    /// Generates a minimal but valid <c>AxSecurityPolicy</c> document.
    /// </summary>
    /// <param name="constrainedTable">Table the policy constrains — emitted as <c>PrimaryTable</c>.</param>
    /// <param name="constrainedTables">
    /// The tables the policy reaches beyond the primary one (issue #162). Previously
    /// <c>&lt;ConstrainedTables&gt;</c> was always emitted empty, so a policy could constrain
    /// exactly one table and there was no way to express the rest of the tree.
    /// </param>
    /// <remarks>
    /// There is deliberately no <c>PolicyGroup</c>. It was on the list of things this scaffolder
    /// was missing, but <c>AxSecurityPolicy</c> has no such member — the contract catalog
    /// generated from <c>Microsoft.Dynamics.AX.Metadata.dll</c> lists ConstrainedTable,
    /// ContextString, ContextType, Enabled, HelpText, IsObsolete, Label, Operation, PrimaryTable,
    /// Query, RoleName, Tags, UseNotExistJoin, Visibility and ConstrainedTables, and nothing
    /// else. Emitting a <c>PolicyGroup</c> element would be silently dropped on read (XML007),
    /// which is precisely the defect class this repo exists to stop.
    /// </remarks>
    public static XDocument Policy(
        string name,
        string constrainedTable,
        string policyQuery,
        PolicyOperation operation = PolicyOperation.Select,
        PolicyContextType contextType = PolicyContextType.RoleName,
        string? contextValue = null,
        IEnumerable<ConstrainedEntity>? constrainedTables = null,
        string? label = null,
        bool useNotExistJoin = false)
    {
        var operationStr   = operation   == PolicyOperation.All            ? "AllOperations" : "Select";
        var contextTypeStr = contextType == PolicyContextType.ContextString ? "ContextString" : "RoleName";

        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var root = new XElement("AxSecurityPolicy",
            new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
            new XElement("Name", name));

        // Serializer order: Name, then the scalars alphabetically, then the collections.
        root.Add(new XElement("ConstrainedTable", "Yes"));
        if (contextType == PolicyContextType.ContextString && !string.IsNullOrWhiteSpace(contextValue))
            root.Add(new XElement("ContextString", contextValue));
        root.Add(new XElement("ContextType", contextTypeStr));
        root.Add(new XElement("Enabled", "Yes"));
        if (!string.IsNullOrWhiteSpace(label)) root.Add(new XElement("Label", label));
        root.Add(new XElement("Operation", operationStr));
        root.Add(new XElement("PrimaryTable", constrainedTable));
        root.Add(new XElement("Query", policyQuery));
        if (useNotExistJoin) root.Add(new XElement("UseNotExistJoin", "Yes"));
        root.Add(new XElement("ConstrainedTables",
            (constrainedTables ?? []).Select(ConstrainedEntityElement)));

        return new XDocument(root);
    }

    /// <summary>
    /// One <c>AxSecurityPolicyConstrainedEntity</c>, nesting its children under the collection
    /// of the same name.
    /// </summary>
    /// <remarks>
    /// Member order is Constrained, Name, Tags, ConstrainedTables — the flag comes first, so an
    /// entity written Name-first loses it and every table in the tree reads back as
    /// unconstrained.
    /// </remarks>
    private static XElement ConstrainedEntityElement(ConstrainedEntity entity)
        => new("AxSecurityPolicyConstrainedEntity",
            new XElement("Constrained", entity.Constrained ? "Yes" : "No"),
            new XElement("Name", entity.Name),
            entity.Children is { Count: > 0 }
                ? new XElement("ConstrainedTables", entity.Children.Select(ConstrainedEntityElement))
                : null);
}
