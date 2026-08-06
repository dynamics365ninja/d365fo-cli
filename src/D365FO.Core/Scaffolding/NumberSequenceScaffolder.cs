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
    /// The AOT name of the module class a number sequence attaches to.
    /// </summary>
    /// <remarks>
    /// The convention is <c>NumberSeqModule&lt;Module&gt;</c> — <c>NumberSeqModuleAsset</c>,
    /// <c>NumberSeqModuleCustomer</c>, <c>NumberSeqModuleBank</c>. This scaffolder used
    /// to target <c>NumberSeqApplicationModule_&lt;Module&gt;</c>, which is the name of
    /// the abstract base class with a suffix bolted on; no such class exists in any
    /// module, so every scaffold it produced failed with "ExtendsOf attribute
    /// specification is invalid", and the <c>next</c> call then failed a second time
    /// because a class that extends nothing has nothing to chain to.
    /// </remarks>
    public static string ModuleClassName(string moduleName) =>
        moduleName.StartsWith("NumberSeqModule", StringComparison.Ordinal)
            ? moduleName
            : "NumberSeqModule" + moduleName;

    /// <summary>
    /// Scaffolds a CoC extension of the per-module <c>NumberSeqModule&lt;ModuleName&gt;</c>
    /// class that registers a new number sequence reference in <c>loadModule()</c>.
    /// </summary>
    /// <remarks>
    /// The target must already exist: a number sequence hangs off an existing module,
    /// and creating a genuinely new one means a new <c>NumberSeqModule</c> enum value,
    /// which is not what this command does.
    /// </remarks>
    public static XDocument ModuleExtension(
        string moduleName,
        string edtName,
        NumberSequenceScope scope = NumberSequenceScope.Company)
    {
        var targetClass   = ModuleClassName(moduleName);
        var extensionName = targetClass + "_Extension";

        var declaration =
            $"[ExtensionOf(classStr({targetClass}))]\n" +
            $"final class {extensionName}\n" +
            "{\n" +
            "}\n";

        var scopeLine = scope == NumberSequenceScope.Shared
            ? "    datatype.addParameterType(NumberSeqParameterType::DataArea, false, false);"
            : "    datatype.addParameterType(NumberSeqParameterType::DataArea, true, false);";

        // protected, not public: a CoC method's signature has to match the one it
        // wraps, and NumberSeqApplicationModule::loadModule is protected.
        var loadModuleSrc =
            "protected void loadModule()\n" +
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
    /// Scaffolds the EDT a number sequence issues values for: a plain <c>Num</c>-derived string.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The element is <c>&lt;AxEdt&gt;</c> with the concrete type in <c>i:type</c>, which is what
    /// every shipped EDT and <see cref="XppScaffolder.Edt"/> both write.
    /// </para>
    /// <para>
    /// Nothing here names the module. An EDT has no <c>NumberSequenceModule</c> member — the
    /// association is made in code, by the <c>NumberSeqApplicationModule</c> extension's
    /// <c>loadModule()</c>, which this scaffolder also generates. The element written before was
    /// discarded on read, so the EDT looked wired up and was not.
    /// </para>
    /// </remarks>
    public static XDocument Edt(
        string edtName,
        string moduleName,
        NumberSequenceScope scope = NumberSequenceScope.Company,
        string? label = null)
    {
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        return new XDocument(
            new XElement("AxEdt",
                new XAttribute(XNamespace.Xmlns + "i", xsi.NamespaceName),
                new XAttribute(XName.Get("type", xsi.NamespaceName), "AxEdtString"),
                new XElement("Name", edtName),
                string.IsNullOrEmpty(label) ? null : new XElement("Label", label),
                new XElement("Extends", "Num")));
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
