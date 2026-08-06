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
    /// Generates a minimal but valid <c>AxSecurityPolicy</c> document.
    /// </summary>
    /// <param name="constrainedTable">Table the policy constrains — emitted as <c>PrimaryTable</c>.</param>
    public static XDocument Policy(
        string name,
        string constrainedTable,
        string policyQuery,
        PolicyOperation operation = PolicyOperation.Select,
        PolicyContextType contextType = PolicyContextType.RoleName,
        string? contextValue = null)
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
        root.Add(new XElement("Operation", operationStr));
        root.Add(new XElement("PrimaryTable", constrainedTable));
        root.Add(new XElement("Query", policyQuery));
        root.Add(new XElement("ConstrainedTables"));

        return new XDocument(root);
    }
}
