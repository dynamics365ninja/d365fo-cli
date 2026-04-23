using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using System.Xml.Linq;
using Xunit;

namespace D365FO.Core.Tests;

public class ExtractPipelineTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-extract-{Guid.NewGuid():N}.sqlite");
    private readonly string _workRoot = Path.Combine(Path.GetTempPath(), $"d365fo-work-{Guid.NewGuid():N}");

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

    [Fact]
    public void ApplyExtract_is_idempotent_and_counts_match()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        var batch = new ExtractBatch(
            Model: "Fleet",
            Publisher: "Contoso",
            Layer: "usr",
            IsCustom: true,
            Tables: new[] { new ExtractedTable("FleetVehicle", "Vehicle", "/x/FleetVehicle.xml",
                new[] { new ExtractedTableField("Vin", "ExtendedDataType", "VinEdt", "VIN", true) }) },
            Classes: new[] { new ExtractedClass("FleetService", null, false, true, "/x/FleetService.xml",
                new[] { new ExtractedMethod("run", "public void run()", "void", false) }) },
            Edts: new[] { new ExtractedEdt("VinEdt", null, "String", "VIN", 17) },
            Enums: new[] { new ExtractedEnum("FleetKind", "Kind", new[] { new ExtractedEnumValue("Car", 0, "Car") }) },
            MenuItems: new[] { new ExtractedMenuItem("FleetForm", "Display", "FleetVehicleForm", "Form", null) },
            CocExtensions: new[] { new ExtractedCoc("CustTable", "update", "CustTable_Extension") },
            Labels: new[] { new ExtractedLabel("FleetLabels", "en-us", "VIN", "Vehicle Identification Number") });

        repo.ApplyExtract(batch);
        var counts1 = repo.CountAll();
        Assert.Equal(1, counts1.Tables);
        Assert.Equal(1, counts1.Fields);
        Assert.Equal(1, counts1.Classes);
        Assert.Equal(1, counts1.Enums);
        Assert.Equal(1, counts1.Coc);

        // Re-apply is idempotent — counts must not double.
        repo.ApplyExtract(batch);
        var counts2 = repo.CountAll();
        Assert.Equal(counts1, counts2);

        var en = repo.GetEnum("FleetKind");
        Assert.NotNull(en);
        Assert.Single(en!.Values);

        var usages = repo.FindUsages("Fleet");
        Assert.NotEmpty(usages);
    }

    [Fact]
    public void MetadataExtractor_reads_a_synthetic_model()
    {
        var model = Path.Combine(_workRoot, "Contoso", "Contoso");
        Directory.CreateDirectory(Path.Combine(model, "AxTable"));
        Directory.CreateDirectory(Path.Combine(model, "AxEnum"));

        File.WriteAllText(Path.Combine(model, "AxTable", "DemoTable.xml"), """
            <AxTable>
              <Name>DemoTable</Name>
              <Label>Demo</Label>
              <Fields>
                <AxTableField>
                  <Name>Code</Name>
                  <ExtendedDataType>Name</ExtendedDataType>
                  <Mandatory>Yes</Mandatory>
                </AxTableField>
              </Fields>
            </AxTable>
            """);
        File.WriteAllText(Path.Combine(model, "AxEnum", "DemoEnum.xml"), """
            <AxEnum>
              <Name>DemoEnum</Name>
              <EnumValues>
                <AxEnumValue><Name>One</Name><Value>0</Value></AxEnumValue>
                <AxEnumValue><Name>Two</Name><Value>1</Value></AxEnumValue>
              </EnumValues>
            </AxEnum>
            """);

        var ex = new MetadataExtractor();
        var batches = ex.ExtractAll(_workRoot).ToList();
        var batch = Assert.Single(batches);
        Assert.Equal("Contoso", batch.Model);
        var table = Assert.Single(batch.Tables);
        Assert.Equal("DemoTable", table.Name);
        var field = Assert.Single(table.Fields);
        Assert.True(field.Mandatory);
        Assert.Equal("Name", field.EdtName);
        var en = Assert.Single(batch.Enums);
        Assert.Equal(2, en.Values.Count);
    }

    [Fact]
    public void MetadataExtractor_marks_models_matching_custom_pattern()
    {
        foreach (var name in new[] { "AslCore", "AslFinance", "MsExtensions" })
        {
            var dir = Path.Combine(_workRoot, "Pkg", name, "AxTable");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "T.xml"), "<AxTable><Name>T</Name></AxTable>");
        }

        var ex = new MetadataExtractor();
        var batches = ex.ExtractAll(_workRoot, labelLanguages: null, customModelPatterns: new[] { "Asl*" })
            .ToDictionary(b => b.Model, b => b.IsCustom);

        Assert.True(batches["AslCore"]);
        Assert.True(batches["AslFinance"]);
        Assert.False(batches["MsExtensions"]);
    }

    [Fact]
    public void Scaffolder_writes_table_atomically_with_backup()
    {
        var target = Path.Combine(_workRoot, "out", "MyTable.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "<old/>");

        var doc = XppScaffolder.Table("MyTable", "Hi", new[] { new TableFieldSpec("A", "Name", null, false) });
        var res = ScaffoldFileWriter.Write(doc, target, overwrite: true);
        Assert.True(File.Exists(target));
        Assert.NotNull(res.BackupPath);
        Assert.True(File.Exists(res.BackupPath!));
        var written = XDocument.Load(target);
        Assert.Equal("AxTable", written.Root!.Name.LocalName);
    }
}
