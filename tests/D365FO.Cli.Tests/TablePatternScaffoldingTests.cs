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

    /// <summary>
    /// Leaving &lt;TableGroup&gt; out is deliberate — the AOT default (Miscellaneous)
    /// then applies and no default is flipped by accident. But the very same tool's
    /// `validate xpp` raises XML003 on that table, so `generate table` warns about
    /// the consequence instead of leaving the caller to hit it one command later.
    /// This test pins the tension the warning describes: if XML003 ever stops
    /// firing here, the warning is stale and must go.
    /// </summary>
    [Fact]
    public void Pattern_None_table_trips_the_XML003_rule_the_command_warns_about()
    {
        var withoutPattern = XppScaffolder.Table("FmThing", label: "@Fm:Thing").ToString();
        var violations = D365FO.Core.Validation.XppValidator.Validate(
            withoutPattern, D365FO.Core.Validation.XppValidator.CodeTypeXmlTable);
        Assert.Contains(violations, v => v.Rule == "XML003" && v.Severity == "error");

        var withPattern = XppScaffolder.Table("FmThing", label: "@Fm:Thing",
            pattern: TablePattern.Miscellaneous).ToString();
        var clean = D365FO.Core.Validation.XppValidator.Validate(
            withPattern, D365FO.Core.Validation.XppValidator.CodeTypeXmlTable);
        Assert.DoesNotContain(clean, v => v.Rule == "XML003");
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

    // --- issue #110: generated tables must carry the same five default field
    //     groups Visual Studio / the AOT stamps on every new table, and must
    //     NOT emit shouldThrowExceptionOnZeroDelete (not part of the fresh
    //     VS scaffold — ground-truthed against shipped standard-model tables).

    [Fact]
    public void Table_emits_the_five_default_AOT_field_groups_in_order()
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        var groupNames = doc.Root!.Element("FieldGroups")!.Elements("AxTableFieldGroup")
            .Select(g => g.Element("Name")!.Value).ToList();

        Assert.Equal(
            new[] { "AutoReport", "AutoLookup", "AutoIdentification", "AutoSummary", "AutoBrowse", "Overview", "General" },
            groupNames);
    }

    [Theory]
    [InlineData("AutoReport")]
    [InlineData("AutoLookup")]
    [InlineData("AutoIdentification")]
    [InlineData("AutoSummary")]
    [InlineData("AutoBrowse")]
    public void Default_AOT_field_groups_are_empty_even_when_the_table_has_fields(string groupName)
    {
        // Ground truth: VS does NOT auto-populate these groups with fields, even
        // once the table has real fields (verified against shipped tables, e.g.
        // ApplicationCommon\AgentFeedUserPreference.xml,
        // ApplicationSuite\Foundation\AssetAddition.xml).
        var doc = XppScaffolder.Table("FmCustomer", pattern: TablePattern.Main);
        var group = doc.Root!.Element("FieldGroups")!.Elements("AxTableFieldGroup")
            .Single(g => g.Element("Name")!.Value == groupName);

        var fieldsEl = group.Element("Fields")!;
        Assert.False(fieldsEl.HasElements);
    }

    [Fact]
    public void AutoIdentification_field_group_has_AutoPopulate_Yes()
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        var group = doc.Root!.Element("FieldGroups")!.Elements("AxTableFieldGroup")
            .Single(g => g.Element("Name")!.Value == "AutoIdentification");

        Assert.Equal("Yes", group.Element("AutoPopulate")!.Value);
    }

    [Theory]
    [InlineData("AutoReport")]
    [InlineData("AutoLookup")]
    [InlineData("AutoSummary")]
    [InlineData("AutoBrowse")]
    public void Only_AutoIdentification_field_group_carries_AutoPopulate(string groupName)
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        var group = doc.Root!.Element("FieldGroups")!.Elements("AxTableFieldGroup")
            .Single(g => g.Element("Name")!.Value == groupName);

        Assert.Null(group.Element("AutoPopulate"));
    }

    [Fact]
    public void Overview_and_General_field_groups_stay_populated_for_form_compatibility()
    {
        // Not part of the AOT default scaffold, but required so this CLI's own
        // generated forms (which reference them via <DataGroup>) compile — see
        // issue #91 follow-up.
        var doc = XppScaffolder.Table("FmCustomer", pattern: TablePattern.Main);
        var overview = doc.Root!.Element("FieldGroups")!.Elements("AxTableFieldGroup")
            .Single(g => g.Element("Name")!.Value == "Overview");
        var general = doc.Root!.Element("FieldGroups")!.Elements("AxTableFieldGroup")
            .Single(g => g.Element("Name")!.Value == "General");

        Assert.True(overview.Element("Fields")!.HasElements);
        Assert.True(general.Element("Fields")!.HasElements);
    }

    [Fact]
    public void Generated_table_does_not_emit_shouldThrowExceptionOnZeroDelete()
    {
        // VS/AOT does not generate this method on a fresh table — it's a
        // developer override some standard tables happen to add, not a
        // scaffold default. Confirmed by sampling shipped standard-model
        // tables: only a small minority carry it.
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        Assert.DoesNotContain("shouldThrowExceptionOnZeroDelete", doc.ToString());
    }

    // --- issue #91: AxTableField is abstract — every field needs a concrete
    //     i:type discriminator or the metadata reader rejects the whole table.

    private static readonly System.Xml.Linq.XNamespace Xsi =
        "http://www.w3.org/2001/XMLSchema-instance";

    [Fact]
    public void Root_declares_the_XMLSchema_instance_namespace()
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        var decl = doc.Root!.Attribute(System.Xml.Linq.XNamespace.Xmlns + "i");
        Assert.NotNull(decl);
        Assert.Equal(Xsi.NamespaceName, decl!.Value);
    }

    [Fact]
    public void Every_field_carries_a_concrete_AxTableField_itype()
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        var fields = doc.Root!.Element("Fields")!.Elements("AxTableField").ToList();
        Assert.NotEmpty(fields);
        foreach (var f in fields)
        {
            var t = f.Attribute(Xsi + "type")?.Value;
            Assert.False(string.IsNullOrEmpty(t), "field missing i:type");
            Assert.StartsWith("AxTableField", t);
            Assert.NotEqual("AxTableField", t); // never the abstract base
        }
    }

    [Theory]
    [InlineData("String",      "AxTableFieldString")]
    [InlineData("Int",         "AxTableFieldInt")]
    [InlineData("Int64",       "AxTableFieldInt64")]
    [InlineData("Real",        "AxTableFieldReal")]
    [InlineData("Enum",        "AxTableFieldEnum")]
    [InlineData("Date",        "AxTableFieldDate")]
    [InlineData("UtcDateTime", "AxTableFieldUtcDateTime")]
    [InlineData("Guid",        "AxTableFieldGuid")]
    [InlineData("Container",   "AxTableFieldContainer")]
    public void Resolver_base_type_maps_to_concrete_field_subtype(string baseType, string expected)
    {
        var custom = new[] { new TableFieldSpec("Foo", "SomeEdt", null, false) };
        var doc = XppScaffolder.Table("FmThing", fields: custom, edtBaseTypeResolver: _ => baseType);
        var field = doc.Root!.Element("Fields")!.Element("AxTableField")!;
        Assert.Equal(expected, field.Attribute(Xsi + "type")!.Value);
    }

    [Fact]
    public void Unknown_edt_without_resolver_falls_back_to_string()
    {
        var custom = new[] { new TableFieldSpec("Foo", "TotallyUnknownEdtXyz", null, false) };
        var doc = XppScaffolder.Table("FmThing", fields: custom);
        var field = doc.Root!.Element("Fields")!.Element("AxTableField")!;
        Assert.Equal("AxTableFieldString", field.Attribute(Xsi + "type")!.Value);
    }

    [Fact]
    public void Well_known_edt_name_heuristic_applies_without_resolver()
    {
        // AmountMST is a real-typed system EDT — the name heuristic should pick Real.
        var custom = new[] { new TableFieldSpec("Amount", "AmountMST", null, false) };
        var doc = XppScaffolder.Table("FmThing", fields: custom);
        var field = doc.Root!.Element("Fields")!.Element("AxTableField")!;
        Assert.Equal("AxTableFieldReal", field.Attribute(Xsi + "type")!.Value);
    }

    [Fact]
    public void Resolver_null_return_falls_back_to_name_heuristic()
    {
        // Resolver present but returns null (EDT not in index) → heuristic over
        // the EDT name still applies (TransDate → Date).
        var custom = new[] { new TableFieldSpec("Posted", "TransDate", null, false) };
        var doc = XppScaffolder.Table("FmThing", fields: custom, edtBaseTypeResolver: _ => null);
        var field = doc.Root!.Element("Fields")!.Element("AxTableField")!;
        Assert.Equal("AxTableFieldDate", field.Attribute(Xsi + "type")!.Value);
    }

    [Fact]
    public void ConfigurationKey_and_FormRef_are_emitted_when_supplied()
    {
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main,
            configurationKey: "FmModule", formRef: "FmThingForm");
        var root = doc.Root!;
        Assert.Equal("FmModule", root.Element("ConfigurationKey")!.Value);
        Assert.Equal("FmThingForm", root.Element("FormRef")!.Value);

        // Both belong to the scalar-property block, ahead of the collections.
        var names = root.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(names.IndexOf("ConfigurationKey") < names.IndexOf("Fields"));
        Assert.True(names.IndexOf("FormRef") < names.IndexOf("Fields"));
    }

    [Fact]
    public void ConfigurationKey_and_FormRef_are_omitted_when_not_supplied()
    {
        // An absent element means the AOT default (ungated, no drill-down form);
        // emitting an empty one would flip a default by accident.
        var doc = XppScaffolder.Table("FmThing", pattern: TablePattern.Main);
        Assert.Null(doc.Root!.Element("ConfigurationKey"));
        Assert.Null(doc.Root!.Element("FormRef"));
    }
}
