using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public sealed record SysOperationParamSpec(string Name, string Type);

public enum SysOperationExecutionMode { Synchronous, Asynchronous, ScheduledBatch }

/// <summary>
/// Scaffolds the DataContract + Service + Controller triplet that forms the
/// standard SysOperation pattern for batch jobs and service operations.
/// </summary>
public static class SysOperationScaffolder
{
    public static XDocument Contract(string contractName, IEnumerable<SysOperationParamSpec>? parameters = null)
    {
        var parms = (parameters ?? Enumerable.Empty<SysOperationParamSpec>()).ToList();

        var memberDecls = parms.Count > 0
            ? string.Join("\n", parms.Select(p => $"    {p.Type} {LowerFirst(p.Name)};")) + "\n"
            : "";

        var declaration =
            "[DataContractAttribute]\n" +
            $"class {contractName}\n" +
            "{\n" +
            memberDecls +
            "}\n";

        var methods = parms.Select(p =>
        {
            var member = LowerFirst(p.Name);
            var src =
                $"[DataMemberAttribute('{p.Name}')]\n" +
                $"public {p.Type} parm{p.Name}({p.Type} _{member} = {member})\n" +
                "{\n" +
                $"    {member} = _{member};\n" +
                $"    return {member};\n" +
                "}\n";
            return new XElement("Method",
                new XElement("Name", $"parm{p.Name}"),
                new XElement("Source", new XCData(src)));
        }).ToList();

        var sourceEl = new XElement("SourceCode",
            new XElement("Declaration", new XCData(declaration)));
        if (methods.Count > 0)
            sourceEl.Add(new XElement("Methods", methods));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", contractName),
                sourceEl));
    }

    public static XDocument Service(
        string serviceName,
        string contractName,
        string serviceMethod,
        IEnumerable<SysOperationParamSpec>? parameters = null)
    {
        var parms = (parameters ?? Enumerable.Empty<SysOperationParamSpec>()).ToList();

        var declaration =
            $"class {serviceName} extends SysOperationServiceBase\n" +
            "{\n" +
            "}\n";

        var contractUnpack = parms.Count > 0
            ? string.Join("\n", parms.Select(p =>
                $"    {p.Type} {LowerFirst(p.Name)} = _contract.parm{p.Name}();")) + "\n\n"
            : "";

        var methodSrc =
            $"public void {serviceMethod}({contractName} _contract)\n" +
            "{\n" +
            contractUnpack +
            "    // service logic here\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", serviceName),
                new XElement("SourceCode",
                    new XElement("Declaration", new XCData(declaration)),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", serviceMethod),
                            new XElement("Source", new XCData(methodSrc)))))));
    }

    public static XDocument Controller(
        string controllerName,
        string serviceName,
        string serviceMethod,
        SysOperationExecutionMode mode = SysOperationExecutionMode.Synchronous)
    {
        var modeStr = mode switch
        {
            SysOperationExecutionMode.Asynchronous    => "SysOperationExecutionMode::Asynchronous",
            SysOperationExecutionMode.ScheduledBatch  => "SysOperationExecutionMode::ScheduledBatch",
            _                                         => "SysOperationExecutionMode::Synchronous",
        };

        var declaration =
            $"class {controllerName} extends SysOperationServiceController\n" +
            "{\n" +
            "}\n";

        var newSrc =
            "public void new()\n" +
            "{\n" +
            $"    super(classStr({serviceName}), methodStr({serviceName}, {serviceMethod}), {modeStr});\n" +
            "}\n";

        var mainSrc =
            "public static void main(Args _args)\n" +
            "{\n" +
            $"    {controllerName} controller = new {controllerName}();\n" +
            "    controller.startOperation();\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", controllerName),
                new XElement("SourceCode",
                    new XElement("Declaration", new XCData(declaration)),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "new"),
                            new XElement("Source", new XCData(newSrc))),
                        new XElement("Method",
                            new XElement("Name", "main"),
                            new XElement("Source", new XCData(mainSrc)))))));
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
