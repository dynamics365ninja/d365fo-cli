using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public sealed record OperationSpec(string Name, string ReturnType, string? ContractParam = null);

/// <summary>
/// Scaffolds the three-file D365FO custom SOAP service pattern:
/// an <c>AxClass</c> service class, an <c>AxService</c> XML, and an
/// <c>AxServiceGroup</c> XML.
/// </summary>
public static class CustomServiceScaffolder
{
    /// <summary>
    /// Scaffolds the <c>AxClass</c> service implementation.
    /// </summary>
    public static XDocument ServiceClass(
        string className,
        IReadOnlyList<OperationSpec> operations)
    {
        var ops = (operations ?? Array.Empty<OperationSpec>()).ToList();
        if (ops.Count == 0)
            ops.Add(new OperationSpec("process", "void"));

        var declaration =
            $"public class {className}\n" +
            "{\n" +
            "}\n";

        var methods = ops.Select(op =>
        {
            var contractParam = string.IsNullOrWhiteSpace(op.ContractParam)
                ? ""
                : $"{op.ContractParam} _contract";

            var defaultReturn = op.ReturnType switch
            {
                "void"     => "",
                "boolean"  => "\n    return false;",
                "int"      => "\n    return 0;",
                "int64"    => "\n    return 0;",
                "real"     => "\n    return 0.0;",
                "str"      => "\n    return '';",
                _          => $"\n    return null;",
            };

            var src =
                $"public {op.ReturnType} {op.Name}({contractParam})\n" +
                "{\n" +
                "    // TODO: implement" +
                (op.ReturnType == "void" ? "" : defaultReturn) + "\n" +
                "}\n";

            return new XElement("Method",
                new XElement("Name", op.Name),
                new XElement("Source", src));
        }).ToList();

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods", methods))));
    }

    /// <summary>
    /// Scaffolds the <c>AxService</c> XML binding the service class to its operations.
    /// </summary>
    public static XDocument ServiceXml(
        string serviceName,
        string serviceClass,
        IReadOnlyList<OperationSpec> operations)
    {
        var ops = (operations ?? Array.Empty<OperationSpec>()).ToList();
        if (ops.Count == 0)
            ops.Add(new OperationSpec("process", "void"));

        var operationEls = ops.Select(op =>
            new XElement("AxServiceOperation",
                new XElement("Name", op.Name),
                new XElement("Method", op.Name)));

        return new XDocument(
            new XElement("AxService",
                new XElement("Name", serviceName),
                new XElement("Class", serviceClass),
                new XElement("ServiceOperations", operationEls)));
    }

    /// <summary>
    /// Scaffolds the <c>AxServiceGroup</c> XML that groups services together.
    /// </summary>
    public static XDocument ServiceGroupXml(string groupName, string serviceName)
    {
        return new XDocument(
            new XElement("AxServiceGroup",
                new XElement("Name", groupName),
                new XElement("Services",
                    new XElement("AxServiceGroupService",
                        new XElement("Name", serviceName),
                        new XElement("Service", serviceName)))));
    }
}
