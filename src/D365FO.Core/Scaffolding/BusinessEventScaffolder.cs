using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public sealed record PayloadSpec(string Name, string Type);

/// <summary>
/// Scaffolds the business event class + companion contract pattern for D365FO
/// custom business events. Generates two files: the event class (extends
/// <c>BusinessEventsBase</c>) and the data contract class (implements
/// <c>BusinessEventsContract</c>).
/// </summary>
public static class BusinessEventScaffolder
{
    /// <summary>
    /// Scaffolds the <c>AxClass</c> for the business event itself.
    /// </summary>
    public static XDocument EventClass(
        string className,
        string contractName,
        string category,
        string? primaryTable = null)
    {
        var tableParam   = string.IsNullOrWhiteSpace(primaryTable) ? "table" : LowerFirst(primaryTable);
        var tableType    = string.IsNullOrWhiteSpace(primaryTable) ? "Common" : primaryTable;

        var newFromTableSrc =
            $"public static {className} newFromTable({tableType} _{tableParam})\n" +
            "{\n" +
            $"    {className} event = new {className}();\n" +
            $"    event.parmId(classStr({className}));\n" +
            "    return event;\n" +
            "}\n";

        var buildContractSrc =
            "[Wrappable(false), Replaceable(false)]\n" +
            "public BusinessEventsContract buildContract()\n" +
            "{\n" +
            $"    return new {contractName}();\n" +
            "}\n";

        var declaration =
            $"[BusinessEvents(classStr({className}), classStr({contractName}), \"{category}\", \"{category}\")]\n" +
            $"public final class {className} extends BusinessEventsBase\n" +
            "{\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "newFromTable"),
                            new XElement("Source", newFromTableSrc)),
                        new XElement("Method",
                            new XElement("Name", "buildContract"),
                            new XElement("Source", buildContractSrc))))));
    }

    /// <summary>
    /// Scaffolds the <c>AxClass</c> for the business events contract.
    /// </summary>
    public static XDocument ContractClass(
        string contractName,
        IReadOnlyList<PayloadSpec>? payload = null)
    {
        var fields = (payload ?? Array.Empty<PayloadSpec>()).ToList();

        var memberDecls = fields.Count > 0
            ? string.Join("\n", fields.Select(p => $"    {p.Type} {LowerFirst(p.Name)};")) + "\n"
            : "";

        var declaration =
            "[DataContractAttribute]\n" +
            $"public final class {contractName} implements BusinessEventsContract\n" +
            "{\n" +
            memberDecls +
            "}\n";

        var methods = fields.Select(p =>
        {
            var member = LowerFirst(p.Name);
            var src =
                $"[DataMember]\n" +
                $"public {p.Type} parm{p.Name}({p.Type} _{member} = {member})\n" +
                "{\n" +
                $"    {member} = _{member};\n" +
                $"    return {member};\n" +
                "}\n";
            return new XElement("Method",
                new XElement("Name", $"parm{p.Name}"),
                new XElement("Source", src));
        }).ToList();

        var sourceEl = new XElement("SourceCode",
            new XElement("Declaration", declaration));
        if (methods.Count > 0)
            sourceEl.Add(new XElement("Methods", methods));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", contractName),
                sourceEl));
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
