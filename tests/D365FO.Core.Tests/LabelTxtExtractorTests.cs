using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

public class LabelTxtExtractorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"d365fo-labels-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ParseLabelFile_reads_sibling_txt()
    {
        var model = Path.Combine(_root, "Fleet", "Fleet");
        var labelDir = Path.Combine(model, "AxLabelFile");
        Directory.CreateDirectory(labelDir);

        File.WriteAllText(Path.Combine(labelDir, "FleetLabels.xml"), """
            <AxLabelFile>
              <Name>FleetLabels</Name>
            </AxLabelFile>
            """);

        File.WriteAllText(Path.Combine(labelDir, "FleetLabels.en-us.label.txt"), """
            ;this is a comment
            Title=Fleet Manager
            Vin=Vehicle Identification Number
            MultiEq=a=b=c
            """);

        File.WriteAllText(Path.Combine(labelDir, "FleetLabels.cs.label.txt"), """
            Title=Správce vozového parku
            """);

        var ex = new MetadataExtractor();
        var batches = ex.ExtractAll(_root).ToList();
        var batch = Assert.Single(batches);
        Assert.Equal(4, batch.Labels.Count);
        var title = batch.Labels.First(l => l.Key == "Title" && l.Language == "en-us");
        Assert.Equal("Fleet Manager", title.Value);
        var multi = batch.Labels.First(l => l.Key == "MultiEq");
        Assert.Equal("a=b=c", multi.Value); // split on first '=' only
        var cs = batch.Labels.First(l => l.Language == "cs");
        Assert.Equal("Správce vozového parku", cs.Value);
    }

    [Fact]
    public void ParseLabelFile_respects_language_filter()
    {
        var model = Path.Combine(_root, "Fleet", "Fleet");
        var labelDir = Path.Combine(model, "AxLabelFile");
        Directory.CreateDirectory(labelDir);
        File.WriteAllText(Path.Combine(labelDir, "FleetLabels.xml"), "<AxLabelFile><Name>FleetLabels</Name></AxLabelFile>");
        File.WriteAllText(Path.Combine(labelDir, "FleetLabels.en-us.label.txt"), "A=1\n");
        File.WriteAllText(Path.Combine(labelDir, "FleetLabels.cs.label.txt"), "A=1\n");

        var ex = new MetadataExtractor();
        var batches = ex.ExtractAll(_root, new[] { "cs" }).ToList();
        var batch = Assert.Single(batches);
        var lbl = Assert.Single(batch.Labels);
        Assert.Equal("cs", lbl.Language);
    }
}
