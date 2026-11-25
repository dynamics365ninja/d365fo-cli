using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// An EDT's table reference says which field it joins on. Assuming the field is named after the
/// EDT wrote a relation to a field that does not exist whenever the key is a surrogate.
/// </summary>
public sealed class EdtReferenceFieldTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"edtref-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqlitePool.ReleaseFor(Path.Combine(_root, "index.sqlite"));
        try { Directory.Delete(_root, recursive: true); } catch { /* temp */ }
    }

    private EdtInfo Indexed(string name, string body)
    {
        var folder = Path.Combine(_root, "ConFleet", "ConFleet", "AxEdt");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name + ".xml"),
            $"<AxEdt xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\" i:type=\"AxEdtInt64\"><Name>{name}</Name>{body}</AxEdt>");
        var batch = new MetadataExtractor().ExtractModel(
            Path.Combine(_root, "ConFleet", "ConFleet"), "ConFleet", [], isCustom: true);
        var repo = new MetadataRepository(Path.Combine(_root, "index.sqlite"));
        repo.EnsureSchema();
        repo.ApplyExtract(batch);
        return repo.GetEdt(name)!;
    }

    private static string Reference(string container, string item, string table, string field)
        => $"<{container}><{item}><RelatedField>{field}</RelatedField><Table>{table}</Table></{item}></{container}>";

    [Theory]
    [InlineData("<ReferenceTable>ConCategory</ReferenceTable>", "TableReferences", "AxEdtTableReference")]
    [InlineData("", "TableReferences", "AxEdtTableReference")]
    [InlineData("", "Relations", "AxEdtRelationFixed")]
    public void The_referenced_field_comes_from_the_table_reference(string referenceTable, string container, string item)
    {
        var edt = Indexed("ConCategoryRef", referenceTable + Reference(container, item, "ConCategory", "RecId"));

        Assert.Equal("ConCategory", edt.ReferenceTable);
        Assert.Equal("RecId", edt.ReferenceField);

        var table = new TableDetails(
            new TableInfo(1, "ConVehicle", "ConFleet", null, null),
            [new TableFieldInfo("Category", "ExtendedDataType", "ConCategoryRef", null, false)],
            [], [], [], []);
        var relation = Assert.Single(TableAugmentScaffolder.DeriveRelations(table, _ => edt, null, out _));
        Assert.Equal("RecId", relation.RelatedField);
        Assert.Equal("ConCategory", relation.RelatedTable);
    }

    [Fact]
    public void An_edt_with_only_a_reference_table_joins_on_the_field_named_after_it()
    {
        var edt = Indexed("ConPlate", "<ReferenceTable>ConPlateTable</ReferenceTable>");

        Assert.Null(edt.ReferenceField);
        var table = new TableDetails(
            new TableInfo(1, "ConVehicle", "ConFleet", null, null),
            [new TableFieldInfo("Plate", "ExtendedDataType", "ConPlate", null, false)], [], [], [], []);
        Assert.Equal("ConPlate", Assert.Single(TableAugmentScaffolder.DeriveRelations(table, _ => edt, null, out _)).RelatedField);
    }
}
