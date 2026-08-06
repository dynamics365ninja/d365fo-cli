using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

public enum MigrationMode { Insert, Update, Upsert }

/// <summary>
/// Scaffolds a data-migration <c>SysRunnable</c> class for D365FO.
/// Uses <c>doInsert</c> / <c>doUpdate</c> (the documented exception to the
/// &quot;never bypass ORM&quot; rule) with configurable batch-commit intervals
/// and progress logging.
/// </summary>
public static class MigrationScriptScaffolder
{
    /// <summary>
    /// Scaffolds one <c>AxClass</c> extending <c>SysRunnable</c> with the
    /// batch-safe migration pattern.
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
            $"/// Run once via Batch or SysRunnable; uses doInsert/doUpdate (permitted exception).\n" +
            $"/// </summary>\n" +
            $"public class {className} extends SysRunnable\n" +
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
