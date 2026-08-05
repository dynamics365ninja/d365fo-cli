using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public enum PolicyOperation { All, Select }

public enum PolicyContextType { RoleName, ContextString }

/// <summary>
/// Scaffolds an <c>AxSecurityPolicy</c> (XDS / extensible data security policy) for
/// D365FO. Binds a policy query to a constrained table with configurable operation
/// scope and context type.
/// </summary>
public static class SecurityPolicyScaffolder
{
    /// <summary>
    /// Generates a minimal but valid <c>AxSecurityPolicy</c> document.
    /// </summary>
    public static XDocument Policy(
        string name,
        string constrainedTable,
        string policyQuery,
        PolicyOperation operation = PolicyOperation.Select,
        PolicyContextType contextType = PolicyContextType.RoleName,
        string? contextValue = null)
    {
        var operationStr    = operation    == PolicyOperation.All    ? "AllOperations" : "Select";
        var contextTypeStr  = contextType  == PolicyContextType.ContextString ? "ContextString" : "RoleName";
        var contextValueStr = contextValue ?? "";

        return new XDocument(
            new XElement("AxSecurityPolicy",
                new XElement("Name", name),
                new XElement("ConstrainedTable", constrainedTable),
                new XElement("Query", policyQuery),
                new XElement("Operation", operationStr),
                new XElement("ContextType", contextTypeStr),
                new XElement("ContextString", contextValueStr),
                new XElement("Enabled", "Yes")));
    }
}
