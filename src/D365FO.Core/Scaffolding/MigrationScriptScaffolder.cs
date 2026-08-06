using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public enum MigrationMode { Insert, Update, Upsert }

/// <summary>
/// Scaffolds a data-migration runnable class for D365FO.
/// Uses <c>doInsert</c> / <c>doUpdate</c> (the documented exception to the
/// &quot;never bypass ORM&quot; rule) with configurable batch-commit intervals
/// and progress logging.
/// </summary>
/// <remarks>
/// A D365FO runnable class is a plain <c>AxClass</c> with a static
/// <c>main(Args)</c> entry point — there is no base class to extend. This
/// scaffolder used to emit <c>extends SysRunnable</c>, a type that exists in no
/// AOT; the knowledge audit (<c>d365fo knowledge audit</c>) found it by resolving
/// the corpus that taught it against the real symbol index.
/// </remarks>
public static class MigrationScriptScaffolder
{
    /// <summary>
    /// Scaffolds one <c>AxClass</c> carrying the batch-safe migration pattern
    /// behind a static <c>main(Args)</c> entry point.
    /// </summary>
    public static XDocument MigrationClass(
        string className,
        string sourceTable,
        string targetTable,
        MigrationMode mode = MigrationMode.Insert,
        int batchSize = 1000)
    {
        var modeCode = mode switch
        {
            MigrationMode.Update => "target.doUpdate();",
            MigrationMode.Upsert => "if (target.RecId) target.doUpdate(); else target.doInsert();",
            _                    => "target.doInsert();",
        };

        var declaration =
            $"/// <summary>\n" +
            $"/// Data migration: {sourceTable} → {targetTable}.\n" +
            $"/// Run once as a runnable class (right-click the class, Set as startup object) or\n" +
            $"/// from a batch job; uses doInsert/doUpdate (permitted exception).\n" +
            $"/// </summary>\n" +
            $"public class {className}\n" +
            "{\n" +
            $"    private static int BatchSize = {batchSize};\n" +
            "}\n";

        var runSrc =
            "public void run()\n" +
            "{\n" +
            $"    {sourceTable} source;\n" +
            $"    {targetTable} target;\n" +
            "    int count = 0;\n" +
            "\n" +
            "    ttsbegin;\n" +
            "    while select source\n" +
            "    {\n" +
            "        // TODO: map fields from source to target\n" +
            "        target.clear();\n" +
            "        // target.Field = source.Field;\n" +
            $"        {modeCode}\n" +
            "        count++;\n" +
            "        if (count mod BatchSize == 0)\n" +
            "        {\n" +
            "            ttscommit;\n" +
            "            info(strFmt(\"Migrated %1 records\", count));\n" +
            "            ttsbegin;\n" +
            "        }\n" +
            "    }\n" +
            "    ttscommit;\n" +
            "    info(strFmt(\"Migration complete. Total: %1\", count));\n" +
            "}\n";

        var mainSrc =
            "public static void main(Args _args)\n" +
            "{\n" +
            $"    {className} runObject = new {className}();\n" +
            "    runObject.run();\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", className),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "run"),
                            new XElement("Source", runSrc)),
                        new XElement("Method",
                            new XElement("Name", "main"),
                            new XElement("Source", mainSrc))))));
    }
}
