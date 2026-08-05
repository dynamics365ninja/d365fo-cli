using D365FO.Core.Scaffolding;
using D365FO.Cli.Commands.Generate;
using System.Xml.Linq;
using Xunit;

namespace D365FO.Cli.Tests;

/// <summary>
/// Golden-file-style snapshot tests for Phase 2 + Phase 6 scaffolders.
/// Parses the output XML and asserts structural elements are present to
/// catch silent regressions if the scaffolder templates are changed.
/// </summary>
// Shares a collection with LabelBatchCreateTests: both override the process-wide
// D365FO_INDEX_DB env var, so running them in parallel would race on that global state.
[Collection("EnvIndexDb")]
public class ScaffoldingSnapshotTests
{
    // ---- SysOperation (Phase 2) ----

    [Fact]
    public void SysOperation_contract_has_DataContractAttribute_and_class()
    {
        var doc = SysOperationScaffolder.Contract("MyContract");
        var root = doc.Root!;
        Assert.Equal("AxClass", root.Name.LocalName);
        Assert.Equal("MyContract", root.Element("Name")!.Value);
        var src = root.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.Contains("[DataContractAttribute]", src);
    }

    [Fact]
    public void SysOperation_service_has_single_process_method_and_correct_extends()
    {
        var doc = SysOperationScaffolder.Service("MyService", "MyContract", "process");
        var root = doc.Root!;
        Assert.Equal("AxClass", root.Name.LocalName);
        Assert.Equal("SysOperationServiceBase", root.Element("Extends")!.Value);
        var methods = root.Element("SourceCode")!.Element("Methods")!.Elements("Method").ToList();
        var method = Assert.Single(methods);
        Assert.Equal("process", method.Element("Name")!.Value);
    }

    [Fact]
    public void SysOperation_controller_extends_SysOperationServiceController()
    {
        var doc = SysOperationScaffolder.Controller("MyController", "MyService", "process");
        var root = doc.Root!;
        Assert.Equal("SysOperationServiceController", root.Element("Extends")!.Value);
        var newMethod = root.Element("SourceCode")!.Element("Methods")!
            .Elements("Method").First(m => m.Element("Name")!.Value == "new");
        Assert.Contains("classStr(MyService)", newMethod.Element("Source")!.Value);
    }

    // ---- EDT (Phase 2) ----

    [Fact]
    public void Edt_has_correct_name_and_extends()
    {
        var doc = XppScaffolder.Edt("MyEdt", "Name");
        var root = doc.Root!;
        Assert.Equal("AxEdt", root.Name.LocalName);
        Assert.Equal("AxEdtString", root.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value);
        Assert.Equal("MyEdt", root.Element("Name")!.Value);
        Assert.Equal("Name", root.Element("Extends")!.Value);
    }

    [Theory]
    [InlineData("String", "AxEdtString")]
    [InlineData("Int", "AxEdtInt")]
    [InlineData("Int64", "AxEdtInt64")]
    [InlineData("Real", "AxEdtReal")]
    [InlineData("Date", "AxEdtDate")]
    [InlineData("UtcDateTime", "AxEdtUtcDateTime")]
    [InlineData("Boolean", "AxEdtEnum")]
    [InlineData("Time", "AxEdtTime")]
    [InlineData("Guid", "AxEdtGuid")]
    [InlineData("Container", "AxEdtContainer")]
    [InlineData("Enum", "AxEdtEnum")]
    public void Edt_base_type_emits_matching_concrete_i_type(string baseType, string expectedType)
    {
        var doc = XppScaffolder.Edt("MyEdt", null, baseType);
        Assert.Equal(
            expectedType,
            doc.Root!.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value);
    }

    [Fact]
    public void Edt_enum_type_emits_EnumType_element_after_TableReferences()
    {
        // Enum-type EDTs require <EnumType> (the backing X++ enum name) after <TableReferences>.
        // Without it VS metadata reader cannot bind the EDT to the enum. (issue #70)
        var doc = XppScaffolder.Edt("MyEnumEdt", "NoYesId", "Enum", null, null, "NoYes");
        var names = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Contains("EnumType", names);
        Assert.True(names.IndexOf("EnumType") > names.IndexOf("TableReferences"),
            "<EnumType> must appear after <TableReferences>");
        Assert.Equal("NoYes", doc.Root.Element("EnumType")!.Value);
    }

