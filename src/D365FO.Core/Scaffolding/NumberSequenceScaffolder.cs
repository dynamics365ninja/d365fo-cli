using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public enum NumberSequenceScope { Company, Shared }

/// <summary>
/// Scaffolds the three-part NumberSeq integration pattern:
/// a CoC extension on the module class, the EDT, and a form event-handler.
/// </summary>
public static class NumberSequenceScaffolder
{
    /// <summary>
    /// Scaffolds a CoC extension of the per-module <c>NumberSeqApplicationModule_&lt;ModuleName&gt;</c>
    /// class that registers a new number sequence reference in <c>loadModule()</c>.
    /// </summary>
    public static XDocument ModuleExtension(
        string moduleName,
        string edtName,
        NumberSequenceScope scope = NumberSequenceScope.Company)
    {
        var targetClass   = $"NumberSeqApplicationModule_{moduleName}";
        var extensionName = targetClass + "_Extension";

        var declaration =
            $"[ExtensionOf(classStr({targetClass}))]\n" +
            $"final class {extensionName}\n" +
            "{\n" +
            "}\n";

        var scopeLine = scope == NumberSequenceScope.Shared
            ? "    datatype.addParameterType(NumberSeqParameterType::DataArea, false, false);"
            : "    datatype.addParameterType(NumberSeqParameterType::DataArea, true, false);";

        var loadModuleSrc =
            "public void loadModule()\n" +
            "{\n" +
            "    next loadModule();\n" +
            "\n" +
            "    NumberSeqDatatype datatype = NumberSeqDatatype::construct();\n" +
            $"    datatype.parmDatatypeId(extendedTypeNum({edtName}));\n" +
            $"    datatype.parmReferenceHelp(literalStr(\"{edtName}\"));\n" +
            "    datatype.parmWizardIsContinuous(false);\n" +
            "    datatype.parmWizardIsManual(false);\n" +
            "    datatype.parmWizardIsChangeDownAllowed(false);\n" +
            "    datatype.parmWizardIsChangeUpAllowed(false);\n" +
            "    datatype.parmWizardHighest(999999);\n" +
            scopeLine + "\n" +
            "    this.create(datatype);\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", extensionName),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "loadModule"),
                            new XElement("Source", loadModuleSrc))))));
    }

    /// <summary>
    /// Scaffolds an EDT that is backed by a number sequence. Carries the same
    /// <c>xmlns:i</c> / <c>i:type="AxEdtString"</c> root discriminator as
    /// <see cref="XppScaffolder.Edt"/> — D365FO's metadata reader (and
    /// <see cref="ScaffoldFileWriter"/>'s own write-time gate) reject an
    /// <c>AxEdtString</c> root without it.
    /// </summary>
    public static XDocument Edt(
        string edtName,
        string moduleName,
        NumberSequenceScope scope = NumberSequenceScope.Company,
        string? label = null)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        return new XDocument(
            new XElement("AxEdtString",
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XAttribute(XName.Get("type", xsi.NamespaceName), "AxEdtString"),
                new XElement("Name", edtName),
                new XElement("Extends", "Num"),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("NumberSequenceModule", moduleName)));
    }

    /// <summary>
    /// Scaffolds a form event-handler class that wires <c>NumberSeqFormHandler</c>
    /// into the form's <c>Initialized</c> event so the field auto-generates its value.
    /// </summary>
    public static XDocument FormHandler(string tableName, string edtName, string className)
    {
        var declaration =
            $"public static class {className}\n" +
            "{\n" +
            "}\n";

        var handlerMethod = $"{tableName}Form_OnInitialized";

        var initSrc =
            $"[FormEventHandler(formStr({tableName}Form), FormEventType::Initialized)]\n" +
            $"public static void {handlerMethod}(xFormRun _sender, FormEventArgs _e)\n" +
            "{\n" +
            "    FormRun formRun = _sender;\n" +
            $"    formRun.numberSeqFormHandler(\n" +
            $"        NumberSeqFormHandler::newForm(\n" +
            $"            CompanyInfo::numRef{edtName}().NumberSequenceId,\n" +
            "            new FormNumberSeqScope(),\n" +
            $"            formRun.dataSource(tableStr({tableName})),\n" +
            $"            fieldStr({tableName}, {edtName})));\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", handlerMethod),
                            new XElement("Source", initSrc))))));
    }
}
