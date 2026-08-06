using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Regression cover for the data-migration scaffolder, added with the defect the knowledge
/// audit found: the class used to be emitted as <c>extends SysRunnable</c>, and no
/// <c>SysRunnable</c> type exists in any AOT. A D365FO runnable class is a plain class with a
/// static <c>main(Args)</c> — there is no base to derive from.
/// </summary>
public class MigrationScriptScaffolderTests
{
    private static (string Declaration, string Run, string Main) Sources(MigrationMode mode = MigrationMode.Insert)
    {
        var doc = MigrationScriptScaffolder.MigrationClass("FmVehicleMigration", "FmVehicleOld", "FmVehicle", mode);
        var source = doc.Root!.Element("SourceCode")!;
        var methods = source.Element("Methods")!.Elements("Method").ToDictionary(
            m => m.Element("Name")!.Value, m => m.Element("Source")!.Value);
        return (source.Element("Declaration")!.Value, methods["run"], methods["main"]);
    }

    [Fact]
    public void Runnable_class_derives_from_nothing()
    {
        var (declaration, _, main) = Sources();

        Assert.DoesNotContain("extends", declaration, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public class FmVehicleMigration", declaration);
        Assert.Contains("public static void main(Args _args)", main);
    }

    [Theory]
    [InlineData(MigrationMode.Insert, "target.doInsert();")]
    [InlineData(MigrationMode.Update, "target.doUpdate();")]
    [InlineData(MigrationMode.Upsert, "if (target.RecId) target.doUpdate(); else target.doInsert();")]
    public void Emits_the_write_call_for_each_mode(MigrationMode mode, string expected)
    {
        var (_, run, _) = Sources(mode);
        Assert.Contains(expected, run);
    }

    [Fact]
    public void Commits_in_batches_around_the_cursor()
    {
        var (declaration, run, _) = Sources();

        Assert.Contains("private static int BatchSize = 1000;", declaration);
        Assert.Contains("ttsbegin;", run);
        Assert.Contains("if (count mod BatchSize == 0)", run);
        Assert.Contains("ttscommit;", run);
    }
}
