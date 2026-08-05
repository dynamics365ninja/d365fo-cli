using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public sealed record DialogParamSpec(string Name, string Edt);

/// <summary>
/// Scaffolds a legacy <c>RunBase</c> / <c>RunBaseBatch</c> class for D365FO.
/// Emits <c>dialog()</c>, <c>getFromDialog()</c>, <c>pack()</c>, <c>unpack()</c>,
/// and optionally <c>canGoBatch()</c> for batch-enabled classes.
/// </summary>
public static class RunBaseScaffolder
{
    /// <summary>
    /// Scaffolds an <c>AxClass</c> extending <c>RunBase</c> or <c>RunBaseBatch</c>.
    /// </summary>
    public static XDocument RunBaseClass(
        string className,
        bool isBatch,
        IReadOnlyList<DialogParamSpec>? dialogParams = null)
    {
        var baseClass = isBatch ? "RunBaseBatch" : "RunBase";
        var parms     = (dialogParams ?? Array.Empty<DialogParamSpec>()).ToList();

        // Declaration: dialog field members + packing version constant
        var dialogFieldDecls = parms.Count > 0
            ? string.Join("\n", parms.Select(p => $"    DialogField dialog{p.Name};")) + "\n    "
            : "    ";

        var declaration =
            $"public class {className} extends {baseClass}\n" +
            "{\n" +
            $"    {dialogFieldDecls}// Packing version constant\n" +
            "    private static int currentVersion = 1;\n" +
            "}\n";

        // dialog() method
        var dialogAddFields = parms.Count > 0
            ? string.Join("\n", parms.Select(p =>
                $"        dialog{p.Name} = dialog.addFieldValue(extendedTypeStr({p.Edt}), {p.Edt}::defaultValue());")) + "\n"
            : "";

        var dialogSrc =
            "public Object dialog()\n" +
            "{\n" +
            "    DialogRunbase dialog = super();\n" +
            dialogAddFields +
            "    return dialog;\n" +
            "}\n";

        // getFromDialog() method
        var getFromDialogAssignments = parms.Count > 0
            ? string.Join("\n", parms.Select(p =>
                $"        // {LowerFirst(p.Name)} = dialog{p.Name}.value();")) + "\n"
            : "";

        var getFromDialogSrc =
            "public boolean getFromDialog()\n" +
            "{\n" +
            getFromDialogAssignments +
            "    return super();\n" +
            "}\n";

        // pack() method
        var packFieldsList = parms.Count > 0
            ? ", " + string.Join(", ", parms.Select(p => LowerFirst(p.Name)))
            : "";

        var packSrc =
            "public container pack()\n" +
            "{\n" +
            $"    return [currentVersion{packFieldsList}];\n" +
            "}\n";

        // unpack() method
        var unpackFieldsList = parms.Count > 0
            ? ", " + string.Join(", ", parms.Select(p => LowerFirst(p.Name)))
            : "";

        var unpackSrc =
            "public boolean unpack(container _packedClass)\n" +
            "{\n" +
            "    int version = RunBase::getVersion(_packedClass);\n" +
            "    if (version == currentVersion)\n" +
            "    {\n" +
            $"        [version{unpackFieldsList}] = _packedClass;\n" +
            "        return true;\n" +
            "    }\n" +
            "    return false;\n" +
            "}\n";

        // run() method
        var runSrc =
            "public void run()\n" +
            "{\n" +
            "    // TODO: implement\n" +
            "}\n";

        // main() method
        var mainSrc =
            "public static void main(Args _args)\n" +
            "{\n" +
            $"    {className} runObject = new {className}();\n" +
            "    if (runObject.prompt())\n" +
            "        runObject.runOperationNow();\n" +
            "}\n";

        var methodElements = new List<XElement>
        {
            new XElement("Method", new XElement("Name", "dialog"),        new XElement("Source", dialogSrc)),
            new XElement("Method", new XElement("Name", "getFromDialog"), new XElement("Source", getFromDialogSrc)),
            new XElement("Method", new XElement("Name", "pack"),          new XElement("Source", packSrc)),
            new XElement("Method", new XElement("Name", "unpack"),        new XElement("Source", unpackSrc)),
        };

        if (isBatch)
        {
            var canGoBatchSrc =
                "public boolean canGoBatch()\n" +
                "{\n" +
                "    return true;\n" +
                "}\n";
            methodElements.Add(new XElement("Method", new XElement("Name", "canGoBatch"), new XElement("Source", canGoBatchSrc)));
        }

        methodElements.Add(new XElement("Method", new XElement("Name", "run"),  new XElement("Source", runSrc)));
        methodElements.Add(new XElement("Method", new XElement("Name", "main"), new XElement("Source", mainSrc)));

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods", methodElements))));
    }

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLower(s[0]) + s[1..];
}
