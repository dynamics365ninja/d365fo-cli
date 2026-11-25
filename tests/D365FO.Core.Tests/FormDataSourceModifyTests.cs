using System.Text.Json.Nodes;
using System.Xml.Linq;
using D365FO.Core;
using D365FO.Core.Bridge;
using D365FO.Core.Index;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #283: adding a datasource to an existing form or form extension, and binding a
/// control to a display method. Exercised against a fake in-process bridge.
/// </summary>
[Collection(EnvironmentCollectionDefinition.Name)]
public sealed class FormDataSourceModifyTests : IDisposable
{
    private const string Ns = "Microsoft.Dynamics.AX.Metadata.V6";
    private const string Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private const string BaseForm =
        "<AxForm xmlns=\"" + Ns + "\" xmlns:i=\"" + Xsi + "\"><Name>FmVehicleList</Name>" +
        "<SourceCode><Methods xmlns=\"\"><Method><Name>classDeclaration</Name><Source>class X {}</Source></Method></Methods>" +
        "<DataSources xmlns=\"\"><DataSource><Name>Vehicle</Name><Methods>" +
        "<Method><Name>dsTitle</Name><Source>public display Name dsTitle(FmVehicle _v) { return ''; }</Source></Method>" +
        "<Method><Name>init</Name><Source>public void init() { super(); }</Source></Method>" +
        "</Methods></DataSource></DataSources></SourceCode>" +
        "<DataSources><AxFormDataSource xmlns=\"\"><Name>Vehicle</Name><Table>FmVehicle</Table><Fields /></AxFormDataSource></DataSources>" +
        "<Design><Pattern xmlns=\"\">SimpleList</Pattern><PatternVersion xmlns=\"\">1.1</PatternVersion><Controls xmlns=\"\">" +
        "<AxFormControl i:type=\"AxFormGridControl\"><Name>Grid</Name><Type>Grid</Type><Controls /></AxFormControl>" +
        "</Controls></Design></AxForm>";

    private const string Extension =
        "<AxFormExtension xmlns=\"" + Ns + "\" xmlns:i=\"" + Xsi + "\"><Name>FmVehicleList.Fleet</Name>" +
        "<ControlModifications><AxExtensionModification xmlns=\"\"><Name>Grid</Name></AxExtensionModification></ControlModifications>" +
        "<Controls><AxFormExtensionControl xmlns=\"\"><Name>Wrapper</Name><FormControl i:type=\"AxFormStringControl\"><Name>Existing</Name><Type>String</Type></FormControl><Parent>Grid</Parent></AxFormExtensionControl></Controls>" +
        "<DataSourceModifications><AxExtensionModification xmlns=\"\"><Name>Vehicle</Name></AxExtensionModification></DataSourceModifications>" +
        "<DataSourceReferences />" +
        "<DataSources><AxFormDataSource xmlns=\"\"><Name>Added</Name><Table>FmDriver</Table></AxFormDataSource></DataSources>" +
        "</AxFormExtension>";

    private const string SiblingExtension =
        "<AxFormExtension xmlns=\"" + Ns + "\"><Name>FmVehicleList.Other</Name>" +
        "<DataSources><AxFormDataSource xmlns=\"\"><Name>FromSibling</Name><Table>FmDriver</Table></AxFormDataSource></DataSources>" +
        "</AxFormExtension>";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"formds-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly MetadataRepository _repo;
    private readonly string? _prevCustomModels;

    public FormDataSourceModifyTests()
    {
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "meta.sqlite");
        _prevCustomModels = Environment.GetEnvironmentVariable("D365FO_CUSTOM_MODELS");
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", "FleetCustom");

