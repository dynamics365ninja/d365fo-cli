using D365FO.Core.Eval;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The measurements that judge the tool rather than what it produces.
/// </summary>
/// <remarks>
/// These run without an installation: they exercise the machinery over a hand-built packages
/// tree. What they cannot prove is the thing the sweep exists for — that the rules are silent on
/// Microsoft's code — because that needs Microsoft's code. That claim is made by running
/// <c>oracle sweep</c> on a host that has an installation, and is recorded in the wave notes.
/// </remarks>
public class OracleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-oracle-{Guid.NewGuid():N}");

    public OracleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Lay a file down in packages shape: &lt;root&gt;/&lt;package&gt;/&lt;model&gt;/Ax*/&lt;name&gt;.xml.</summary>
    private string Write(string model, string axFolder, string name, string xml)
    {
        var dir = Path.Combine(_root, model, model, axFolder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".xml");
        File.WriteAllText(path, xml);
        return path;
    }

    private const string CleanTable = """
        <?xml version="1.0" encoding="utf-8"?>
        <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>ConCleanTable</Name>
          <Label>@ConFleet:Label</Label>
          <TableGroup>Main</TableGroup>
          <Fields><AxTableField i:type="AxTableFieldString"><Name>ConField</Name><ExtendedDataType>Name</ExtendedDataType></AxTableField></Fields>
          <Indexes><AxTableIndex><Name>ConIdx</Name><AlternateKey>Yes</AlternateKey></AxTableIndex></Indexes>
        </AxTable>
        """;

    // ── sweep ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_sweep_walks_the_packages_shape_and_counts_what_it_finds()
    {
        Write("ConFleet", "AxTable", "ConCleanTable", CleanTable);
        Write("ConFleet", "AxClass", "ConClass", """
            <?xml version="1.0" encoding="utf-8"?>
            <AxClass><Name>ConClass</Name><SourceCode><Declaration><![CDATA[class ConClass
            {
            }
            ]]></Declaration></SourceCode></AxClass>
            """);

        var report = OracleSweep.Run(_root, new OracleSweep.Options(IncludeWarnings: true));

        Assert.Equal(2, report.FilesScanned);
        Assert.Equal(1, report.XppBlocksScanned);   // only the class carries a Declaration
        Assert.Equal(0, report.FilesUnreadable);
    }

    /// <summary>
    /// The bar is errors, not findings: a warning is a style opinion that shipped code is allowed
    /// to disagree with, and folding the two together would make the bar unmeetable and therefore
    /// ignored.
    /// </summary>
    [Fact]
    public void Warnings_do_not_break_the_bar_and_errors_do()
    {
        // No alternate key: XML001, a warning.
        Write("ConFleet", "AxTable", "ConNoKey", """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>ConNoKey</Name><Label>@ConFleet:L</Label><TableGroup>Main</TableGroup>
              <Fields><AxTableField i:type="AxTableFieldString"><Name>F</Name><ExtendedDataType>Name</ExtendedDataType></AxTableField></Fields>
            </AxTable>
            """);

        var report = OracleSweep.Run(_root, new OracleSweep.Options(IncludeWarnings: true));

        Assert.True(report.Warnings > 0);
        Assert.Equal(0, report.Errors);
        Assert.True(report.BarHeld);
    }

    [Fact]
    public void An_unreadable_file_is_counted_rather_than_swallowed()
    {
        var path = Write("ConFleet", "AxTable", "ConBroken", "<AxTable><Name>ConBroken</Name>");  // truncated
        Assert.True(File.Exists(path));

        var report = OracleSweep.Run(_root, new OracleSweep.Options(IncludeWarnings: true));

        // It is read as text without trouble; it is the PARSE that fails, and the rules that need
        // a document simply say nothing. What must not happen is a crash or a silent skip.
        Assert.Equal(1, report.FilesScanned);
    }

    [Fact]
    public void Samples_are_bounded_so_the_sweep_costs_the_same_however_wrong_the_rules_are()
    {
        for (var i = 0; i < 12; i++)
            Write("ConFleet", "AxTable", $"ConNoKey{i}", CleanTable.Replace("ConCleanTable", $"ConNoKey{i}")
                .Replace("<Indexes><AxTableIndex><Name>ConIdx</Name><AlternateKey>Yes</AlternateKey></AxTableIndex></Indexes>", ""));

        var report = OracleSweep.Run(_root, new OracleSweep.Options(SamplesPerRule: 2, IncludeWarnings: true));

        Assert.All(report.ByRule, r => Assert.True(r.Samples.Count <= 2));
        Assert.Contains(report.ByRule, r => r.Count > 2);   // counted beyond what was kept
    }

    [Fact]
    public void A_model_filter_narrows_the_sweep()
    {
        Write("ConFleet", "AxTable", "ConA", CleanTable);
        Write("ConOther", "AxTable", "ConB", CleanTable.Replace("ConCleanTable", "ConB"));

        Assert.Equal(2, OracleSweep.Run(_root).FilesScanned);
        Assert.Equal(1, OracleSweep.Run(_root, new OracleSweep.Options(Model: "ConOther")).FilesScanned);
        Assert.Equal(2, OracleSweep.Run(_root, new OracleSweep.Options(AxFolder: "AxTable")).FilesScanned);
        Assert.Equal(0, OracleSweep.Run(_root, new OracleSweep.Options(AxFolder: "AxForm")).FilesScanned);
    }

    // ── census ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_census_counts_members_and_the_files_that_carry_them()
    {
        Write("ConFleet", "AxTable", "ConA", CleanTable);
        Write("ConFleet", "AxTable", "ConB", """
            <?xml version="1.0" encoding="utf-8"?>
            <AxTable><Name>ConB</Name><TableGroup>Transaction</TableGroup></AxTable>
            """);

        var census = OracleCensus.Run(_root, "AxTable");

        Assert.Equal(2, census.FilesScanned);

        var name = census.Members.Single(m => m.Member == "Name");
        Assert.Equal(2, name.Files);

        var label = census.Members.Single(m => m.Member == "Label");
        Assert.Equal(1, label.Files);

        // Leaf values are sampled, so a property rule can be built on what the values really are.
        var group = census.Members.Single(m => m.Member == "TableGroup");
        Assert.Contains("Main", group.SampleValues);
        Assert.Contains("Transaction", group.SampleValues);
    }

    /// <summary>
    /// The measurement that refuses an order rule. Two documents writing the same pair of members
    /// in opposite orders is a counter-example, and one is enough.
    /// </summary>
    [Fact]
    public void An_order_seen_both_ways_is_reported_as_unstable()
    {
        Write("ConFleet", "AxTable", "ConA", "<AxTable><Name>ConA</Name><Label>x</Label></AxTable>");
        Write("ConFleet", "AxTable", "ConB", "<AxTable><Label>y</Label><Name>ConB</Name></AxTable>");

        var census = OracleCensus.Run(_root, "AxTable");

        Assert.False(census.OrderIsStable);
        Assert.NotEmpty(census.OrderCounterExamples);
    }

    [Fact]
    public void One_consistent_order_is_reported_as_stable()
    {
        Write("ConFleet", "AxTable", "ConA", "<AxTable><Name>ConA</Name><Label>x</Label></AxTable>");
        Write("ConFleet", "AxTable", "ConB", "<AxTable><Name>ConB</Name><Label>y</Label></AxTable>");

        Assert.True(OracleCensus.Run(_root, "AxTable").OrderIsStable);
    }

    [Fact]
    public void A_member_the_contract_does_not_declare_is_reported_as_drift()
    {
        Write("ConFleet", "AxTable", "ConA",
            "<AxTable><Name>ConA</Name><ConNoSuchMember>x</ConNoSuchMember></AxTable>");

        var census = OracleCensus.Run(_root, "AxTable");

        Assert.Contains("ConNoSuchMember", census.SeenNotDeclared);
        Assert.DoesNotContain("Name", census.SeenNotDeclared);
    }

    // ── probe ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The three rules the hand-run recipe kept getting wrong: the folder comes from the root
    /// element, the file name from the declared &lt;Name&gt;, and anything that is not an AOT
    /// document is refused rather than copied somewhere the compiler will trip over it.
    /// </summary>
    [Fact]
    public void The_probe_places_an_artefact_where_the_compiler_expects_it()
    {
        var source = Path.Combine(_root, "written-under-another-name.xml");
        File.WriteAllText(source, """
            <?xml version="1.0" encoding="utf-8"?>
            <AxClass><Name>ConProbeClass</Name><SourceCode><Declaration><![CDATA[class ConProbeClass
            {
            }
            ]]></Declaration></SourceCode></AxClass>
            """);

        var work = Path.Combine(_root, "work");
        var prep = OracleProbe.Prepare([source], work, "ConProbeModel", withFixture: false);

        var placed = Assert.Single(prep.Placed);
        Assert.Equal("ConProbeClass", placed.ObjectName);
        Assert.Equal("AxClass", placed.AxFolder);
        Assert.EndsWith(Path.Combine("AxClass", "ConProbeClass.xml"), placed.Placed, StringComparison.Ordinal);
        Assert.True(File.Exists(placed.Placed));
        Assert.True(File.Exists(Path.Combine(work, "ConProbeModel", "Descriptor", "ConProbeModel.xml")));
    }

    [Theory]
    [InlineData("<notaot><Name>X</Name></notaot>", "not an AOT element")]
    [InlineData("<AxClass></AxClass>", "declares no <Name>")]
    [InlineData("<AxClass><Name>X</Name>", "not readable XML")]
    public void Something_that_is_not_a_usable_document_is_refused_with_the_reason(string xml, string reason)
    {
        var source = Path.Combine(_root, "candidate.xml");
        File.WriteAllText(source, xml);

        var prep = OracleProbe.Prepare([source], Path.Combine(_root, "work"), "ConProbeModel", withFixture: false);

        Assert.Empty(prep.Placed);
        var rejected = Assert.Single(prep.Rejected);
        Assert.Contains(reason, rejected.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_file_is_refused_rather_than_reported_as_compiled()
    {
        var prep = OracleProbe.Prepare(
            [Path.Combine(_root, "absent.xml")], Path.Combine(_root, "work"), "ConProbeModel", withFixture: false);

        Assert.Empty(prep.Placed);
        Assert.Contains(prep.Rejected, r => r.Reason.Contains("no such file", StringComparison.Ordinal));
    }

    // ── runtime ────────────────────────────────────────────────────────────

    [Fact]
    public void An_unconfigured_runner_is_reported_as_unconfigured()
    {
        var bin = Path.Combine(_root, "packages", "bin");
        Directory.CreateDirectory(bin);
        File.WriteAllText(Path.Combine(bin, "SysTestConsole.exe"), "");
        File.WriteAllText(Path.Combine(bin, "SysTestConsole.exe.config"), """
            <configuration><appSettings>
              <add key="DataAccess.Database" value="" />
            </appSettings></configuration>
            """);

        var diagnosis = RuntimeOracle.Diagnose(Path.Combine(_root, "packages"));

        Assert.True(diagnosis.RunnerPresent);
        Assert.False(diagnosis.Configured);
        Assert.Contains("DataAccess.Database", diagnosis.Missing);   // present but empty still counts as missing
        Assert.Contains("DataAccess.DbServer", diagnosis.Missing);
    }

    [Fact]
    public void Configuring_copies_only_what_is_missing_and_keeps_the_original()
    {
        var bin = Path.Combine(_root, "packages", "bin");
        Directory.CreateDirectory(bin);
        var runnerConfig = Path.Combine(bin, "SysTestConsole.exe.config");
        File.WriteAllText(Path.Combine(bin, "SysTestConsole.exe"), "");
        File.WriteAllText(runnerConfig, """
            <configuration><appSettings>
              <add key="DataAccess.Database" value="AlreadySet" />
            </appSettings></configuration>
            """);

        var web = Path.Combine(_root, "web.config");
        File.WriteAllText(web, """
            <configuration><appSettings>
              <add key="DataAccess.Database" value="FromWeb" />
              <add key="DataAccess.DbServer" value="SERVER1" />
              <add key="DataAccess.SqlUser" value="axdbadmin" />
            </appSettings></configuration>
            """);

        var result = RuntimeOracle.Configure(runnerConfig, web);

        // The one already set is left alone: this is a repair, not an overwrite.
        Assert.DoesNotContain("DataAccess.Database", result.Written);
        Assert.Contains("DataAccess.DbServer", result.Written);
        Assert.Contains("DataAccess.SqlUser", result.Written);
        // And what the web.config does not carry is not invented.
        Assert.Contains("DataAccess.SqlPwd", result.Unavailable);

        Assert.True(File.Exists(result.BackupPath));
        var written = File.ReadAllText(runnerConfig);
        Assert.Contains("AlreadySet", written, StringComparison.Ordinal);
        Assert.Contains("SERVER1", written, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control has to contain all three outcomes, or it cannot tell a runner that discriminates
    /// from one that reports everything the same way.
    /// </summary>
    [Fact]
    public void The_negative_control_passes_fails_and_throws_on_purpose()
    {
        var source = RuntimeOracle.NegativeControlSource("ConControlTest");

        Assert.Equal(3, source.Split("[SysTestMethodAttribute]").Length - 1);
        Assert.Contains("class ConControlTest extends SysTestCase", source, StringComparison.Ordinal);
        Assert.Contains("assertEquals(1, 1)", source, StringComparison.Ordinal);
        Assert.Contains("assertEquals(1, 2", source, StringComparison.Ordinal);
        Assert.Contains("throw error(", source, StringComparison.Ordinal);
    }
}
