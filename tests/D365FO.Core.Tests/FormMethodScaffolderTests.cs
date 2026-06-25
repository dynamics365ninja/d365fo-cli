using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Covers the form-method injection path (issue #99): correct stub signatures
/// from the catalog, placement in the form-level SourceCode tree with the right
/// namespaces, idempotency / overwrite, and owner validation.
/// </summary>
public class FormMethodScaffolderTests
{
    private const string Ns = "Microsoft.Dynamics.AX.Metadata.V6";

    // A minimal generated-style form (no SourceCode yet): Name, metadata
    // DataSources, Design — exactly what `generate form` emits.
    private static XDocument BareForm() => XDocument.Parse($"""
        <AxForm xmlns="{Ns}" xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
          <Name>FmTestForm</Name>
          <DataSources>
            <AxFormDataSource xmlns="">
              <Name>FmVehicle</Name>
              <Table>FmVehicle</Table>
            </AxFormDataSource>
          </DataSources>
          <Design>
            <Controls>
              <AxFormControl xmlns="" i:type="AxFormStringControl">
                <Name>Vehicle_VIN</Name>
              </AxFormControl>
            </Controls>
          </Design>
        </AxForm>
        """);

    private static XElement? Local(XContainer parent, string name)
        => parent.Descendants().FirstOrDefault(e => e.Name.LocalName == name);

    [Fact]
    public void DataSource_method_lands_in_SourceCode_DataSources_with_empty_namespace()
    {
        var doc = BareForm();
        var sig = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active")!;
        var res = FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", sig, null, overwrite: false);

        Assert.True(res.Changed);
        Assert.False(res.AlreadyExisted);

        var sourceCode = Local(doc.Root!, "SourceCode")!;
        // SourceCode stays in the AxForm namespace.
        Assert.Equal(Ns, sourceCode.Name.NamespaceName);

        // The DataSources subtree resets to the empty namespace.
        var dsContainer = sourceCode.Elements().First(e => e.Name.LocalName == "DataSources");
        Assert.Equal("", dsContainer.Name.NamespaceName);

        var method = Local(dsContainer, "Method")!;
        Assert.Equal("active", Local(method, "Name")!.Value);

        // SourceCode must come before the metadata DataSources / Design.
        var rootChildren = doc.Root!.Elements().Select(e => e.Name.LocalName).ToList();
        Assert.True(rootChildren.IndexOf("SourceCode") < rootChildren.IndexOf("DataSources"));
        Assert.True(rootChildren.IndexOf("SourceCode") < rootChildren.IndexOf("Design"));
    }

    [Fact]
    public void Stub_signature_matches_catalog_return_type()
    {
        var doc = BareForm();
        // active() returns int → must capture and return ret.
        var active = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active")!;
        FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", active, null, false);
        var src = Local(doc.Root!, "Source")!.Value;
        Assert.Contains("public int active()", src);
        Assert.Contains("int ret;", src);
        Assert.Contains("ret = super();", src);
        Assert.Contains("return ret;", src);

        // init() returns void → no ret plumbing.
        var doc2 = BareForm();
        var init = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "init")!;
        FormMethodScaffolder.InjectDataSourceMethod(doc2, "FmVehicle", init, null, false);
        var src2 = Local(doc2.Root!, "Source")!.Value;
        Assert.Contains("public void init()", src2);
        Assert.Contains("super();", src2);
        Assert.DoesNotContain("return ret;", src2);
    }

    [Fact]
    public void Create_method_forwards_its_parameter_to_super()
    {
        var doc = BareForm();
        var create = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "create")!;
        FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", create, null, false);
        var src = Local(doc.Root!, "Source")!.Value;
        Assert.Contains("public void create(boolean _append = false)", src);
        Assert.Contains("super(_append);", src);
    }

    [Fact]
    public void Control_method_lands_in_DataControls()
    {
        var doc = BareForm();
        var modified = FormMethodCatalog.TryGet(FormMethodCatalog.Target.Control, "modified")!;
        var res = FormMethodScaffolder.InjectControlMethod(doc, "Vehicle_VIN", modified, null, false);
        Assert.True(res.Changed);

        var sourceCode = Local(doc.Root!, "SourceCode")!;
        var container = sourceCode.Elements().First(e => e.Name.LocalName == "DataControls");
        Assert.Equal("", container.Name.NamespaceName);
        var dc = container.Elements().First(e => e.Name.LocalName == "DataControl");
        Assert.Equal("Vehicle_VIN", Local(dc, "Name")!.Value);
        Assert.Contains("public boolean modified()", Local(dc, "Source")!.Value);
    }

    [Fact]
    public void Duplicate_method_is_rejected_unless_overwrite()
    {
        var doc = BareForm();
        var sig = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active")!;
        FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", sig, null, false);

        // Second insert without overwrite: no change, flagged as existing.
        var again = FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", sig, null, overwrite: false);
        Assert.False(again.Changed);
        Assert.True(again.AlreadyExisted);
        // Still exactly one <Method>.
        Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Method");
    }

    [Fact]
    public void Overwrite_replaces_body_without_duplicating()
    {
        var doc = BareForm();
        var sig = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active")!;
        FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", sig, customBody: "return 0;", overwrite: false);

        var res = FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", sig, customBody: "return 42;", overwrite: true);
        Assert.True(res.Changed);
        Assert.True(res.AlreadyExisted);
        Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Method");
        Assert.Contains("return 42;", Local(doc.Root!, "Source")!.Value);
        Assert.DoesNotContain("return 0;", Local(doc.Root!, "Source")!.Value);
    }

    [Fact]
    public void Second_method_reuses_existing_owner_and_methods_node()
    {
        var doc = BareForm();
        var active = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active")!;
        var write = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "write")!;
        FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", active, null, false);
        FormMethodScaffolder.InjectDataSourceMethod(doc, "FmVehicle", write, null, false);

        // One DataSource node, one Methods node, two Method children.
        Assert.Single(doc.Descendants(), e => e.Name.LocalName == "DataSource");
        Assert.Single(doc.Descendants(), e => e.Name.LocalName == "Methods");
        Assert.Equal(2, doc.Descendants().Count(e => e.Name.LocalName == "Method"));
    }

    [Fact]
    public void ListDataSourceNames_and_ListControlNames_read_the_form()
    {
        var doc = BareForm();
        Assert.Contains("FmVehicle", FormMethodScaffolder.ListDataSourceNames(doc));
        Assert.Contains("Vehicle_VIN", FormMethodScaffolder.ListControlNames(doc));
    }

    [Fact]
    public void Injecting_into_non_form_throws_typed_exception()
    {
        var doc = XDocument.Parse("<AxTable><Name>T</Name></AxTable>");
        var sig = FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active")!;
        var ex = Assert.Throws<FormMethodScaffolder.FormMethodException>(
            () => FormMethodScaffolder.InjectDataSourceMethod(doc, "X", sig, null, false));
        Assert.Equal("INVALID_FORM", ex.Code);
    }

    [Fact]
    public void Catalog_exposes_distinct_method_sets_per_target()
    {
        Assert.NotEmpty(FormMethodCatalog.List(FormMethodCatalog.Target.DataSource));
        Assert.NotEmpty(FormMethodCatalog.List(FormMethodCatalog.Target.Control));
        // 'active' is a datasource method, not a control method.
        Assert.NotNull(FormMethodCatalog.TryGet(FormMethodCatalog.Target.DataSource, "active"));
        Assert.Null(FormMethodCatalog.TryGet(FormMethodCatalog.Target.Control, "active"));
        // case-insensitive
        Assert.NotNull(FormMethodCatalog.TryGet(FormMethodCatalog.Target.Control, "MODIFIED"));
    }
}