        _repo = new MetadataRepository(_dbPath);
        _repo.EnsureSchema();
        _repo.ApplyExtract(ExtractBatch.Empty("FleetCustom") with
        {
            IsCustom = true,
            Tables = new[]
            {
                new ExtractedTable("FmVehicle", null, "x", Array.Empty<ExtractedTableField>()),
                new ExtractedTable("FmDriver", null, "x", Array.Empty<ExtractedTableField>())
                {
                    Methods = new[]
                    {
                        new ExtractedMethod("fullName", "public display Name fullName()", "Name", false),
                        new ExtractedMethod("validateWrite", "public boolean validateWrite()", "boolean", false),
                    },
                },
            },
            Classes = new[]
            {
                new ExtractedClass("FmDriver_Extension", null, false, true, "x", new[]
                {
                    new ExtractedMethod("age", "public display int age()", "int", false),
                }),
            },
            Forms = new[] { new ExtractedForm("FmVehicleList", "x", Array.Empty<ExtractedFormDataSource>()) },
            Extensions = new[]
            {
                new ExtractedObjectExtension("Form", "FmVehicleList", "FmVehicleList.Fleet", null),
                new ExtractedObjectExtension("Form", "FmVehicleList", "FmVehicleList.Other", null),
            },
        });
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("D365FO_CUSTOM_MODELS", _prevCustomModels);
        SqlitePool.ReleaseFor(_dbPath);
        try { Directory.Delete(_root, true); } catch { }
    }

    private static ObjectModifyEngine.ModifyRequest DataSource(string name, string table = "FmDriver") => new()
    {
        Operation = ObjectModifyEngine.Operation.AddDataSource,
        Kind = "form",
        ObjectName = "FmVehicleList",
        Member = name,
        Table = table,
    };

    private static ObjectModifyEngine.ModifyRequest Control(
        string dataSource, string? method = null, string type = "String") => new()
    {
        Operation = ObjectModifyEngine.Operation.AddControl,
        Kind = "form",
        ObjectName = "FmVehicleList",
        Member = "Grid_New",
        Type = type,
        Parent = "Grid",
        DataSource = dataSource,
        DataMethod = method,
    };

    private (ToolResult<object> Result, XElement? Written) Run(ObjectModifyEngine.ModifyRequest request)
    {
        JsonObject? written = null;
        using var bridge = ObjectModifyEngineTests.FakeBridge.Create(message =>
        {
            var method = (string?)message["method"];
            var args = message["params"];
            JsonObject result;
            if (method == "readObjectXml")
            {
                var xml = (string?)args?["name"] switch
                {
                    "FmVehicleList" => BaseForm,
                    "FmVehicleList.Fleet" => Extension,
                    "FmVehicleList.Other" => SiblingExtension,
                    _ => null,
                };
                result = xml is null
                    ? new JsonObject { ["ok"] = false, ["error"] = "NOT_FOUND", ["message"] = "not found" }
                    : new JsonObject { ["ok"] = true, ["xml"] = xml };
            }
            else
            {
                if (method is "createObject" or "updateObject") written = (JsonObject?)args?.DeepClone();
                result = new JsonObject { ["ok"] = true };
            }
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = message["id"]?.DeepClone(), ["result"] = result };
        });

        var outcome = ObjectModifyEngine.ModifyCore(request, _repo, bridge.Client, Path.Combine(_root, "journal.sqlite"));
        return (outcome, written is null ? null : XDocument.Parse((string)written["xml"]!).Root);
    }

    private static XElement Child(XElement parent, string name)
        => parent.Elements().Single(e => e.Name.LocalName == name);

    private static string[] Names(XElement root, string collection)
        => Child(root, collection).Elements().Select(e => Child(e, "Name").Value).ToArray();

    // ---- add-data-source ----------------------------------------------------

    [Fact]
    public void A_datasource_is_appended_to_a_form_without_touching_the_rest_of_it()
    {
        var (result, written) = Run(DataSource("Driver") with
        {
            Model = "FleetCustom",
            JoinSource = "vehicle",
            LinkType = "outerjoin",
        });

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(new[] { "Vehicle", "Driver" }, Names(written!, "DataSources"));
        var added = Child(written!, "DataSources").Elements().Last();
        Assert.Equal(XNamespace.None, added.Name.Namespace);
        Assert.Equal("FmDriver", Child(added, "Table").Value);
        Assert.Equal("Vehicle", Child(added, "JoinSource").Value);
        Assert.Equal("OuterJoin", Child(added, "LinkType").Value);

        var before = XDocument.Parse(BaseForm).Root!;
        foreach (var section in new[] { "SourceCode", "Design" })
            Assert.True(XNode.DeepEquals(Child(before, section), Child(written!, section)), section);
        Assert.True(XNode.DeepEquals(
            Child(before, "DataSources").Elements().Single(), Child(written!, "DataSources").Elements().First()));
    }

    [Fact]
    public void A_datasource_is_appended_to_a_form_extension_joined_to_the_base_form()
    {
        var (result, written) = Run(DataSource("Driver") with
        {
            ExtensionSuffix = "Fleet",
            ExtensionModel = "FleetCustom",
            JoinSource = "Vehicle",
            LinkType = "InnerJoin",
        });

        Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal("AxFormExtension", written!.Name.LocalName);
        Assert.Equal(new[] { "Added", "Driver" }, Names(written, "DataSources"));
        Assert.Equal(new[] { "Wrapper" }, Names(written, "Controls"));
        Assert.Equal(new[] { "Grid" }, Names(written, "ControlModifications"));
        Assert.Equal(new[] { "Vehicle" }, Names(written, "DataSourceModifications"));
    }

    [Theory]
    [InlineData("Vehicle")]     // the base form
    [InlineData("added")]       // this extension
    [InlineData("FromSibling")] // another extension of the same form
    public void A_datasource_name_the_form_already_uses_is_refused(string name)
    {
        var (result, written) = Run(DataSource(name) with { ExtensionSuffix = "Fleet", ExtensionModel = "FleetCustom" });

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.AlreadyExists, result.Error!.Code);
        Assert.Null(written);
    }

    [Theory]
    [InlineData("Driver", "NoSuchTable", null, null, D365FoErrorCodes.TableNotFound)]
    [InlineData("Driver", "FmDriver", "NoSuchSource", null, D365FoErrorCodes.DataSourceNotFound)]
    // A datasource another extension adds is not one the form itself can join to.
    [InlineData("Driver", "FmDriver", "FromSibling", null, D365FoErrorCodes.DataSourceNotFound)]
    [InlineData("Driver", "FmDriver", "Vehicle", "SideJoin", D365FoErrorCodes.BadInput)]
    [InlineData("Driver", "FmDriver", null, "InnerJoin", D365FoErrorCodes.BadInput)]
    [InlineData("Bad Name", "FmDriver", null, null, D365FoErrorCodes.BadInput)]
    public void An_invalid_datasource_is_refused_without_a_write(
        string name, string table, string? joinSource, string? linkType, string code)
    {
        var (result, written) = Run(DataSource(name, table) with
        {
            Model = "FleetCustom",
            JoinSource = joinSource,
            LinkType = linkType,
        });

        Assert.False(result.Ok);
        Assert.Equal(code, result.Error!.Code);
        Assert.Null(written);
    }

    [Fact]
    public void A_batch_can_add_a_datasource_and_bind_a_display_method_to_it_in_one_write()
    {
        var steps = BatchStepParser.Parse(
            """
            [{"operation":"add-data-source","member":"Driver","table":"FmDriver","joinSource":"Vehicle","linkType":"OuterJoin"},
             {"operation":"add-control","member":"Grid_Driver","type":"String","parent":"Grid","dataSource":"Driver","dataMethod":"fullName"}]
            """,
            "form", "FmVehicleList", "FleetCustom");

        var (result, written) = Run(steps[0] with { Batch = steps });

        Assert.True(result.Ok, result.Error?.Message);
        var control = written!.Descendants().Single(e =>
            e.Name.LocalName == "AxFormControl" && Child(e, "Name").Value == "Grid_Driver");
        Assert.Equal("fullName", Child(control, "DataMethod").Value);
        Assert.Equal("Driver", Child(control, "DataSource").Value);
        Assert.DoesNotContain(control.Elements(), e => e.Name.LocalName == "DataField");
        // The reader drops members that arrive out of contract order.
        var order = control.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(order.IndexOf("FormControlExtension") < order.IndexOf("DataMethod"));
        Assert.True(order.IndexOf("DataMethod") < order.IndexOf("DataSource"));
    }

    // ---- display methods ----------------------------------------------------

    [Theory]
    [InlineData("Vehicle", "dsTitle")]                  // declared on the form datasource
    [InlineData("Added", "fullName")]                   // declared on the datasource's table
    [InlineData("Added", "FmDriver_Extension.age")]     // added by an extension class
    [InlineData("Added", "FmDriver_Extension::age")]
    [InlineData("FromSibling", "fullName")]             // datasource of another extension
    public void A_control_binds_to_a_display_method_in_a_form_extension(string dataSource, string method)
    {
        var (result, written) = Run(Control(dataSource, method) with
        {
            ExtensionSuffix = "Fleet",
            ExtensionModel = "FleetCustom",
        });

        Assert.True(result.Ok, result.Error?.Message);
        var control = written!.Descendants().Single(e =>
            e.Name.LocalName == "FormControl" && e.Elements().Any(c => c.Value == "FormControl" || c.Value == "Grid_New")
            && e.Elements().Any(c => c.Name.LocalName == "DataMethod"));
        Assert.Equal(method, Child(control, "DataMethod").Value);
        Assert.Equal(dataSource, Child(control, "DataSource").Value);
        Assert.Equal(new[] { "Added" }, Names(written, "DataSources"));
    }

    [Theory]
    [InlineData("Vehicle", "init", "String", D365FoErrorCodes.BadInput)]              // not a display method
    [InlineData("Added", "validateWrite", "String", D365FoErrorCodes.BadInput)]       // not a display method
    [InlineData("Added", "noSuchMethod", "String", D365FoErrorCodes.MethodNotFound)]
    [InlineData("Added", "NoSuchClass.age", "String", D365FoErrorCodes.MethodNotFound)]
    [InlineData("Added", "fullName()", "String", D365FoErrorCodes.BadInput)]
    [InlineData("Added", "fullName", "Group", D365FoErrorCodes.BadInput)]             // no DataMethod property
    [InlineData("NoSuchSource", "fullName", "String", D365FoErrorCodes.DataSourceNotFound)]
    [InlineData("NoSuchSource", null, "Grid", D365FoErrorCodes.DataSourceNotFound)]
    [InlineData("Vehicle", null, "ButtonSeparator", D365FoErrorCodes.BadInput)]       // takes no datasource
    public void An_invalid_binding_is_refused_without_a_write(
        string dataSource, string? method, string type, string code)
    {
        var (result, written) = Run(Control(dataSource, method, type) with
        {
            ExtensionSuffix = "Fleet",
            ExtensionModel = "FleetCustom",
        });

        Assert.False(result.Ok);
        Assert.Equal(code, result.Error!.Code);
        Assert.Null(written);
    }

    [Fact]
    public void A_grid_keeps_the_datasource_it_is_given()
    {
        var (result, written) = Run(Control("vehicle", type: "Grid") with { Model = "FleetCustom", Parent = null });

        Assert.True(result.Ok, result.Error?.Message);
        var grid = written!.Descendants().Single(e =>
            e.Name.LocalName == "AxFormControl" && Child(e, "Name").Value == "Grid_New");
        Assert.Equal("Vehicle", Child(grid, "DataSource").Value);
    }

    /// <summary>
    /// A reference group binds through <c>ReferenceField</c>; it has no <c>DataField</c>, so one
    /// written there is dropped and the control is saved bound to nothing.
    /// </summary>
    [Theory]
    [InlineData("ReferenceGroup")]
    [InlineData("referencegroup")]
    public void A_reference_group_binds_its_reference_field(string type)
    {
        var (result, written) = Run(Control("Vehicle", type: type)
            with { DataField = "Driver", Model = "FleetCustom", Parent = null });

        Assert.True(result.Ok, result.Error?.Message);
        var control = written!.Descendants().Single(e =>
            e.Name.LocalName == "AxFormControl" && Child(e, "Name").Value == "Grid_New");
        Assert.Equal("Driver", Child(control, "ReferenceField").Value);
        Assert.DoesNotContain(control.Elements(), e => e.Name.LocalName == "DataField");
        var order = control.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(order.IndexOf("FormControlExtension") < order.IndexOf("Controls"));
        Assert.True(order.IndexOf("Controls") < order.IndexOf("DataSource"));
        Assert.True(order.IndexOf("DataSource") < order.IndexOf("ReferenceField"));
    }

    [Fact]
    public void A_field_and_a_method_cannot_both_be_bound()
    {
        var (result, written) = Run(Control("Vehicle", "dsTitle") with { DataField = "VIN", Model = "FleetCustom" });

        Assert.False(result.Ok);
        Assert.Equal(D365FoErrorCodes.BadInput, result.Error!.Code);
        Assert.Null(written);
    }
}
