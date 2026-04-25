using System.Linq;
using D365FO.Core.Scaffolding;

namespace D365FO.Cli.Tests;

public class TablePatternScaffoldingTests
{
    [Theory]
    [InlineData("master",          TablePattern.Main)]
    [InlineData("Main",            TablePattern.Main)]
    [InlineData("transaction",     TablePattern.Transaction)]
    [InlineData("transactional",   TablePattern.Transaction)]
    [InlineData("setup",           TablePattern.Parameter)]
    [InlineData("config",          TablePattern.Parameter)]
    [InlineData("parameter",       TablePattern.Parameter)]
    [InlineData("group",           TablePattern.Group)]
    [InlineData("worksheet-header",TablePattern.WorksheetHeader)]
    [InlineData("worksheet_line",  TablePattern.WorksheetLine)]
    [InlineData("lookup",          TablePattern.Reference)]
    [InlineData("framework",       TablePattern.Framework)]
    [InlineData("misc",            TablePattern.Miscellaneous)]
    [InlineData("",                TablePattern.None)]
    [InlineData(null,              TablePattern.None)]
    public void Normalizer_accepts_aliases(string? input, TablePattern expected)
    {
        Assert.True(TablePatternNormalizer.TryNormalize(input, out var actual, out var err));
        Assert.Null(err);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("TempDB")]
    [InlineData("InMemory")]
    public void Normalizer_rejects_TableType_values_passed_as_pattern(string bad)
    {
        Assert.False(TablePatternNormalizer.TryNormalize(bad, out _, out var err));
        Assert.NotNull(err);
        Assert.Contains("TableType", err, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalizer_rejects_unknown_pattern()
    {
        Assert.False(TablePatternNormalizer.TryNormalize("nonsense", out _, out var err));
        Assert.Contains("Unknown table pattern", err);
    }

    [Theory]
    [InlineData("regulartable", TableStorage.RegularTable)]
    [InlineData("TempDB",       TableStorage.TempDB)]
    [InlineData("inmemory",     TableStorage.InMemory)]
    [InlineData("",             TableStorage.RegularTable)]
    public void Storage_normalizer_accepts(string? input, TableStorage expected)
    {
        Assert.True(TablePatternNormalizer.TryNormalizeStorage(input, out var actual, out _));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Pattern_None_emits_no_TableGroup_element()
    {
        var doc = XppScaffolder.Table("FmThing");
        Assert.Null(doc.Root!.Element("TableGroup"));
        Assert.Null(doc.Root.Element("TableType"));
    }

    [Theory]
    [InlineData(TablePattern.Main,            "Main")]
    [InlineData(TablePattern.Transaction,     "Transaction")]
    [InlineData(TablePattern.Parameter,       "Parameter")]
    [InlineData(TablePattern.WorksheetHeader, "WorksheetHeader")]
    public void Pattern_emits_canonical_TableGroup(TablePattern p, string expected)
    {
        var doc = XppScaffolder.Table("FmThing", pattern: p);
        Assert.Equal(expected, doc.Root!.Element("TableGroup")!.Value);
    }

    [Fact]
    public void TempDB_storage_emits_TableType_but_pattern_remains_Main()
    {
        var doc = XppScaffolder.Table("FmTmp", pattern: TablePattern.Main, storage: TableStorage.TempDB);
        Assert.Equal("Main",   doc.Root!.Element("TableGroup")!.Value);
        Assert.Equal("TempDB", doc.Root.Element("TableType")!.Value);
    }

    [Fact]
    public void Pattern_Main_default_skeleton_has_AccountNum_Name_Description()
    {
        var doc = XppScaffolder.Table("FmCustomer", pattern: TablePattern.Main);
        var fields = doc.Root!.Element("Fields")!.Elements("AxTableField")
            .Select(e => e.Element("Name")!.Value).ToList();
        Assert.Contains("AccountNum",  fields);
        Assert.Contains("Name",        fields);
        Assert.Contains("Description", fields);
    }

    [Fact]
    public void Pattern_Transaction_default_skeleton_has_TransDate_and_AmountMST()
    {
        var doc = XppScaffolder.Table("FmTrans", pattern: TablePattern.Transaction);
        var amountEdt = doc.Root!.Element("Fields")!.Elements("AxTableField")
            .First(e => e.Element("Name")!.Value == "Amount")
            .Element("ExtendedDataType")!.Value;
        Assert.Equal("AmountMST", amountEdt);
    }

    [Fact]
    public void Caller_supplied_fields_override_pattern_defaults()
    {
        var custom = new[] { new TableFieldSpec("Foo", "Name", null, true) };
        var doc = XppScaffolder.Table("FmThing", fields: custom, pattern: TablePattern.Main);
        var names = doc.Root!.Element("Fields")!.Elements("AxTableField")
            .Select(e => e.Element("Name")!.Value).ToList();
        Assert.Single(names);
        Assert.Equal("Foo", names[0]);
    }

    [Fact]
    public void Alternate_key_index_is_emitted_with_AlternateKey_yes()
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        var idx = doc.Root!.Element("Indexes")!.Element("AxTableIndex")!;
        Assert.Equal("Yes", idx.Element("AlternateKey")!.Value);
        Assert.Equal("No",  idx.Element("AllowDuplicates")!.Value);

        var pkFields = idx.Element("Fields")!.Elements("AxTableIndexField")
            .Select(e => e.Element("DataField")!.Value).ToList();
        Assert.Contains("AccountNum", pkFields);
    }

    [Fact]
    public void Explicit_primary_key_overrides_mandatory_default()
    {
        var doc = XppScaffolder.Table(
            "FmThing",
            pattern: TablePattern.Main,
            primaryKeyFields: new[] { "Name" });
        var pkFields = doc.Root!.Element("Indexes")!.Element("AxTableIndex")!
            .Element("Fields")!.Elements("AxTableIndexField")
            .Select(e => e.Element("DataField")!.Value).ToList();
        Assert.Single(pkFields);
        Assert.Equal("Name", pkFields[0]);
    }

    [Fact]
    public void Unknown_primary_key_field_is_silently_ignored_so_compile_isnt_blocked()
    {
        // We want the "compile-clean" guarantee: an unknown PK reference falls
        // back to mandatory fields rather than emitting a dangling DataField.
        var doc = XppScaffolder.Table(
            "FmThing",
            pattern: TablePattern.Main,
            primaryKeyFields: new[] { "FieldThatDoesNotExist" });
        var pkFields = doc.Root!.Element("Indexes")!.Element("AxTableIndex")!
            .Element("Fields")!.Elements("AxTableIndexField")
            .Select(e => e.Element("DataField")!.Value).ToList();
        Assert.Contains("AccountNum", pkFields);
    }

    [Fact]
    public void Empty_table_emits_no_index_element()
    {
        // Pattern=None and no fields supplied → no AxTableIndex (would be empty).
        var doc = XppScaffolder.Table("FmEmpty");
        Assert.Null(doc.Root!.Element("Indexes"));
    }
}