    [Fact]
    public void Edt_enum_type_defaults_to_NoYes_when_extends_NoYesId()
    {
        // When --enum-type is omitted and extends is NoYesId, NoYes is inferred.
        var doc = XppScaffolder.Edt("MyEnumEdt", "NoYesId", "Enum");
        Assert.Equal("NoYes", doc.Root!.Element("EnumType")!.Value);
    }

    [Fact]
    public void Edt_enum_type_without_extends_and_without_enum_type_does_not_emit_EnumType()
    {
        var doc = XppScaffolder.Edt("MyEnumEdt", null, "Enum");
        Assert.Null(doc.Root!.Element("EnumType"));
    }

    [Fact]
    public void Edt_enum_type_with_custom_extends_does_not_guess_EnumType()
    {
        var doc = XppScaffolder.Edt("MyEnumEdt", "ABCModelType", "Enum");
        Assert.Null(doc.Root!.Element("EnumType"));
    }

    [Fact]
    public void Edt_non_enum_type_does_not_emit_EnumType_element()
    {
        // Non-enum EDTs must not get a spurious <EnumType> element.
        var doc = XppScaffolder.Edt("MyStringEdt", null, "String");
        Assert.Null(doc.Root!.Element("EnumType"));
    }

    [Fact]
    public void Edt_base_type_Date_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyDateEdt", null, "Date");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Int64_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyInt64Edt", null, "Int64");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_String_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyStringEdt", null, "String");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Int_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyIntEdt", null, "Int");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Real_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyRealEdt", null, "Real");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Time_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyTimeEdt", null, "Time");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_UtcDateTime_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyDateTimeEdt", null, "UtcDateTime");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Boolean_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyBoolEdt", null, "Boolean");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Boolean_without_extends_defaults_EnumType_to_NoYes()
    {
        var doc = XppScaffolder.Edt("MyBoolEdt", null, "Boolean");
        Assert.Equal("NoYes", doc.Root!.Element("EnumType")!.Value);
    }

    [Fact]
    public void Edt_base_type_Enum_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyEnumEdt", null, "Enum");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Guid_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyGuidEdt", null, "Guid");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Fact]
    public void Edt_base_type_Container_has_no_default_extends()
    {
        var doc = XppScaffolder.Edt("MyContainerEdt", null, "Container");
        Assert.Null(doc.Root!.Element("Extends"));
    }

    [Theory]
    [InlineData("Integer", "AxEdtInt")]
    [InlineData("Int64", "AxEdtInt64")]
    [InlineData("Amount", "AxEdtReal")]
    [InlineData("Date", "AxEdtDate")]
    [InlineData("TransDate", "AxEdtDate")]
    [InlineData("UtcDateTime", "AxEdtUtcDateTime")]
    [InlineData("TransDateTime", "AxEdtUtcDateTime")]
    [InlineData("NoYesId", "AxEdtEnum")]
    [InlineData("TimeOfDay", "AxEdtTime")]
    [InlineData("Guid", "AxEdtGuid")]
    [InlineData("Container", "AxEdtContainer")]
    public void Edt_extends_infers_non_string_i_type(string extends, string expectedType)
    {
        var doc = XppScaffolder.Edt("MyEdt", extends);
        Assert.Equal(
            expectedType,
            doc.Root!.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value);
    }

    [Fact]
    public void Edt_with_size_has_StringSize_element()
    {
        var doc = XppScaffolder.Edt("MyEdt", null, null, 50);
        Assert.NotNull(doc.Root!.Element("StringSize"));
        Assert.Equal("50", doc.Root.Element("StringSize")!.Value);
    }

    [Fact]
    public void Edt_root_has_XMLSchema_instance_namespace()
    {
        var doc = XppScaffolder.Edt("MyEdt", "Name", null, 10, "My label");
        Assert.Equal(
            "http://www.w3.org/2001/XMLSchema-instance",
            doc.Root!.GetNamespaceOfPrefix("i")!.NamespaceName);
    }

    [Fact]
    public void Edt_emits_base_members_before_derived_StringSize()
    {
        // DataContractSerializer serializes base-class members (AxEdt: Name/Extends/Label)
        // before derived members (AxEdtString.StringSize): Label must precede StringSize.
        var doc = XppScaffolder.Edt("MyEdt", "Name", null, 10, "My label");
        var names = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.Equal(new[] { "Name", "Extends", "Label", "StringSize" }, names);
    }

    [Fact]
    public void ScaffoldFileWriter_rejects_abstract_AxEdt_root()
    {
        var doc = new XDocument(new XElement("AxEdt", new XElement("Name", "Bad")));
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d365fo-cli-test-abstract-edt.xml");
        if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScaffoldFileWriter.Write(doc, tmp, overwrite: true));
        Assert.Contains("AxEdt", ex.Message);
        Assert.False(System.IO.File.Exists(tmp));
    }

    [Fact]
    public void ScaffoldFileWriter_rejects_abstract_AxEdtExtension_root()
    {
        var raw = "<?xml version=\"1.0\" encoding=\"utf-8\"?><AxEdtExtension><Name>Bad.Extension</Name></AxEdtExtension>";
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d365fo-cli-test-abstract-edt-ext.xml");
        if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScaffoldFileWriter.Write(raw, tmp, overwrite: true));
        Assert.Contains("AxEdtExtension", ex.Message);
        Assert.False(System.IO.File.Exists(tmp));
    }

    [Fact]
    public void ScaffoldFileWriter_rejects_AxEnum_without_xsi_namespace()
    {
        // issue #70: VS's metadata reader cannot open an AxEnum whose root is
        // missing the XMLSchema-instance declaration.
        var doc = new XDocument(new XElement("AxEnum",
            new XElement("Name", "Bad"),
            new XElement("IsExtensible", "true")));
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d365fo-cli-test-enum-no-xsi.xml");
        if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScaffoldFileWriter.Write(doc, tmp, overwrite: true));
        Assert.Contains("XMLSchema-instance", ex.Message);
        Assert.False(System.IO.File.Exists(tmp));
    }

    [Fact]
    public void ScaffoldFileWriter_rejects_AxTable_without_xsi_namespace_string_overload()
    {
        // AxTableField is polymorphic: without the declaration its i:type
        // discriminators cannot resolve (issue #91).
        var raw = "<?xml version=\"1.0\" encoding=\"utf-8\"?><AxTable><Name>Bad</Name></AxTable>";
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d365fo-cli-test-table-no-xsi.xml");
        if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScaffoldFileWriter.Write(raw, tmp, overwrite: true));
        Assert.Contains("AxTable", ex.Message);
        Assert.False(System.IO.File.Exists(tmp));
    }

    [Fact]
    public void ScaffoldFileWriter_rejects_NoYes_spelling_in_IsExtensible()
    {
        // issue #70: IsExtensible is a CLR bool, so "Yes" is not a valid encoding
        // even though most AOT properties use the NoYes spelling.
        var doc = new XDocument(new XElement("AxEnum",
            new XAttribute(XNamespace.Xmlns + "i", "http://www.w3.org/2001/XMLSchema-instance"),
            new XElement("Name", "Bad"),
            new XElement("IsExtensible", "Yes")));
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d365fo-cli-test-enum-yesno.xml");
        if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScaffoldFileWriter.Write(doc, tmp, overwrite: true));
        Assert.Contains("IsExtensible", ex.Message);
        Assert.Contains("true", ex.Message);
        Assert.False(System.IO.File.Exists(tmp));
    }

    [Fact]
    public void ScaffoldFileWriter_rejects_NoYes_spelling_in_IsExtensible_string_overload()
    {
        var raw = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                  "<AxEnum xmlns:i=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                  "<Name>Bad</Name><IsExtensible>No</IsExtensible></AxEnum>";
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "d365fo-cli-test-enum-yesno-raw.xml");
        if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        var ex = Assert.Throws<System.InvalidOperationException>(() => ScaffoldFileWriter.Write(raw, tmp, overwrite: true));
        Assert.Contains("IsExtensible", ex.Message);
        Assert.False(System.IO.File.Exists(tmp));
    }

    [Fact]
    public void ScaffoldFileWriter_accepts_scaffolder_enum_output()
    {
        // The guards must not fire on what the generators actually emit —
        // otherwise the whole enum/table/EDT generate path is dead.
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-enum-ok-{System.Guid.NewGuid():N}.xml");
        try
        {
            var res = ScaffoldFileWriter.Write(
                XppScaffolder.Enum("MyEnum", new[] { new EnumValueSpec("None", 0) }, isExtensible: true),
                tmp, overwrite: true);
            Assert.True(res.Bytes > 0);

            var tablePath = System.IO.Path.ChangeExtension(tmp, ".table.xml");
            try
            {
                ScaffoldFileWriter.Write(
                    XppScaffolder.Table("MyTable", fields: new[] { new TableFieldSpec("AccountNum", "Name", null, true) }),
                    tablePath, overwrite: true);
            }
            finally
            {
                if (System.IO.File.Exists(tablePath)) System.IO.File.Delete(tablePath);
            }
        }
        finally
        {
            if (System.IO.File.Exists(tmp)) System.IO.File.Delete(tmp);
        }
    }

    [Fact]
    public void GenerateEnumCommand_with_verify_still_writes_when_the_metadata_runtime_is_absent()
    {
        // --verify is strictly belt-and-suspenders: generation has to keep working
        // offline (CI, agent sessions, machines without the VS metadata assemblies),
        // so an unavailable runtime must never turn a good write into a failure.
        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-verify-{System.Guid.NewGuid():N}.xml");
        var oldFlag = System.Environment.GetEnvironmentVariable("D365FO_BRIDGE_ENABLED");
        try
        {
            System.Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", "0");
            var exit = new GenerateEnumCommand().Execute(null!, new GenerateEnumCommand.Settings
            {
                Name = "MyVerifiedEnum",
                Values = new[] { "None:0" },
                Out = outPath,
                Overwrite = true,
                Verify = true,
            });

            Assert.Equal(0, exit);
            Assert.True(System.IO.File.Exists(outPath));
            Assert.Equal("AxEnum", XDocument.Load(outPath).Root!.Name.LocalName);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("D365FO_BRIDGE_ENABLED", oldFlag);
            if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);
        }
    }

    [Fact]
    public void GenerateEdtCommand_without_index_still_infers_NoYes_from_NoYesId()
    {
        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-edt-{System.Guid.NewGuid():N}.xml");
        if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);

        var cmd = new GenerateEdtCommand();
        var settings = new GenerateEdtCommand.Settings
        {
            Name = "MyEnumEdt",
            Extends = "NoYesId",
            BaseType = "Enum",
            Out = outPath,
            Overwrite = true,
            EnumType = null,
        };

        var oldDb = System.Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
        var forcedMissingDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-missing-{System.Guid.NewGuid():N}.sqlite");
        if (System.IO.File.Exists(forcedMissingDb)) System.IO.File.Delete(forcedMissingDb);

        try
        {
            System.Environment.SetEnvironmentVariable("D365FO_INDEX_DB", forcedMissingDb);
            var exit = cmd.Execute(null!, settings);
            Assert.Equal(0, exit);

            var doc = XDocument.Load(outPath);
            Assert.Equal("AxEdt", doc.Root!.Name.LocalName);
            Assert.Equal("AxEdtEnum", doc.Root!.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value);
            Assert.Equal("NoYes", doc.Root.Element("EnumType")!.Value);
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("D365FO_INDEX_DB", oldDb);
            if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);
        }
    }

    // ---- Enum (Phase 2) ----

    [Fact]
    public void Enum_has_IsExtensible_and_values_in_order()
    {
        var vals = new[] { new EnumValueSpec("None", 0), new EnumValueSpec("Yes", 1), new EnumValueSpec("No", 2) };
        var doc = XppScaffolder.Enum("MyEnum", vals, isExtensible: true);
        var root = doc.Root!;
        Assert.Equal("AxEnum", root.Name.LocalName);
        // IsExtensible is a CLR bool → DataContractSerializer expects true/false, not Yes/No.
        Assert.Equal("true", root.Element("IsExtensible")!.Value);
        // VS emits the XMLSchema-instance namespace on every AxEnum root.
        Assert.Equal(
            "http://www.w3.org/2001/XMLSchema-instance",
            root.GetNamespaceOfPrefix("i")!.NamespaceName);
        var items = root.Element("EnumValues")!.Elements().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("0", items[0].Element("Value")!.Value);
        Assert.Equal("1", items[1].Element("Value")!.Value);
    }

    [Fact]
    public void Enum_extensible_sets_UseEnumValue_No()
    {
        // VS build validation: "UseEnumValue property must be set to 'No' when the
        // IsExtensible property is 'True'." UseEnumValue is a NoYes-style enum property
        // (unlike the CLR-bool IsExtensible), so it takes "Yes"/"No", not "true"/"false".
        var vals = new[] { new EnumValueSpec("Draft", 0), new EnumValueSpec("Validated", 1), new EnumValueSpec("Completed", 2) };
        var doc = XppScaffolder.Enum("MyEnumStatusType", vals, isExtensible: true, label: "test status");
        var root = doc.Root!;
        Assert.Equal("true", root.Element("IsExtensible")!.Value);
        Assert.Equal("No", root.Element("UseEnumValue")!.Value);
    }

    [Fact]
    public void Enum_non_extensible_does_not_emit_UseEnumValue()
    {
        // UseEnumValue is only forced when IsExtensible=true; a non-extensible enum has no
        // build-rule requiring it, so the element is omitted (VS defaults it to Yes).
        var vals = new[] { new EnumValueSpec("None", 0) };
        var doc = XppScaffolder.Enum("MyEnum", vals, isExtensible: false);
        var root = doc.Root!;
        Assert.Equal("false", root.Element("IsExtensible")!.Value);
        Assert.Null(root.Element("UseEnumValue"));
    }

    // ---- Query (Phase 2) ----

    [Fact]
    public void Query_has_root_data_source()
    {
        var doc = QueryScaffolder.Query("CustQuery", new[] { new QueryDataSourceSpec("CustTable") });
        var root = doc.Root!;
        Assert.Equal("AxQuery", root.Name.LocalName);
        Assert.Equal("CustQuery", root.Element("Name")!.Value);
        var ds = root.Element("DataSources")!.Elements().First();
        Assert.Equal("AxQuerySimpleRootDataSource", ds.Name.LocalName);
        Assert.Equal("CustTable", ds.Element("Table")!.Value);
    }

    [Fact]
    public void Query_join_produces_embedded_data_source()
    {
        var ds = new[]
        {
            new QueryDataSourceSpec("CustTable"),
            new QueryDataSourceSpec("CustTrans", ParentDs: "CustTable"),
        };
        var doc = QueryScaffolder.Query("CustJoinQuery", ds);
        var root = doc.Root!;
        var rootDs = root.Element("DataSources")!.Elements().First();
        var embedded = rootDs.Element("DataSources")!.Elements().First();
        Assert.Equal("AxQuerySimpleEmbeddedDataSource", embedded.Name.LocalName);
        Assert.Equal("CustTrans", embedded.Element("Table")!.Value);
    }

    // ---- BusinessEvent (Phase 6) ----

    [Fact]
    public void BusinessEvent_class_extends_BusinessEventsBase()
    {
        var doc = BusinessEventScaffolder.EventClass("MyEvent", "MyEventContract", "Payments");
        var root = doc.Root!;
        Assert.Equal("AxClass", root.Name.LocalName);
        Assert.Equal("BusinessEventsBase", root.Element("Extends")!.Value);
        var decl = root.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.Contains("[BusinessEvents(", decl);
        Assert.Contains("classStr(MyEvent)", decl);
        Assert.Contains("classStr(MyEventContract)", decl);
    }

    [Fact]
    public void BusinessEvent_contract_has_DataContractAttribute()
    {
        var doc = BusinessEventScaffolder.ContractClass("MyEventContract");
        var root = doc.Root!;
        Assert.Equal("AxClass", root.Name.LocalName);
        var decl = root.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.Contains("[DataContractAttribute]", decl);
    }

    // ---- RunBase (Phase 6) ----

    [Fact]
    public void RunBase_extends_RunBase_by_default()
    {
        var doc = RunBaseScaffolder.RunBaseClass("MyRunBase", false);
        var root = doc.Root!;
        Assert.Equal("RunBase", root.Element("Extends")!.Value);
    }

    [Fact]
    public void RunBase_batch_extends_RunBaseBatch_and_has_canGoBatch()
    {
        var doc = RunBaseScaffolder.RunBaseClass("MyBatch", true);
        var root = doc.Root!;
        Assert.Equal("RunBaseBatch", root.Element("Extends")!.Value);
        var methods = root.Element("SourceCode")!.Element("Methods")!.Elements("Method");
        Assert.Contains(methods, m => m.Element("Name")!.Value == "canGoBatch");
    }

    // ---- SecurityPolicy (Phase 6) ----

    [Fact]
    public void SecurityPolicy_has_constrained_table_and_query()
    {
        var doc = SecurityPolicyScaffolder.Policy("MyPolicy", "CustTable", "MyCustPolicyQuery");
        var root = doc.Root!;
        Assert.Equal("AxSecurityPolicy", root.Name.LocalName);
        // ConstrainedTable is a NoYes flag; the table's name belongs in PrimaryTable. Putting
        // the name in ConstrainedTable made the provider reject the file outright.
        Assert.Equal("Yes", root.Element("ConstrainedTable")!.Value);
        Assert.Equal("CustTable", root.Element("PrimaryTable")!.Value);
        Assert.Equal("MyCustPolicyQuery", root.Element("Query")!.Value);
    }

    // ---- CustomService (Phase 6) ----

    [Fact]
    public void CustomService_class_is_plain_with_no_service_attribute()
    {
        // There is no class-level [ServiceAttribute] in X++ — confirmed absent from every
        // shipped attribute-class AOT file and every real AxService-registered class on a
        // live D365FO platform. Exposure comes entirely from the AxService/AxServiceGroup XML.
        var doc = CustomServiceScaffolder.ServiceClass("VendorService",
            new[] { new OperationSpec("lookupVendor", "void") });
        var root = doc.Root!;
        Assert.Equal("AxClass", root.Name.LocalName);
        var decl = root.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.DoesNotContain("[ServiceAttribute]", decl);
        var methods = root.Element("SourceCode")!.Element("Methods")!.Elements("Method").ToList();
        Assert.Contains(methods, m => m.Element("Name")!.Value == "lookupVendor");
        var lookupSrc = methods.First(m => m.Element("Name")!.Value == "lookupVendor").Element("Source")!.Value;
        Assert.DoesNotContain("[SysEntryPointAttribute", lookupSrc);
    }

    [Fact]
    public void CustomService_xml_uses_real_platform_element_names()
    {
        // Confirmed against shipped AxService/AxServiceGroup XML on a live D365FO platform
        // (e.g. DMFEntityWriterService.xml / DMFServiceGroup.xml): AxService wraps operations
        // in <ServiceOperations>, and AxServiceGroupService needs both <Name> (local id) and
        // <Service> (the actual reference to the AxService object) to link correctly.
        var serviceDoc = CustomServiceScaffolder.ServiceXml("VendorLookup", "VendorLookupService",
            new[] { new OperationSpec("lookupVendor", "void") });
        var serviceRoot = serviceDoc.Root!;
        Assert.NotNull(serviceRoot.Element("ServiceOperations"));
        Assert.Null(serviceRoot.Element("Operations"));

        var groupDoc = CustomServiceScaffolder.ServiceGroupXml("VendorLookupServiceGroup", "VendorLookup");
        var groupService = groupDoc.Root!.Element("Services")!.Element("AxServiceGroupService")!;
        Assert.Equal("VendorLookup", groupService.Element("Service")?.Value);
    }

    // ---- SysTest (issue #107) ----

    [Fact]
    public void SysTest_extends_SysTestCase_and_emits_default_test_method()
    {
        var doc = SysTestScaffolder.TestClass("CustServiceTest");
        var root = doc.Root!;
        Assert.Equal("AxClass", root.Name.LocalName);
        Assert.Equal("CustServiceTest", root.Element("Name")!.Value);
        Assert.Equal("SysTestCase", root.Element("Extends")!.Value);

        var decl = root.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.Contains("public class CustServiceTest extends SysTestCase", decl);
        Assert.DoesNotContain("SysTestCaseDataDependency", decl);
        Assert.DoesNotContain("AtlDataRootNode", decl);

        var methods = root.Element("SourceCode")!.Element("Methods")!.Elements("Method").ToList();
        var testMethod = Assert.Single(methods);
        Assert.Equal("subject_scenario_expectedResult", testMethod.Element("Name")!.Value);
        var src = testMethod.Element("Source")!.Value;
        Assert.Contains("[SysTestMethod]", src);
        Assert.Contains("// Arrange", src);
        Assert.Contains("// Act", src);
        Assert.Contains("// Assert", src);
    }

    [Fact]
    public void SysTest_data_area_id_emits_SysTestCaseDataDependency_attribute()
    {
        var doc = SysTestScaffolder.TestClass("CustServiceTest", dataAreaId: "USMF");
        var decl = doc.Root!.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.Contains("[SysTestCaseDataDependency('USMF')]", decl);
    }

    [Fact]
    public void SysTest_without_data_area_id_omits_attribute_entirely()
    {
        var doc = SysTestScaffolder.TestClass("CustServiceTest");
        var decl = doc.Root!.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.DoesNotContain("SysTestCaseDataDependency", decl);
    }

    [Fact]
    public void SysTest_atl_adds_field_and_setUpTestCase_calling_super_first()
    {
        var doc = SysTestScaffolder.TestClass("CustServiceTest", atl: true);
        var decl = doc.Root!.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.Contains("AtlDataRootNode data;", decl);

        var methods = doc.Root.Element("SourceCode")!.Element("Methods")!.Elements("Method").ToList();
        var setUp = methods.First(m => m.Element("Name")!.Value == "setUpTestCase");
        var src = setUp.Element("Source")!.Value;
        Assert.True(src.IndexOf("super();", StringComparison.Ordinal) < src.IndexOf("AtlDataRootNode::construct();", StringComparison.Ordinal),
            "super() must be called before AtlDataRootNode::construct()");
    }

    [Fact]
    public void SysTest_without_atl_has_no_field_or_setUpTestCase()
    {
        var doc = SysTestScaffolder.TestClass("CustServiceTest", atl: false);
        var decl = doc.Root!.Element("SourceCode")!.Element("Declaration")!.Value;
        Assert.DoesNotContain("AtlDataRootNode", decl);

        var methods = doc.Root.Element("SourceCode")!.Element("Methods")!.Elements("Method");
        Assert.DoesNotContain(methods, m => m.Element("Name")!.Value == "setUpTestCase");
    }

    [Fact]
    public void SysTest_subject_from_resolved_method_names_the_test_method()
    {
        var doc = SysTestScaffolder.TestClass("CustServiceCreateCustomerTest", subject: "createCustomer");
        var methods = doc.Root!.Element("SourceCode")!.Element("Methods")!.Elements("Method").ToList();
        var testMethod = Assert.Single(methods);
        Assert.Equal("createCustomer_scenario_expectedResult", testMethod.Element("Name")!.Value);
    }

    [Fact]
    public void GenerateSysTestCommand_class_not_found_fails_with_clear_error()
    {
        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-systest-{System.Guid.NewGuid():N}.xml");
        var cmd = new GenerateSysTestCommand();
        var settings = new GenerateSysTestCommand.Settings
        {
            Name = "CustServiceTest",
            Class = "ThisClassDoesNotExistAnywhere",
            Out = outPath,
            Overwrite = true,
        };

        var oldDb = System.Environment.GetEnvironmentVariable("D365FO_INDEX_DB");
        var forcedMissingDb = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-missing-{System.Guid.NewGuid():N}.sqlite");
        if (System.IO.File.Exists(forcedMissingDb)) System.IO.File.Delete(forcedMissingDb);

        try
        {
            System.Environment.SetEnvironmentVariable("D365FO_INDEX_DB", forcedMissingDb);
            var exit = cmd.Execute(null!, settings);
            Assert.Equal(1, exit);
            Assert.False(System.IO.File.Exists(outPath));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("D365FO_INDEX_DB", oldDb);
            if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);
        }
    }

    [Fact]
    public void GenerateSysTestCommand_class_and_table_together_is_rejected()
    {
        var cmd = new GenerateSysTestCommand();
        var settings = new GenerateSysTestCommand.Settings
        {
            Name = "CustServiceTest",
            Class = "CustService",
            Table = "CustTable",
            Out = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-systest-{System.Guid.NewGuid():N}.xml"),
        };
        var exit = cmd.Execute(null!, settings);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void GenerateSysTestCommand_method_without_class_or_table_is_rejected()
    {
        var cmd = new GenerateSysTestCommand();
        var settings = new GenerateSysTestCommand.Settings
        {
            Name = "CustServiceTest",
            Method = "createCustomer",
            Out = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-systest-{System.Guid.NewGuid():N}.xml"),
        };
        var exit = cmd.Execute(null!, settings);
        Assert.Equal(1, exit);
    }

    [Fact]
    public void GenerateSysTestCommand_writes_file_and_reports_extends_SysTestCase()
    {
        var outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"d365fo-cli-test-systest-{System.Guid.NewGuid():N}.xml");
        if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);

        var cmd = new GenerateSysTestCommand();
        var settings = new GenerateSysTestCommand.Settings
        {
            Name = "CustServiceTest",
            DataAreaId = "USMF",
            Atl = true,
            Out = outPath,
            Overwrite = true,
        };

        try
        {
            var exit = cmd.Execute(null!, settings);
            Assert.Equal(0, exit);

            var doc = XDocument.Load(outPath);
            Assert.Equal("AxClass", doc.Root!.Name.LocalName);
            Assert.Equal("SysTestCase", doc.Root.Element("Extends")!.Value);
            var decl = doc.Root.Element("SourceCode")!.Element("Declaration")!.Value;
            Assert.Contains("[SysTestCaseDataDependency('USMF')]", decl);
            Assert.Contains("AtlDataRootNode data;", decl);
        }
        finally
        {
            if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);
        }
    }

    // ---- MenuItem (issue #102) ----

    [Fact]
    public void MenuItem_emits_Symbol_image_to_avoid_BPErrorMissingOrUnsupportedImage()
    {
        // Omitting <Image> (or leaving it defaulted to File) trips
        // BPErrorMissingOrUnsupportedImage during best-practice checks.
        // Symbol tells the compiler to inherit the icon, which is always valid.
        var doc = MenuItemScaffolder.MenuItem(MenuItemKind.Display, "MyMenuItem", "MyForm");
        var root = doc.Root!;
        Assert.Equal("AxMenuItemDisplay", root.Name.LocalName);
        Assert.Equal("Symbol", root.Element("Image")!.Element("ImageType")!.Value);
    }

    [Theory]
    [InlineData(MenuItemKind.Display, "AxMenuItemDisplay")]
    [InlineData(MenuItemKind.Action, "AxMenuItemAction")]
    [InlineData(MenuItemKind.Output, "AxMenuItemOutput")]
    public void MenuItem_all_kinds_emit_Symbol_image(MenuItemKind kind, string expectedRoot)
    {
        var doc = MenuItemScaffolder.MenuItem(kind, "MyMenuItem", "MyObject");
        var root = doc.Root!;
        Assert.Equal(expectedRoot, root.Name.LocalName);
        Assert.Equal("Symbol", root.Element("Image")!.Element("ImageType")!.Value);
    }
}
