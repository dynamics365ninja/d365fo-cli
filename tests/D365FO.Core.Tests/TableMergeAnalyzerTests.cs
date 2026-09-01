using D365FO.Core.Extract;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// The merged view has to be the shape the AOS sees, and has to say so when it is not.
/// </summary>
/// <remarks>
/// The roster of extension names used to be returned under a contract that promised a merge.
/// The failure mode that makes worth testing: a caller reading "field not in the list" as
/// "field does not exist", when the field is contributed by an extension nobody folded in.
/// </remarks>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class TableMergeAnalyzerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"merge-{Guid.NewGuid():N}.sqlite");
    private readonly string _workRoot = Path.Combine(Path.GetTempPath(), $"merge-work-{Guid.NewGuid():N}");
    private readonly MetadataRepository _repo;

    public TableMergeAnalyzerTests()
    {
        Directory.CreateDirectory(_workRoot);
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) { try { File.Delete(p); } catch { } }
        }
        if (Directory.Exists(_workRoot)) { try { Directory.Delete(_workRoot, true); } catch { } }
    }

    private string WriteExtension(string fileName, string xml)
    {
        var path = Path.Combine(_workRoot, fileName);
        File.WriteAllText(path, xml);
        return path;
    }

    private void Seed(string extensionPath)
    {
        _repo.ApplyExtract(ExtractBatch.Empty("Foundation") with
        {
            Publisher = "Microsoft",
            IsCustom = false,
            Tables = new[]
            {
                new ExtractedTable("FmVehicle", null, "x", new[]
                {
                    new ExtractedTableField("VehicleId", "String", "FmVehicleId", null, false),
                }),
            },
        });

        _repo.ApplyExtract(ExtractBatch.Empty("FleetCustom") with
        {
            Publisher = "Contoso",
            IsCustom = true,
            Extensions = new[]
            {
                new ExtractedObjectExtension("Table", "FmVehicle", "FmVehicle.Extension", extensionPath),
            },
        });
    }

    [Fact]
    public void An_extension_field_appears_in_the_merge_labelled_with_its_contributor()
    {
        var path = WriteExtension("FmVehicle.Extension.xml", """
            <AxTableExtension>
              <Name>FmVehicle.Extension</Name>
              <Fields>
                <AxTableField i:type="AxTableFieldString" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
                  <Name>Nickname</Name>
                  <ExtendedDataType>Name</ExtendedDataType>
                </AxTableField>
              </Fields>
              <Indexes />
              <Relations />
            </AxTableExtension>
            """);
        Seed(path);

        var merged = TableMergeAnalyzer.Merge(_repo, "FmVehicle");

        Assert.Equal("FmVehicle", merged.Table);
        Assert.Empty(merged.Unreadable);

        var baseField = Assert.Single(merged.Fields, f => f.Name == "VehicleId");
        Assert.Equal("FmVehicle", baseField.Origin);

        var added = Assert.Single(merged.Fields, f => f.Name == "Nickname");
        Assert.Equal("FmVehicle.Extension", added.Origin);
        Assert.Equal("FleetCustom", added.Model);
        Assert.Equal("Name", added.Detail);
    }

    [Fact]
    public void An_extension_that_cannot_be_read_is_reported_rather_than_dropped()
    {
        // A merge missing a contributor is worse than no merge: it answers "that field does not
        // exist" with the same confidence as a complete one.
        Seed(Path.Combine(_workRoot, "gone.xml"));

        var merged = TableMergeAnalyzer.Merge(_repo, "FmVehicle");

        Assert.Single(merged.Unreadable);
        Assert.Contains("FmVehicle.Extension", merged.Unreadable[0]);
        Assert.DoesNotContain(merged.Fields, f => f.Name == "Nickname");
    }
}
