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
    /// Values of the <c>ModuleAxapta</c> enum — the fourth argument of
    /// <c>[BusinessEvents]</c>. Ground-truthed against
    /// <c>ApplicationPlatform/AxEnum/ModuleAxapta.xml</c> on a real installation
    /// (40 values). A value outside this set is a compile error, so the caller is
    /// told which ones exist rather than being let through to find out on the AOS.
    /// </summary>
    public static readonly IReadOnlyList<string> ModuleAxaptaValues =
    [
        "Ledger", "Bank", "SalesOrder", "Customer", "PurchaseOrder", "Vendor", "Inventory",
        "BOM", "Route", "WorkCenter", "Production", "MasterPlanning", "HumanResource", "Project",
        "Location", "General", "Costing", "Expense", "CRM", "Activities", "Contacts",
        "BusinessRelations", "Opportunities", "Leads", "Campaigns", "Basic", "TimeAndAttendance",
        "Budget", "FixedAssets", "RetailTerminal", "RCash", "FleetManagement", "TAM",
        "ProcurementAndSourcing", "NotApplicable", "Obsolete", "SalesAndMarketing",
        "SystemAdministration", "Tax", "ProductInformationManagement",
    ];

    /// <summary>The honest default for a skeleton: the scaffolder does not know the business module.</summary>
    public const string DefaultModule = "NotApplicable";

    /// <summary>Resolves a caller-supplied module name case-insensitively, or null when it is not a real value.</summary>
    public static string? NormalizeModule(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return DefaultModule;
        return ModuleAxaptaValues.FirstOrDefault(v => string.Equals(v, value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Scaffolds the <c>AxClass</c> for the business event itself.
    /// </summary>
    /// <param name="module">
    /// <c>ModuleAxapta</c> value for the attribute's fourth argument. Must be one of
    /// <see cref="ModuleAxaptaValues"/>; anything else throws rather than emitting
    /// code the compiler will reject.
    /// </param>
    /// <remarks>
    /// The attribute shape is read off shipped events
    /// (<c>BenefitAnnualSalaryChangeBusinessEvent</c>,
    /// <c>BackgroundOperationCancelledBusinessEvent</c>): the first argument is the
    /// <em>contract</em> class, then a name and a description, then
    /// <c>ModuleAxapta::&lt;module&gt;</c>. This used to emit the event class as the
    /// first argument, a second <c>classStr</c> where the name belongs, and a plain
    /// string where the enum belongs — <c>"Cannot implicitly convert from type 'str'
    /// to type 'Extensible Enumeration(ModuleAxapta)'"</c>. The factory method also
    /// called <c>parmId()</c>, which exists on no business event: the only parm
    /// method <c>BusinessEventsBase</c> declares is <c>parmUserId</c>. Both found by
    /// <c>eval verify-build</c> on <c>L1-business-event-basic</c>.
    /// </remarks>
    public static XDocument EventClass(
        string className,
        string contractName,
        string module,
        string? primaryTable = null)
    {
        var resolvedModule = NormalizeModule(module)
            ?? throw new ArgumentException(
                $"'{module}' is not a ModuleAxapta value. Valid values: {string.Join(", ", ModuleAxaptaValues)}.",
                nameof(module));

        var hasTable   = !string.IsNullOrWhiteSpace(primaryTable);
        var tableType  = hasTable ? primaryTable! : "Common";
        var member     = LowerFirst(tableType);
        var factory    = hasTable ? $"newFrom{tableType}" : "newFromTable";

        // Shipped events keep the source record in a private member, set through a
        // parm method, and let buildContract() read it — the factory has nothing else
        // to hand the base class.
        var factorySrc =
            $"public static {className} {factory}({tableType} _{member})\n" +
            "{\n" +
            $"    {className} businessEvent = new {className}();\n" +
            $"    businessEvent.parm{tableType}(_{member});\n" +
            "\n" +
            "    return businessEvent;\n" +
            "}\n";

        var parmSrc =
            $"private {tableType} parm{tableType}({tableType} _{member} = {member})\n" +
            "{\n" +
            $"    {member} = _{member};\n" +
            "\n" +
            $"    return {member};\n" +
            "}\n";

        var buildContractSrc =
            "[Wrappable(false), Replaceable(false)]\n" +
            "public BusinessEventsContract buildContract()\n" +
            "{\n" +
            $"    return new {contractName}();\n" +
            "}\n";

        var declaration =
            $"[BusinessEvents(classStr({contractName}), '{className}', '{className}', ModuleAxapta::{resolvedModule})]\n" +
            $"public final class {className} extends BusinessEventsBase\n" +
            "{\n" +
            $"    private {tableType} {member};\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", factory),
                            new XElement("Source", factorySrc)),
                        new XElement("Method",
                            new XElement("Name", $"parm{tableType}"),
                            new XElement("Source", parmSrc)),
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
            // extends, not implements: BusinessEventsContract is a class. Declaring it
            // in an implements list fails with "must designate an interface", and the
            // buildContract() return then fails to convert as well.
            $"public class {contractName} extends BusinessEventsContract\n" +
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
