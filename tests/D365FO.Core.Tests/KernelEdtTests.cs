using D365FO.Core.Index;
using D365FO.Core.Knowledge;
using D365FO.Core.Scaffolding;
using D365FO.Core.Validation;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Kernel EDTs have no AOT file, so no lookup can find them. They compile, and their fields have
/// a fixed base type.
/// </summary>
public sealed class KernelEdtTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"kerneledt-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public KernelEdtTests()
    {
        _repo = new MetadataRepository(_db);
        _repo.EnsureSchema();
        _repo.ApplyExtract(ExtractBatch.Empty("ConFleet") with
        {
            // A custom EDT that extends a kernel one records no base type of its own.
            Edts = new[] { new ExtractedEdt("ConFieldRef", "FieldId", null, null, null) },
        });
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_db);
        try { File.Delete(_db); } catch { /* temp */ }
    }

    [Theory]
    [InlineData("UserId", "String")]
    [InlineData("recid", "Int64")]
    [InlineData("FieldId", "Integer")]
    [InlineData("RelationType", "Int64")]
    [InlineData("MoneyMST", "Real")]
    [InlineData("TimeOfDay", "Time")]
    [InlineData("Sha1HashCode", "Container")]
    public void A_kernel_edt_resolves_to_its_base_type(string name, string baseType)
    {
        Assert.Equal(baseType, FieldTypeResolver.For(_repo).Resolve(name));
        Assert.Equal(baseType, FieldTypeResolver.For(null).Resolve(name));
    }

    [Fact]
    public void An_edt_that_extends_a_kernel_edt_resolves_through_it()
        => Assert.Equal("Integer", _repo.GetEdtBaseType("ConFieldRef"));

    [Theory]
    [InlineData("FieldId", "AxTableFieldInt")]
    [InlineData("SequenceNum", "AxTableFieldInt64")]
    [InlineData("Sha1HashCode", "AxTableFieldContainer")]
    [InlineData("ValidFromDateTime", "AxTableFieldUtcDateTime")]
    public void A_scaffolded_field_gets_the_right_subtype_without_an_index(string edt, string subtype)
    {
        var table = XppScaffolder.Table("ConProbe", null, [new TableFieldSpec("Value", edt, null, false)]);

        var field = table.Descendants("AxTableField").Single(f => f.Element("Name")!.Value == "Value");
        Assert.Contains(subtype, field.Attributes().Select(a => a.Value));
    }

    [Fact]
    public void A_reference_to_a_kernel_edt_is_not_a_hallucination()
    {
        var result = ReferenceResolver.Resolve(
            "class ConProbe\n{\n    void run()\n    {\n        UserId user;\n        int id = extendedTypeNum(UserId);\n        str s = extendedTypeStr(RecId);\n    }\n}",
            _repo);

        Assert.DoesNotContain(result.Violations, v => v.Severity == "error");
        Assert.True(ReferenceResolver.IsKernelType("UserId"));
        Assert.False(ReferenceResolver.IsKernelType("UserIdd"));
    }
}
