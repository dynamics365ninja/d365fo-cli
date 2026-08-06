using System.Text.RegularExpressions;
using System.Xml.Linq;
using D365FO.Core.FormPatterns;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Issue #164 / R5 — cloning a reference form under a new name.
/// </summary>
/// <remarks>
/// The fixture mirrors the shape of a real shipped form: the root name, the X++ class
/// declaration, a datasource entry under <c>&lt;SourceCode&gt;</c> (where override methods live),
/// the design datasource, and a control pointing at it by name. Verified against
/// <c>ApplicationSuite\Foundation\AxForm\CustGroup.xml</c> on a live installation — a 16 KB form
/// where the clone differs from the source on exactly the intended lines and is byte-identical
/// everywhere else.
/// </remarks>
public class FormClonerTests
{
    private const string Source = """
        <?xml version="1.0" encoding="utf-8"?>
        <AxForm xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
        	<Name>CustGroup</Name>
        	<SourceCode>
        		<Methods>
        			<Method>
        				<Name>classDeclaration</Name>
        				<Source>[Form] public class CustGroup extends FormRun { void go() { formStr(CustGroup); } }</Source>
        			</Method>
        		</Methods>
        		<DataSources xmlns="">
        			<DataSource>
        				<Name>CustGroup</Name>
        				<Methods />
        			</DataSource>
        		</DataSources>
        	</SourceCode>
        	<DataSources>
        		<AxFormDataSource>
        			<Name>CustGroup</Name>
        			<Table>CustGroup</Table>
        		</AxFormDataSource>
        	</DataSources>
        	<Design>
        		<AxFormControl xmlns="" i:type="AxFormStringControl">
        			<Name>Grid_CustGroupId</Name>
        			<DataField>CustGroupId</DataField>
        			<DataSource>CustGroup</DataSource>
        		</AxFormControl>
        	</Design>
        </AxForm>
        """;

    private static Dictionary<string, string> Rebind(string from, string to) => new() { [from] = to };

    [Fact]
    public void The_clone_takes_the_new_name_everywhere_the_form_names_itself()
    {
        var result = FormCloner.Clone(Source, "ConVehicleGroup");

        Assert.Contains("<Name>ConVehicleGroup</Name>", result.Xml);
        Assert.Contains("public class ConVehicleGroup extends FormRun", result.Xml);
        Assert.Contains("formStr(ConVehicleGroup)", result.Xml);
        Assert.DoesNotContain("public class CustGroup", result.Xml);
    }

    [Fact]
    public void Without_a_rebind_the_datasource_still_points_at_the_original_table()
    {
        // Cloning is not rebinding. A clone of a CustGroup form is still bound to CustGroup
        // unless the caller says otherwise.
        var result = FormCloner.Clone(Source, "ConVehicleGroup");

        Assert.Contains("<Table>CustGroup</Table>", result.Xml);
        Assert.Empty(result.Rebound);
    }

    [Fact]
    public void A_rebind_moves_the_table_the_datasource_and_every_control_that_names_it()
    {
        var result = FormCloner.Clone(Source, "ConVehicleGroup", Rebind("CustGroup", "ConVehicleGroupTable"));

        Assert.Contains("<Table>ConVehicleGroupTable</Table>", result.Xml);
        Assert.Contains("<DataSource>ConVehicleGroupTable</DataSource>", result.Xml);
        // Both datasource entries: the design one, and the SourceCode one holding overrides.
        Assert.Equal(2, Regex.Matches(result.Xml, "<Name>ConVehicleGroupTable</Name>").Count);
        Assert.Single(result.Rebound);
        Assert.Single(result.RenamedDataSources);
    }

    [Fact]
    public void The_form_is_renamed_before_the_rebind_so_the_root_never_takes_the_table_name()
    {
        // Load-bearing ordering. The form, its datasource and its table are all called
        // "CustGroup" here — which is the normal case — so a rebind that ran first would rename
        // the form itself to the new table's name.
        var result = FormCloner.Clone(Source, "ConVehicleGroup", Rebind("CustGroup", "ConVehicleGroupTable"));

        var root = XDocument.Parse(result.Xml).Root!;
        Assert.Equal("ConVehicleGroup", root.Elements().First(e => e.Name.LocalName == "Name").Value);
    }

    [Fact]
    public void Everything_else_is_left_alone()
    {
        // The reason this is string surgery and not a round-trip: an AxForm's Design subtree is
        // written in the empty namespace with i:type on every control, and loading it into an
        // XDocument and writing it back rewrites namespace declarations nobody asked to change.
        var result = FormCloner.Clone(Source, "ConVehicleGroup");

        Assert.Contains("<AxFormControl xmlns=\"\" i:type=\"AxFormStringControl\">", result.Xml);
        Assert.Contains("<DataField>CustGroupId</DataField>", result.Xml);

        // Three self-references changed and nothing else, so the length moves by exactly that.
        var delta = ("ConVehicleGroup".Length - "CustGroup".Length) * 3;
        Assert.Equal(Source.Length + delta, result.Xml.Length);
    }

    [Fact]
    public void A_control_name_that_merely_contains_the_form_name_is_not_touched()
    {
        // Form names are short and appear inside unrelated identifiers. A blind replace would
        // rename Grid_CustGroupId and the DataField with it.
        var result = FormCloner.Clone(Source, "ConVehicleGroup");

        Assert.Contains("<Name>Grid_CustGroupId</Name>", result.Xml);
        Assert.Contains("<DataField>CustGroupId</DataField>", result.Xml);
    }

    [Fact]
    public void A_rebind_of_a_table_no_datasource_uses_is_reported_rather_than_silently_ignored()
    {
        var result = FormCloner.Clone(Source, "ConVehicleGroup", Rebind("VendTable", "ConVendorTable"));

        Assert.Empty(result.Rebound);
        Assert.Contains(result.Warnings, w => w.Contains("No datasource is bound to 'VendTable'"));
    }

    [Fact]
    public void A_rebind_warns_that_the_bound_fields_were_not_checked()
    {
        var result = FormCloner.Clone(Source, "ConVehicleGroup", Rebind("CustGroup", "ConVehicleGroupTable"));

        Assert.Contains(result.Warnings, w => w.Contains("validate references"));
    }

    [Fact]
    public void Every_clone_warns_about_the_references_it_cannot_reach()
    {
        var result = FormCloner.Clone(Source, "ConVehicleGroup");

        Assert.Contains(result.Warnings, w => w.Contains("menu items"));
    }

    [Fact]
    public void Cloning_onto_the_same_name_is_refused()
        => Assert.Throws<FormCloneException>(() => FormCloner.Clone(Source, "CustGroup"));

    [Fact]
    public void A_document_that_is_not_a_form_is_refused()
    {
        Assert.Throws<FormCloneException>(() =>
            FormCloner.Clone("<AxTable><Name>CustTable</Name></AxTable>", "ConVehicle"));
        Assert.Throws<FormCloneException>(() =>
            FormCloner.Clone("<AxForm></AxForm>", "ConVehicle"));
    }

    [Fact]
    public void The_clone_is_still_well_formed_and_still_a_form()
    {
        var result = FormCloner.Clone(Source, "ConVehicleGroup", Rebind("CustGroup", "ConVehicleGroupTable"));

        Assert.Equal("AxForm", XDocument.Parse(result.Xml).Root!.Name.LocalName);
    }
}
