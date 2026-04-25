using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// End-to-end coverage for the v8 schema additions: extractor reads
/// <c>&lt;Design&gt;&lt;Pattern&gt;</c> from AxForm XML, repository persists
/// pattern columns, and <c>FindFormPatterns</c> / <c>SummarizeFormPatterns</c>
/// honour the <c>--pattern</c> / <c>--table</c> filters used by
/// <c>d365fo find form-patterns</c>.
/// </summary>
public class FormPatternAnalyzerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-fp-{Guid.NewGuid():N}.sqlite");
    private readonly string _workRoot = Path.Combine(Path.GetTempPath(), $"d365fo-fpwork-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
        if (Directory.Exists(_workRoot)) Directory.Delete(_workRoot, recursive: true);
    }

    private void WriteForm(string model, string formName, string xml)
    {
        var dir = Path.Combine(_workRoot, model, model, "AxForm");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, formName + ".xml"), xml);
    }

    [Fact]
    public void Extractor_reads_pattern_and_primary_datasource()
    {
        WriteForm("Fleet", "FleetVehicleList", """
            <AxForm>
              <Name>FleetVehicleList</Name>
              <DataSources>
                <AxFormDataSource>
                  <Name>FleetVehicle</Name>
                  <Table>FleetVehicle</Table>
                </AxFormDataSource>
              </DataSources>
              <Design>
                <Pattern>SimpleList</Pattern>
                <PatternVersion>1.1</PatternVersion>
                <Style>SimpleList</Style>
                <TitleDataSource>FleetVehicle</TitleDataSource>
              </Design>
            </AxForm>
            """);

        var ex = new MetadataExtractor();
        var batch = Assert.Single(ex.ExtractAll(_workRoot).ToList());
        var form = Assert.Single(batch.Forms);
        Assert.Equal("SimpleList", form.Pattern);
        Assert.Equal("1.1", form.PatternVersion);
        Assert.Equal("SimpleList", form.Style);
        Assert.Equal("FleetVehicle", form.TitleDataSource);
        Assert.Equal("FleetVehicle", form.DataSources[0].Table);
    }

    [Fact]
    public void Form_without_design_block_keeps_null_pattern()
    {
        WriteForm("Fleet", "Bare", "<AxForm><Name>Bare</Name></AxForm>");
        var ex = new MetadataExtractor();
        var batch = Assert.Single(ex.ExtractAll(_workRoot).ToList());
        var form = Assert.Single(batch.Forms);
        Assert.Null(form.Pattern);
        Assert.Empty(form.DataSources);
    }

    [Fact]
    public void FindFormPatterns_filters_by_pattern_and_table()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var batch = ExtractBatch.Empty("Fleet") with
        {
            Forms = new[]
            {
                new ExtractedForm("CustGroup", "/p/CustGroup.xml",
                    new[] { new ExtractedFormDataSource("CustGroup", "CustGroup") })
                    { Pattern = "SimpleList", PatternVersion = "1.1" },
                new ExtractedForm("CustTable", "/p/CustTable.xml",
                    new[] { new ExtractedFormDataSource("CustTable", "CustTable") })
                    { Pattern = "DetailsMaster", PatternVersion = "1.2" },
                new ExtractedForm("CustTableListPage", "/p/CustTableListPage.xml",
                    new[] { new ExtractedFormDataSource("CustTable", "CustTable") })
                    { Pattern = "ListPage", PatternVersion = "1.1" },
                new ExtractedForm("Legacy", "/p/Legacy.xml",
                    Array.Empty<ExtractedFormDataSource>())
                    { Pattern = null },
            },
        };
        repo.ApplyExtract(batch);

        // --pattern uses prefix match.
        var simpleList = repo.FindFormPatterns(pattern: "SimpleList");
        Assert.Single(simpleList);
        Assert.Equal("CustGroup", simpleList[0].Name);
        Assert.Equal("CustGroup", simpleList[0].PrimaryTable);

        // --table returns every form bound to that table, regardless of pattern.
        var byTable = repo.FindFormPatterns(table: "CustTable");
        Assert.Equal(2, byTable.Count);
        Assert.Contains(byTable, r => r.Pattern == "DetailsMaster");
        Assert.Contains(byTable, r => r.Pattern == "ListPage");

        // Combined filters intersect.
        var combo = repo.FindFormPatterns(pattern: "ListPage", table: "CustTable");
        Assert.Single(combo);
        Assert.Equal("CustTableListPage", combo[0].Name);

        // Histogram surfaces "(none)" bucket for forms without a pattern.
        var summary = repo.SummarizeFormPatterns().ToDictionary(s => s.Pattern, s => s.Count);
        Assert.Equal(1, summary["SimpleList"]);
        Assert.Equal(1, summary["DetailsMaster"]);
        Assert.Equal(1, summary["ListPage"]);
        Assert.Equal(1, summary["(none)"]);
    }

    [Fact]
    public void FindFormPatterns_returns_primary_table_from_first_datasource()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();
        var batch = ExtractBatch.Empty("Sales") with
        {
            Forms = new[]
            {
                new ExtractedForm("SalesTable", "/p/SalesTable.xml", new[]
                {
                    new ExtractedFormDataSource("SalesTable", "SalesTable"),
                    new ExtractedFormDataSource("SalesLine", "SalesLine") { JoinSource = "SalesTable" },
                })
                { Pattern = "DetailsTransaction" },
            },
        };
        repo.ApplyExtract(batch);

        var rows = repo.FindFormPatterns(pattern: "DetailsTransaction");
        var row = Assert.Single(rows);
        Assert.Equal("SalesTable", row.PrimaryTable);
        Assert.Equal(2, row.DataSourceCount);
    }
}
