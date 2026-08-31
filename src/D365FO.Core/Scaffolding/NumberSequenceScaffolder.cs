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
                    new XElement("Declaration", new XCData(declaration)),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "loadModule"),
                            new XElement("Source", new XCData(loadModuleSrc)))))));
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
    /// Scaffolds a form event-handler class that wires <c>NumberSeqFormHandler</c> into the
    /// form's datasource events so the field auto-generates its value.
    /// </summary>
    /// <remarks>
    /// The emitted shape follows the corpus (number-sequence-patterns §3), adapted to the
    /// event-handler route for a form the caller may not own:
    /// <list type="bullet">
    /// <item><c>newForm</c>'s arguments are (RefRecId from <c>.NumberSequenceId</c>, the
    /// <b>FormRun</b>, the <b>FormDataSource</b>, <c>fieldNum(...)</c>) — the earlier template
    /// passed a scope object for the FormRun and <c>fieldStr</c> for the FieldId, neither of
    /// which compiles.</item>
    /// <item>The <c>numRef&lt;Edt&gt;()</c> accessor lives on the <b>module's parameters
    /// table</b> — never on <c>CompanyInfo</c> (the corpus says so in as many words).</item>
    /// <item>The handler must drive the datasource's create/write/delete cycle, not a one-time
    /// call in an init handler — one <c>NumberSeqFormHandler</c> per open form instance, held
    /// in a Map keyed by the FormRun object and released when the form closes.</item>
    /// </list>
    /// </remarks>
    public static XDocument FormHandler(string tableName, string edtName, string className, string? moduleName = null)
    {
        // The module's parameters table (CustParameters, FMParameters, …) owns the
        // numRef accessor. The conventional name is <Module>Parameters; the caller
        // must repoint it when the module spells its table differently.
        var parametersTable = (moduleName ?? tableName) + "Parameters";
        var formName = tableName + "Form";

        var declaration =
            $"public static class {className}\n" +
            "{\n" +
            "    // One NumberSeqFormHandler per open form instance, keyed by the FormRun\n" +
            "    // object itself. Released in the Closing handler below.\n" +
            "    private static Map formHandlers = new Map(Types::Class, Types::Class);\n" +
            "}\n";

        string HandlerSource(string method, string eventType, string call) =>
            $"[FormDataSourceEventHandler(formDataSourceStr({formName}, {tableName}), FormDataSourceEventType::{eventType})]\n" +
            $"public static void {method}(FormDataSource _sender, FormDataSourceEventArgs _e)\n" +
            "{\n" +
            $"    {className}::formHandler(_sender).{call}();\n" +
            "}\n";

        var lookupSrc =
            "/// <summary>\n" +
            $"/// Lazily builds the handler that drives auto-numbering of {tableName}.{edtName}.\n" +
            "/// </summary>\n" +
            "private static NumberSeqFormHandler formHandler(FormDataSource _ds)\n" +
            "{\n" +
            "    FormRun formRun = _ds.formRun();\n\n" +
            "    if (!formHandlers.exists(formRun))\n" +
            "    {\n" +
            "        formHandlers.insert(formRun, NumberSeqFormHandler::newForm(\n" +
            $"            {parametersTable}::numRef{edtName}().NumberSequenceId, // RefRecId, not a string\n" +
            "            formRun,\n" +
            "            _ds,\n" +
            $"            fieldNum({tableName}, {edtName})));\n" +
            "    }\n\n" +
            "    return formHandlers.lookup(formRun);\n" +
            "}\n";

        var closeSrc =
            $"[FormEventHandler(formStr({formName}), FormEventType::Closing)]\n" +
            $"public static void {formName}_OnClosing(xFormRun _sender, FormEventArgs _e)\n" +
            "{\n" +
            "    if (formHandlers.exists(_sender))\n" +
            "    {\n" +
            "        formHandlers.remove(_sender);\n" +
            "    }\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", new XCData(declaration)),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "formHandler"),
                            new XElement("Source", new XCData(lookupSrc))),
                        new XElement("Method",
                            new XElement("Name", $"{tableName}_OnCreating"),
                            new XElement("Source", new XCData(HandlerSource($"{tableName}_OnCreating", "Creating", "formMethodDataSourceCreatePre")))),
                        new XElement("Method",
                            new XElement("Name", $"{tableName}_OnCreated"),
                            new XElement("Source", new XCData(HandlerSource($"{tableName}_OnCreated", "Created", "formMethodDataSourceCreate")))),
                        new XElement("Method",
                            new XElement("Name", $"{tableName}_OnWritten"),
                            new XElement("Source", new XCData(HandlerSource($"{tableName}_OnWritten", "Written", "formMethodDataSourceWrite")))),
                        new XElement("Method",
                            new XElement("Name", $"{tableName}_OnDeleting"),
                            new XElement("Source", new XCData(HandlerSource($"{tableName}_OnDeleting", "Deleting", "formMethodDataSourceDelete")))),
                        new XElement("Method",
                            new XElement("Name", $"{formName}_OnClosing"),
                            new XElement("Source", new XCData(closeSrc)))))));
    }
}
