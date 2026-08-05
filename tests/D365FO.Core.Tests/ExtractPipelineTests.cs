using D365FO.Core.Extract;
using D365FO.Core.Index;
using D365FO.Core.Scaffolding;
using System.Xml.Linq;
using Xunit;

namespace D365FO.Core.Tests;

// Shares a collection with ScaffoldFileWriterJournalTests: both call ScaffoldFileWriter.Write,
// which (issue #113) journals via the process-wide D365FO_INDEX_DB env var that the journal
// test overrides — running them in parallel would race on that global state.
[Collection("ScaffoldFileWriter")]
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
            Labels: new[] { new ExtractedLabel("FleetLabels", "en-us", "VIN", "Vehicle Identification Number") })
        {
            // Extension that adds a field to a DIFFERENT table (CustTable) using the
            // same EDT as the base FleetVehicle.Vin field. Exercises the base +
            // extension union in FindTablesByField (find-fields gap fix).
            Extensions = new[]
            {
                new ExtractedObjectExtension("Table", "CustTable", "CustTable.FleetExtension",
                    "/x/CustTable.FleetExtension.xml")
                {
                    AddedFields = new[]
                    {
                        new ExtractedTableField("FleetVin", "ExtendedDataType", "VinEdt", "Fleet VIN", false),
                    },
                },
            },
        };

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

        // Field-level lookup: exact field name and exact EDT name should both
        // resolve to the same table (issue #101 — relation-based results were
        // being confused with actual field membership).
        var byFieldName = repo.FindTablesByField("Vin");
        var hit = Assert.Single(byFieldName);
        Assert.Equal("FleetVehicle", hit.TableName);
        Assert.Equal("VinEdt", hit.EdtName);
        Assert.Equal("base", hit.Source);

        // EDT lookup now spans base-table fields AND extension-added fields, so it
        // reconciles with a direct SQL column query (find-fields gap fix).
        var byEdtName = repo.FindTablesByField("VinEdt");
        Assert.Equal(2, byEdtName.Count);
        Assert.Contains(byEdtName, r => r.TableName == "FleetVehicle" && r.Source == "base");
        var extByEdt = Assert.Single(byEdtName, r => r.Source == "extension");
        Assert.Equal("CustTable", extByEdt.TableName);
        Assert.Equal("CustTable.FleetExtension", extByEdt.ExtensionName);

        // The extension-added field also resolves by its own name and carries provenance.
        var byExtField = repo.FindTablesByField("FleetVin");
        var extHit = Assert.Single(byExtField);
        Assert.Equal("CustTable", extHit.TableName);
        Assert.Equal("extension", extHit.Source);
        Assert.Equal("CustTable.FleetExtension", extHit.ExtensionName);

        Assert.Empty(repo.FindTablesByField("NoSuchFieldOrEdt"));
    }

    [Fact]
    public void FindTablesByField_limit_returns_deterministic_alphabetical_order_across_base_and_extension()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        // Base tables declaring "SharedField" directly, inserted in REVERSE
        // alphabetical order so the two alphabetically-earliest names (AlphaTable,
        // BravoTable) are the LAST rows written. Without an ORDER BY before the
        // SQL-side LIMIT, SQLite's unordered scan returns rows in insertion order,
        // so a naive `LIMIT @limit` would truncate away exactly the earliest names
        // before the final in-memory sort ever sees them.
        var baseTableNames = new[]
        {
            "GolfTable", "FoxtrotTable", "EchoTable", "DeltaTable", "CharlieTable", "BravoTable", "AlphaTable",
        };
        var tables = baseTableNames.Select(n => new ExtractedTable(n, null, $"/x/{n}.xml",
            new[] { new ExtractedTableField("SharedField", "String", null, null, false) })).ToArray();

        // Extension-added "SharedField" on further tables, sorting well after the
        // base tables above but also inserted in reverse order, to exercise the
        // same truncation risk on the ExtensionFields query.
        var extTableNames = new[]
        {
            "NovemberTable", "MikeTable", "LimaTable", "KiloTable", "JulietTable", "IndiaTable", "HotelTable",
        };
        var extensions = extTableNames.Select(n => new ExtractedObjectExtension(
            "Table", n, $"{n}.SharedExt", $"/x/{n}.SharedExt.xml")
        {
            AddedFields = new[] { new ExtractedTableField("SharedField", "String", null, null, false) },
        }).ToArray();

        var batch = new ExtractBatch(
            Model: "Shared",
            Publisher: "Contoso",
            Layer: "usr",
            IsCustom: true,
            Tables: tables,
            Classes: Array.Empty<ExtractedClass>(),
            Edts: Array.Empty<ExtractedEdt>(),
            Enums: Array.Empty<ExtractedEnum>(),
            MenuItems: Array.Empty<ExtractedMenuItem>(),
            CocExtensions: Array.Empty<ExtractedCoc>(),
            Labels: Array.Empty<ExtractedLabel>())
        {
            Extensions = extensions,
        };

        repo.ApplyExtract(batch);

        // 14 total matches (7 base + 7 extension); ask for far fewer so the
        // per-source SQL-side LIMIT actually kicks in on both queries.
        var results = repo.FindTablesByField("SharedField", limit: 5);

        Assert.Equal(
            new[] { "AlphaTable", "BravoTable", "CharlieTable", "DeltaTable", "EchoTable" },
            results.Select(r => r.TableName).ToArray());
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
    public void MetadataExtractor_synthesizes_positional_values_for_UseEnumValue_No()
    {
        var model = Path.Combine(_workRoot, "Contoso", "Contoso");
        Directory.CreateDirectory(Path.Combine(model, "AxEnum"));
        File.WriteAllText(Path.Combine(model, "AxEnum", "TierEnum.xml"), """
            <AxEnum>
              <Name>TierEnum</Name>
              <UseEnumValue>No</UseEnumValue>
              <IsExtensible>true</IsExtensible>
              <EnumValues>
                <AxEnumValue><Name>Standard</Name></AxEnumValue>
                <AxEnumValue><Name>Silver</Name></AxEnumValue>
                <AxEnumValue><Name>Gold</Name></AxEnumValue>
              </EnumValues>
            </AxEnum>
            """);

        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var en = Assert.Single(Assert.Single(batches).Enums);
        // UseEnumValue=No omits <Value>; the compiled value is positional.
        Assert.Equal(new int?[] { 0, 1, 2 }, en.Values.Select(v => v.Value).ToArray());
    }

    [Fact]
    public void MetadataExtractor_keeps_explicit_values_for_UseEnumValue_Yes()
    {
        var model = Path.Combine(_workRoot, "Contoso", "Contoso");
        Directory.CreateDirectory(Path.Combine(model, "AxEnum"));
        File.WriteAllText(Path.Combine(model, "AxEnum", "GapEnum.xml"), """
            <AxEnum>
              <Name>GapEnum</Name>
              <EnumValues>
                <AxEnumValue><Name>A</Name><Value>10</Value></AxEnumValue>
                <AxEnumValue><Name>B</Name><Value>20</Value></AxEnumValue>
              </EnumValues>
            </AxEnum>
            """);

        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var en = Assert.Single(Assert.Single(batches).Enums);
        Assert.Equal(new int?[] { 10, 20 }, en.Values.Select(v => v.Value).ToArray());
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

    [Fact]
    public void MetadataExtractor_AllowDuplicates_Yes_means_duplicate()
    {
        var model = Path.Combine(_workRoot, "PkgFix2", "PkgFix2");
        Directory.CreateDirectory(Path.Combine(model, "AxTable"));
        // Index with AllowDuplicates="Yes"
        File.WriteAllText(Path.Combine(model, "AxTable", "T2.xml"), """
            <AxTable>
              <Name>T2</Name>
              <Indexes>
                <AxTableIndex>
                  <Name>IX_Dup</Name>
                  <AllowDuplicates>Yes</AllowDuplicates>
                  <Fields>
                    <AxTableIndexField><DataField>Code</DataField></AxTableIndexField>
                  </Fields>
                </AxTableIndex>
              </Indexes>
            </AxTable>
            """);
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix2");
        var table = Assert.Single(batch.Tables);
        var ix = Assert.Single(table.Indexes);
        Assert.True(ix.AllowDuplicates, "AllowDuplicates=Yes should be true");
    }

    [Fact]
    public void MetadataExtractor_AllowDuplicates_absent_means_unique()
    {
        var model = Path.Combine(_workRoot, "PkgFix2b", "PkgFix2b");
        Directory.CreateDirectory(Path.Combine(model, "AxTable"));
        // Index WITHOUT AllowDuplicates element — D365FO default is unique
        File.WriteAllText(Path.Combine(model, "AxTable", "T2b.xml"), """
            <AxTable>
              <Name>T2b</Name>
              <Indexes>
                <AxTableIndex>
                  <Name>IX_Unique</Name>
                  <Fields>
                    <AxTableIndexField><DataField>Code</DataField></AxTableIndexField>
                  </Fields>
                </AxTableIndex>
              </Indexes>
            </AxTable>
            """);
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix2b");
        var table = Assert.Single(batch.Tables);
        var ix = Assert.Single(table.Indexes);
        Assert.False(ix.AllowDuplicates, "Missing AllowDuplicates should default to unique (false)");
    }

    [Fact]
    public void MetadataExtractor_reads_table_fields_when_FieldGroups_precede_Fields()
    {
        // Regression: FieldGroups contain their own <Fields> children.
        // The extractor must pick the root-level <Fields>, not a nested one.
        var model = Path.Combine(_workRoot, "PkgFG", "PkgFG");
        Directory.CreateDirectory(Path.Combine(model, "AxTable"));
        File.WriteAllText(Path.Combine(model, "AxTable", "TWithFG.xml"), """
            <AxTable>
              <Name>TWithFG</Name>
              <FieldGroups>
                <AxTableFieldGroup>
                  <Name>AutoReport</Name>
                  <Fields>
                    <AxTableFieldGroupField><DataField>AccountNum</DataField></AxTableFieldGroupField>
                  </Fields>
                </AxTableFieldGroup>
              </FieldGroups>
              <Fields>
                <AxTableFieldString>
                  <Name>AccountNum</Name>
                  <ExtendedDataType>CustAccount</ExtendedDataType>
                  <Mandatory>Yes</Mandatory>
                </AxTableFieldString>
                <AxTableFieldString>
                  <Name>Name</Name>
                  <ExtendedDataType>Name</ExtendedDataType>
                </AxTableFieldString>
              </Fields>
            </AxTable>
            """);
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFG");
        var table = Assert.Single(batch.Tables);
        Assert.Equal(2, table.Fields.Count);
        Assert.Equal("AccountNum", table.Fields[0].Name);
        Assert.Equal("Name", table.Fields[1].Name);
    }

    [Fact]
    public void MetadataExtractor_reads_data_entity_fields_when_FieldGroups_precede_Fields()
    {
        // Regression: AxDataEntityView can contain FieldGroups with nested <Fields>.
        // The extractor must pick the root-level <Fields> with AxDataEntityViewField nodes.
        var model = Path.Combine(_workRoot, "PkgEntityFG", "PkgEntityFG");
        Directory.CreateDirectory(Path.Combine(model, "AxDataEntityView"));
        File.WriteAllText(Path.Combine(model, "AxDataEntityView", "VendVendorGroupEntity.xml"), """
            <AxDataEntityView>
              <Name>VendVendorGroupEntity</Name>
              <FieldGroups>
                <AxTableFieldGroup>
                  <Name>AutoReport</Name>
                  <Fields>
                    <AxTableFieldGroupField><DataField>VendorGroupId</DataField></AxTableFieldGroupField>
                  </Fields>
                </AxTableFieldGroup>
              </FieldGroups>
              <Fields>
                <AxDataEntityViewField>
                  <Name>VendorGroupId</Name>
                  <DataField>VendGroup</DataField>
                  <DataSource>VendGroup</DataSource>
                  <IsMandatory>Yes</IsMandatory>
                </AxDataEntityViewField>
                <AxDataEntityViewField>
                  <Name>Description</Name>
                  <DataField>Name</DataField>
                  <DataSource>VendGroup</DataSource>
                </AxDataEntityViewField>
              </Fields>
            </AxDataEntityView>
            """);

        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgEntityFG");
        var entity = Assert.Single(batch.DataEntities);
        Assert.Equal(2, entity.Fields.Count);
        Assert.Equal("VendorGroupId", entity.Fields[0].Name);
        Assert.Equal("Description", entity.Fields[1].Name);
        Assert.True(entity.Fields[0].IsMandatory);
    }

    [Fact]
    public void MetadataExtractor_reads_form_datasources_when_SourceCode_DataSources_precede_metadata()
    {
        // Regression (issue #99): a form that carries datasource override methods
        // has a form-level <SourceCode><DataSources> block whose <DataSource>
        // children are NOT AxFormDataSource, and it appears BEFORE the metadata
        // <DataSources>. The extractor must still pick the metadata container and
        // index the real datasource, not the (first-in-document) SourceCode one.
        var model = Path.Combine(_workRoot, "PkgFormDS", "PkgFormDS");
        Directory.CreateDirectory(Path.Combine(model, "AxForm"));
        File.WriteAllText(Path.Combine(model, "AxForm", "DemoForm.xml"), """
            <AxForm xmlns="Microsoft.Dynamics.AX.Metadata.V6" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>DemoForm</Name>
              <SourceCode>
                <DataSources xmlns="">
                  <DataSource>
                    <Name>DemoDS</Name>
                    <Methods>
                      <Method><Name>active</Name><Source><![CDATA[public int active(){return super();}]]></Source></Method>
                    </Methods>
                    <Fields />
                  </DataSource>
                </DataSources>
              </SourceCode>
              <DataSources>
                <AxFormDataSource xmlns="">
                  <Name>DemoDS</Name>
                  <Table>DemoTable</Table>
                </AxFormDataSource>
              </DataSources>
              <Design />
            </AxForm>
            """);

        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFormDS");
        var form = Assert.Single(batch.Forms);
        Assert.Equal("DemoForm", form.Name);
        var ds = Assert.Single(form.DataSources);
        Assert.Equal("DemoDS", ds.Name);
        Assert.Equal("DemoTable", ds.Table);
    }

    [Fact]
    public void MetadataExtractor_detects_abstract_with_newline_in_declaration()
    {
        var model = Path.Combine(_workRoot, "PkgFix4", "PkgFix4");
        Directory.CreateDirectory(Path.Combine(model, "AxClass"));
        // Declaration split over a newline (no space on either side of "abstract")
        File.WriteAllText(Path.Combine(model, "AxClass", "BaseClass.xml"), @"
<AxClass>
  <Name>BaseClass</Name>
  <SourceCode>
    <Declaration>public abstract
class BaseClass extends SysOperationServiceBase
{
}</Declaration>
  </SourceCode>
</AxClass>");;
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix4");
        var cls = Assert.Single(batch.Classes);
        Assert.True(cls.IsAbstract, "abstract with trailing newline should be detected by word-boundary regex");
    }

    [Fact]
    public void MetadataExtractor_detects_ExtensionOf_intrinsics_case_insensitively()
    {
        var model = Path.Combine(_workRoot, "PkgCocCase", "PkgCocCase");
        Directory.CreateDirectory(Path.Combine(model, "AxClass"));
        File.WriteAllText(Path.Combine(model, "AxClass", "Unrelated_Extension.xml"), """
            <AxClass>
              <Name>Unrelated_Extension</Name>
              <SourceCode>
                <Declaration><![CDATA[
            [extensionof(tablestr(CustTable))]
            final class Unrelated_Extension
            {
            }
                ]]></Declaration>
                <Methods>
                  <Method>
                    <Name>update</Name>
                    <Source><![CDATA[public void update() { next update(); }]]></Source>
                  </Method>
                </Methods>
              </SourceCode>
            </AxClass>
            """);

        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgCocCase");
        var coc = Assert.Single(batch.CocExtensions);
        Assert.Equal("CustTable", coc.TargetClass);
        Assert.Equal("update", coc.TargetMethod);
        Assert.Equal("Unrelated_Extension", coc.ExtensionClass);
    }

    [Fact]
    public void MetadataExtractor_today_in_comment_does_not_flag_HasTodayCall()
    {
        var model = Path.Combine(_workRoot, "PkgFix5", "PkgFix5");
        Directory.CreateDirectory(Path.Combine(model, "AxClass"));
        File.WriteAllText(Path.Combine(model, "AxClass", "SomeClass.xml"), @"
<AxClass>
  <Name>SomeClass</Name>
  <SourceCode>
    <Methods>
      <Method>
        <Name>run</Name>
        <Source>
public void run()
{
    // NOTE: today() is deprecated, use DateTimeUtil::getToday(...)
    TransDate d = DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone());
}
        </Source>
      </Method>
    </Methods>
  </SourceCode>
</AxClass>");;
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix5");
        var cls = Assert.Single(batch.Classes);
        var method = Assert.Single(cls.Methods);
        Assert.False(method.HasTodayCall,
            "today() inside a // comment must not flag HasTodayCall");
    }

    [Fact]
    public void MetadataExtractor_today_in_string_literal_does_not_flag_HasTodayCall()
    {
        var model = Path.Combine(_workRoot, "PkgFix5b", "PkgFix5b");
        Directory.CreateDirectory(Path.Combine(model, "AxClass"));
        File.WriteAllText(Path.Combine(model, "AxClass", "SomeClass2.xml"), @"
<AxClass>
  <Name>SomeClass2</Name>
  <SourceCode>
    <Methods>
      <Method>
        <Name>run</Name>
        <Source>
public void run()
{
    str msg = ""Do not use today() - use DateTimeUtil instead"";
    info(msg);
}
        </Source>
      </Method>
    </Methods>
  </SourceCode>
</AxClass>");
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix5b");
        var cls = Assert.Single(batch.Classes);
        var method = Assert.Single(cls.Methods);
        Assert.False(method.HasTodayCall,
            "today() inside a string literal must not flag HasTodayCall");
    }

    [Fact]
    public void MetadataExtractor_security_skips_disabled_entry_points()
    {
        var model = Path.Combine(_workRoot, "PkgFix10", "PkgFix10");
        Directory.CreateDirectory(Path.Combine(model, "AxSecurityPrivilege"));
        File.WriteAllText(Path.Combine(model, "AxSecurityPrivilege", "FleetPriv.xml"), """
            <AxSecurityPrivilege>
              <Name>FleetPriv</Name>
              <EntryPoints>
                <AxSecurityEntryPointReference>
                  <ObjectName>FleetForm</ObjectName>
                  <ObjectType>MenuItemDisplay</ObjectType>
                  <AccessLevel>Read</AccessLevel>
                </AxSecurityEntryPointReference>
                <AxSecurityEntryPointReference>
                  <ObjectName>FleetOldForm</ObjectName>
                  <ObjectType>MenuItemDisplay</ObjectType>
                  <AccessLevel>Read</AccessLevel>
                  <Enabled>No</Enabled>
                </AxSecurityEntryPointReference>
              </EntryPoints>
            </AxSecurityPrivilege>
            """);
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix10");
        var priv = Assert.Single(batch.Privileges);
        // Only enabled entry point should appear
        var ep = Assert.Single(priv.EntryPoints);
        Assert.Equal("FleetForm", ep.ObjectName);
    }

    [Fact]
    public void MetadataExtractor_parses_AxMap()
    {
        var model = Path.Combine(_workRoot, "PkgFix11", "PkgFix11");
        Directory.CreateDirectory(Path.Combine(model, "AxMap"));
        File.WriteAllText(Path.Combine(model, "AxMap", "DirPartyAddress.xml"), """
            <AxMap>
              <Name>DirPartyAddress</Name>
              <Label>@SYS12345</Label>
              <Fields>
                <AxMapBaseField>
                  <Name>Street</Name>
                  <ExtendedDataType>LogisticsAddressStreet</ExtendedDataType>
                </AxMapBaseField>
                <AxMapBaseField>
                  <Name>City</Name>
                  <ExtendedDataType>LogisticsAddressCity</ExtendedDataType>
                </AxMapBaseField>
              </Fields>
              <Mappings>
                <AxTableMapping>
                  <MappingTable>LogisticsPostalAddress</MappingTable>
                </AxTableMapping>
                <AxTableMapping>
                  <MappingTable>LogisticsLocationAddress</MappingTable>
                </AxTableMapping>
              </Mappings>
            </AxMap>
            """);
        var batches = new MetadataExtractor().ExtractAll(_workRoot).ToList();
        var batch = batches.Single(b => b.Model == "PkgFix11");
        var map = Assert.Single(batch.Maps);
        Assert.Equal("DirPartyAddress", map.Name);
        Assert.Equal(2, map.Fields.Count);
        Assert.Equal("Street", map.Fields[0].Name);
        Assert.Equal("LogisticsAddressStreet", map.Fields[0].EdtName);
        Assert.Equal(2, map.MappedTables.Count);
        Assert.Contains("LogisticsPostalAddress", map.MappedTables);
    }

    [Fact]
    public void ApplyExtract_roundtrips_AxMaps_and_SearchMaps_finds_them()
    {
        var repo = new MetadataRepository(_dbPath);
        repo.EnsureSchema();

        var mapField = new ExtractedMapField("Street", "ExtendedDataType", "LogisticsAddressStreet", null);
        var map = new ExtractedMap("DirPartyAddress", "@SYS12345", "/model/AxMap/DirPartyAddress.xml",
            new[] { mapField })
        {
            MappedTables = new[] { "LogisticsPostalAddress" },
        };
        var batch = ExtractBatch.Empty("ApplicationSuite") with { Maps = new[] { map } };

        repo.ApplyExtract(batch);

        // SearchMaps
        var hits = repo.SearchMaps("DirParty");
        var hit = Assert.Single(hits);
        Assert.Equal("DirPartyAddress", hit.Name);
        Assert.Equal("ApplicationSuite", hit.Model);

        // GetMap full detail
        var detail = repo.GetMap("DirPartyAddress");
        Assert.NotNull(detail);
        var field = Assert.Single(detail!.Fields);
        Assert.Equal("Street", field.Name);
        Assert.Equal("LogisticsAddressStreet", field.EdtName);
        var table = Assert.Single(detail.MappedTables);
        Assert.Equal("LogisticsPostalAddress", table);

        // Re-apply is idempotent — no duplicate rows
        repo.ApplyExtract(batch);
        Assert.Single(repo.SearchMaps("DirParty"));
    }
}
