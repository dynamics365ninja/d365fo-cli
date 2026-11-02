using System.Text.Json;
using System.Xml.Linq;
using D365FO.Core.Analysis;
using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using D365FO.Mcp;
using Microsoft.Data.Sqlite;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #233: every EDT in the index had an empty base type, because the extractor read it off
/// the root element name — always the abstract <c>&lt;AxEdt&gt;</c> — instead of the
/// <c>i:type</c> discriminator. Every scaffolder that types a field from the index then fell
/// back to a name guess that defaults to String, so <c>AmountCur</c>, <c>RefRecId</c> and
/// <c>Counter</c> fields came out as <c>AxTableFieldString</c>, and a field on a base enum was
/// written as an EDT reference to the enum.
/// </summary>
public sealed class EdtBaseTypeIndexTests : IDisposable
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static readonly string SamplesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "Samples", "MiniAot"));

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"d365fo-edtbase-{Guid.NewGuid():N}.sqlite");
    private readonly MetadataRepository _repo;

    public EdtBaseTypeIndexTests()
    {
        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        foreach (var batch in new MetadataExtractor().ExtractAll(SamplesDir))
            _repo.ApplyExtract(batch, sourceFingerprint: "fp-" + batch.Model);
    }

    public void Dispose()
    {
        SqlitePool.ReleaseFor(_dbPath);
        foreach (var ext in new[] { "", "-wal", "-shm" })
        {
            var p = _dbPath + ext;
            if (File.Exists(p)) File.Delete(p);
        }
    }

    // ─── Extraction ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("VinEdt", "String")]
    [InlineData("FmMileage", "Real")]
    [InlineData("FmOdometerReading", "Real")]
    [InlineData("FmVehicleRefRecId", "Int64")]
    [InlineData("FmSeatCount", "Int")]
    [InlineData("FmVehicleStatusId", "Enum")]
    [InlineData("FmServiceDate", "Date")]
    [InlineData("FmLastServiceDateTime", "UtcDateTime")]
    [InlineData("FmTelematicsId", "Guid")]
    [InlineData("FmServiceHistory", "Container")]
    [InlineData("FmServiceTime", "Time")]
    public void Extracted_edt_base_type_comes_from_the_i_type_discriminator_not_the_abstract_root(string edt, string expected)
    {
        var info = _repo.GetEdt(edt);

        Assert.NotNull(info);
        Assert.Equal(expected, info!.BaseType);
    }

    [Fact]
    public void Enum_edt_keeps_its_enum_type()
    {
        Assert.Equal("FmVehicleStatus", _repo.GetEdt("FmVehicleStatusId")!.EnumType);
    }

    [Theory]
    [InlineData("<AxEdtReal><Name>X</Name></AxEdtReal>", "Real")]
    [InlineData("<AxEdt xmlns:i='http://www.w3.org/2001/XMLSchema-instance' xmlns:q='urn:x' i:type='q:AxEdtInt64'><Name>X</Name></AxEdt>", "Int64")]
    [InlineData("<AxEdt><Name>X</Name></AxEdt>", null)]
    public void Edt_base_type_reads_a_concrete_root_or_a_prefixed_discriminator(string xml, string? expected)
    {
        Assert.Equal(expected, MetadataExtractor.EdtBaseTypeOf(XElement.Parse(xml)));
    }

    [Fact]
    public void Edt_without_a_base_type_of_its_own_resolves_through_extends()
    {
        _repo.ApplyExtract(ExtractBatch.Empty("Partial") with
        {
            Edts = new[]
            {
                new ExtractedEdt("PartialAmount", "PartialMoney", null, null, null),
                new ExtractedEdt("PartialMoney", "FmMileage", null, null, null),
                new ExtractedEdt("PartialLoopA", "PartialLoopB", null, null, null),
                new ExtractedEdt("PartialLoopB", "PartialLoopA", null, null, null),
            },
        });

        Assert.Equal("Real", _repo.GetEdtBaseType("PartialAmount"));
        Assert.Null(_repo.GetEdtBaseType("PartialLoopA"));
        Assert.Null(_repo.GetEdtBaseType("NoSuchEdt"));
    }

    // ─── Scaffolding ───────────────────────────────────────────────────────

    [Fact]
    public void Generated_table_fields_take_the_indexed_base_type_instead_of_the_String_fallback()
    {
        var types = FieldTypeResolver.For(_repo);
        var doc = XppScaffolder.Table("TypeProbeTmp", fields: new[]
        {
            new TableFieldSpec("Mileage", "FmOdometerReading", null, false),
            new TableFieldSpec("VehicleRef", "FmVehicleRefRecId", null, false),
            new TableFieldSpec("Seats", "FmSeatCount", null, false),
            new TableFieldSpec("StatusId", "FmVehicleStatusId", null, false),
            new TableFieldSpec("Vin", "VinEdt", null, false),
        }, edtBaseTypeResolver: types.Resolve);

        var fields = Fields(doc, "AxTableField");
        Assert.Equal("AxTableFieldReal", fields["Mileage"].Attribute(Xsi + "type")!.Value);
        Assert.Equal("AxTableFieldInt64", fields["VehicleRef"].Attribute(Xsi + "type")!.Value);
        Assert.Equal("AxTableFieldInt", fields["Seats"].Attribute(Xsi + "type")!.Value);
        Assert.Equal("AxTableFieldEnum", fields["StatusId"].Attribute(Xsi + "type")!.Value);
        Assert.Equal("AxTableFieldString", fields["Vin"].Attribute(Xsi + "type")!.Value);
        // An enum EDT is still referenced as an EDT; the EDT carries the enum.
        Assert.Equal("FmVehicleStatusId", fields["StatusId"].Element("ExtendedDataType")!.Value);
        Assert.Null(fields["StatusId"].Element("EnumType"));
        Assert.Null(types.Warning());
    }

    [Fact]
    public void Field_on_a_base_enum_is_AxTableFieldEnum_with_EnumType_and_no_ExtendedDataType()
    {
        var types = FieldTypeResolver.For(_repo);
        var doc = XppScaffolder.Table("TypeProbeTmp2", fields: new[]
        {
            new TableFieldSpec("Status", "FmVehicleStatus", "@Fleet:VehicleStatus", true),
        }, edtBaseTypeResolver: types.Resolve);

        var field = Fields(doc, "AxTableField")["Status"];
        Assert.Equal("AxTableFieldEnum", field.Attribute(Xsi + "type")!.Value);
        Assert.Null(field.Element("ExtendedDataType"));
        Assert.Equal("FmVehicleStatus", field.Element("EnumType")!.Value);
        // AxTableFieldEnum's own member follows the inherited ones.
        Assert.Equal(new[] { "Name", "Label", "Mandatory", "EnumType" },
            field.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.Null(types.Warning());
    }

    [Fact]
    public void Map_field_on_a_base_enum_is_AxMapFieldEnum_with_EnumType()
    {
        var doc = MapScaffolder.Map("FmTypeProbeMap", new[]
        {
            new MapFieldSpec("Status", "FmVehicleStatus"),
            new MapFieldSpec("Mileage", "FmMileage"),
        }, edtBaseTypeResolver: FieldTypeResolver.For(_repo).Resolve);

        var fields = Fields(doc, "AxMapBaseField");
        Assert.Equal("AxMapFieldEnum", fields["Status"].Attribute(Xsi + "type")!.Value);
        Assert.Null(fields["Status"].Element("ExtendedDataType"));
        Assert.Equal("FmVehicleStatus", fields["Status"].Element("EnumType")!.Value);
        Assert.Equal("AxMapFieldReal", fields["Mileage"].Attribute(Xsi + "type")!.Value);
    }

    [Fact]
    public void Field_type_the_index_cannot_resolve_falls_back_to_the_heuristic_and_says_so()
    {
        var types = FieldTypeResolver.For(_repo);
        var doc = XppScaffolder.Table("TypeProbeTmp3", fields: new[]
        {
            new TableFieldSpec("Amt", "NotIndexedAmount", null, false),
            new TableFieldSpec("When", "NotIndexedDate", null, false),
            new TableFieldSpec("Seats", "FmSeatCount", null, false),
        }, edtBaseTypeResolver: types.Resolve);

        var fields = Fields(doc, "AxTableField");
        Assert.Equal("AxTableFieldString", fields["Amt"].Attribute(Xsi + "type")!.Value);
        Assert.Equal("AxTableFieldDate", fields["When"].Attribute(Xsi + "type")!.Value);
        Assert.Equal(new[] { "NotIndexedAmount", "NotIndexedDate" }, types.Unresolved);
        var warning = types.Warning();
        Assert.NotNull(warning);
        Assert.StartsWith(FieldTypeResolver.UnresolvedCode, warning);
        Assert.Contains("NotIndexedAmount → String", warning);
        Assert.Contains("NotIndexedDate → Date", warning);
        Assert.DoesNotContain("FmSeatCount", warning);
    }

    [Fact]
    public void Resolver_without_an_index_resolves_nothing_and_warns()
    {
        var types = FieldTypeResolver.For(null);

        Assert.Null(types.Resolve("FmMileage"));
        Assert.Contains("FmMileage", types.Warning());
    }

    // ─── MCP parity ────────────────────────────────────────────────────────

    [Fact]
    public void Mcp_generate_table_types_fields_like_the_cli()
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"TypeProbeTmp-{Guid.NewGuid():N}.xml");
        try
        {
            var result = new ToolHandlers(_repo).GenerateTable("TypeProbeTmp", null,
                new[] { "Mileage:FmMileage", "VehicleRef:FmVehicleRefRecId", "Status:FmVehicleStatus" },
                pattern: null, installTo: null, outPath: outPath, overwrite: true);

            Assert.True(result.Ok, result.Error?.Message);
            var fields = Fields(XDocument.Load(outPath), "AxTableField");
            Assert.Equal("AxTableFieldReal", fields["Mileage"].Attribute(Xsi + "type")!.Value);
            Assert.Equal("AxTableFieldInt64", fields["VehicleRef"].Attribute(Xsi + "type")!.Value);
            Assert.Equal("AxTableFieldEnum", fields["Status"].Attribute(Xsi + "type")!.Value);
            Assert.Equal("FmVehicleStatus", fields["Status"].Element("EnumType")!.Value);
            Assert.DoesNotContain(result.Warnings ?? [], w => w.StartsWith(FieldTypeResolver.UnresolvedCode, StringComparison.Ordinal));
        }
        finally
        {
            if (File.Exists(outPath)) File.Delete(outPath);
        }
    }

    [Fact]
    public void Mcp_generate_edt_takes_the_concrete_type_of_its_parent()
    {
        var result = new ToolHandlers(_repo).GenerateEdt("FmTripMileage", "FmMileage", null, 0);

        Assert.True(result.Ok, result.Error?.Message);
        var xml = JsonSerializer.SerializeToElement(result.Data).GetProperty("xml").GetString()!;
        Assert.Equal("AxEdtReal", XDocument.Parse(xml).Root!.Attribute(Xsi + "type")!.Value);
    }

    // ─── get table --brief ─────────────────────────────────────────────────

    [Fact]
    public void Table_brief_reports_the_edt_base_type_instead_of_ExtendedDataType()
    {
        _repo.ApplyExtract(ExtractBatch.Empty("BriefModel") with
        {
            Tables = new[]
            {
                new ExtractedTable("FmTripLog", null, "x", new[]
                {
                    new ExtractedTableField("Mileage", "ExtendedDataType", "FmOdometerReading", null, false),
                    new ExtractedTableField("Vehicle", "ExtendedDataType", "FmVehicleRefRecId", null, false),
                    new ExtractedTableField("Odd", "ExtendedDataType", "NotIndexedEdt", null, false),
                }),
            },
        });
        var details = _repo.GetTableDetails("FmTripLog")!;

        var brief = JsonSerializer.SerializeToElement(TableBrief.FromIndex(details, _repo.GetEdtBaseType));
        var types = brief.GetProperty("fields").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.GetProperty("type").GetString());

        Assert.Equal("Real", types["Mileage"]);
        Assert.Equal("Int64", types["Vehicle"]);
        Assert.Equal("ExtendedDataType", types["Odd"]);
    }

    // ─── Schema migration ──────────────────────────────────────────────────

    [Fact]
    public void Upgrading_a_v20_index_clears_every_fingerprint_so_the_next_refresh_re_extracts()
    {
        Assert.Equal("fp-TestModel", _repo.GetModelFingerprints()["TestModel"]);

        // What a v20 build left behind: the same tables, an older user_version.
        using (var conn = new SqliteConnection(_repo.ConnectionString))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 20;";
            cmd.ExecuteNonQuery();
        }

        var upgraded = new MetadataRepository(_dbPath);
        Assert.True(upgraded.EnsureSchema());

        Assert.All(upgraded.GetModelFingerprints().Values, Assert.Null);
        Assert.Equal(21, MetadataRepository.CurrentSchemaVersion);
    }

    [Fact]
    public void An_index_already_at_v21_keeps_its_fingerprints()
    {
        var reopened = new MetadataRepository(_dbPath);

        Assert.False(reopened.EnsureSchema());
        Assert.Equal("fp-TestModel", reopened.GetModelFingerprints()["TestModel"]);
    }

    private static Dictionary<string, XElement> Fields(XDocument doc, string element) =>
        doc.Descendants(element).ToDictionary(f => f.Element("Name")!.Value, f => f);
}
