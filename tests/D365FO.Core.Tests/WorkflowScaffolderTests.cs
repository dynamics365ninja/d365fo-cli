using System.Xml.Linq;
using D365FO.Core.Scaffolding;
using Xunit;

namespace D365FO.Core.Tests;

/// <summary>
/// Locks the workflow scaffolder to the shapes shipped in the standard AOT
/// (ground-truthed against ApplicationSuite\Foundation: 88 AxWorkflowTemplate,
/// 82 AxWorkflowApproval, 34 AxWorkflowTask files). The regression guarded here
/// is finding G1 of the knowledge audit: the scaffolder used to emit an
/// &lt;AxWorkflow&gt; root with invented property names, into an AOT folder that
/// does not exist on any AOS — so the object was invisible to the index and
/// unreadable by the metadata provider.
/// </summary>
public class WorkflowScaffolderTests
{
    private static string[] ChildNames(XElement root) =>
        root.Elements().Select(e => e.Name.LocalName).ToArray();

    private static XElement? Child(XElement root, string name) =>
        root.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    [Fact]
    public void Template_root_is_AxWorkflowTemplate()
    {
        var doc = WorkflowScaffolder.WorkflowTemplate("ConVehicleReview", "ConVehicleReviewDocument");

        Assert.Equal("AxWorkflowTemplate", doc.Root!.Name.LocalName);
        Assert.Equal("AxWorkflowTemplate", WorkflowScaffolder.TemplateRoot);
    }

    [Fact]
    public void Template_uses_the_real_metamodel_property_names()
    {
        var doc = WorkflowScaffolder.WorkflowTemplate(
            "ConVehicleReview", "ConVehicleReviewDocument",
            category: "ConVehicleCategory",
            documentMenuItem: "ConVehicleReviewMenuItem",
            submitMenuItem: "ConVehicleReviewSubmit");

        var root = doc.Root!;
        Assert.Equal("ConVehicleReviewDocument", Child(root, "Document")!.Value);
        Assert.Equal("ConVehicleCategory", Child(root, "Category")!.Value);
        Assert.Equal("ConVehicleReviewMenuItem", Child(root, "DocumentMenuItem")!.Value);
        Assert.Equal("ConVehicleReviewSubmit", Child(root, "SubmitToWorkflowMenuItem")!.Value);

        // Properties AxWorkflowTemplate does not have — the pre-fix scaffolder invented them.
        Assert.Null(Child(root, "DocumentTableName"));
        Assert.Null(Child(root, "DocumentMenuItemType"));
        Assert.Null(Child(root, "SubmitToWorkflowMenuItemType"));
        Assert.Null(Child(root, "WorkflowDocumentClass"));
        Assert.Null(Child(root, "WorkflowElements"));
    }

    [Fact]
    public void Template_emits_serializer_order_name_then_alphabetical_then_collections()
    {
        var doc = WorkflowScaffolder.WorkflowTemplate(
            "ConVehicleReview", "ConVehicleReviewDocument",
            category: "ConVehicleCategory",
            documentMenuItem: "ConVehicleReviewMenuItem",
            submitMenuItem: "ConVehicleReviewSubmit");

        Assert.Equal(
            new[]
            {
                "Name", "Category", "Document", "DocumentMenuItem", "SubmitToWorkflowMenuItem",
                "LineItemWorkflows", "SupportedElements",
            },
            ChildNames(doc.Root!));
    }

    [Fact]
    public void Template_omits_category_when_not_supplied()
    {
        var doc = WorkflowScaffolder.WorkflowTemplate("ConVehicleReview", "ConVehicleReviewDocument");

        Assert.Null(Child(doc.Root!, "Category"));
    }

    [Fact]
    public void Supported_elements_reference_approval_and_task_by_kind_prefixed_name()
    {
        var doc = WorkflowScaffolder.WorkflowTemplate(
            "ConVehicleReview", "ConVehicleReviewDocument",
            approvalName: "ConVehicleApproval",
            taskName: "ConVehicleTask");

        var refs = Child(doc.Root!, "SupportedElements")!.Elements().ToArray();
        Assert.All(refs, r => Assert.Equal("AxWorkflowElementReference", r.Name.LocalName));

        Assert.Equal("ApprovalConVehicleApproval", Child(refs[0], "Name")!.Value);
        Assert.Equal("ConVehicleApproval", Child(refs[0], "ElementName")!.Value);
        Assert.Equal("TaskConVehicleTask", Child(refs[1], "Name")!.Value);
        Assert.Equal("ConVehicleTask", Child(refs[1], "ElementName")!.Value);
    }

    [Fact]
    public void Approval_carries_the_four_fixed_outcomes_with_their_canonical_types()
    {
        var doc = WorkflowScaffolder.WorkflowApproval("ConVehicleApproval", "ConVehicleReviewDocument");
        var root = doc.Root!;

        Assert.Equal("AxWorkflowApproval", root.Name.LocalName);
        Assert.Equal(
            new[] { "Name", "Document", "Approve", "Deny", "Reject", "RequestChange" },
            ChildNames(root));

        // Approve takes the default outcome type, so it carries no <Type>.
        Assert.Null(Child(Child(root, "Approve")!, "Type"));
        Assert.Equal("Deny", Child(Child(root, "Deny")!, "Type")!.Value);
        Assert.Equal("Return", Child(Child(root, "Reject")!, "Type")!.Value);
        Assert.Equal("RequestChange", Child(Child(root, "RequestChange")!, "Type")!.Value);
    }

    [Fact]
    public void Outcomes_leave_ActionMenuItem_unset_because_no_menu_item_is_generated()
    {
        var approval = WorkflowScaffolder.WorkflowApproval("ConVehicleApproval", "ConVehicleReviewDocument");
        var task = WorkflowScaffolder.WorkflowTask("ConVehicleTask", "ConVehicleReviewDocument");

        Assert.DoesNotContain(
            approval.Root!.Descendants(),
            e => e.Name.LocalName == "ActionMenuItem");
        Assert.DoesNotContain(
            task.Root!.Descendants(),
            e => e.Name.LocalName == "ActionMenuItem");
    }

    [Fact]
    public void Task_wraps_outcomes_in_a_WorkflowOutcomes_collection()
    {
        var doc = WorkflowScaffolder.WorkflowTask("ConVehicleTask", "ConVehicleReviewDocument", "ConVehicleReviewMenuItem");
        var root = doc.Root!;

        Assert.Equal("AxWorkflowTask", root.Name.LocalName);
        Assert.Equal(
            new[] { "Name", "Document", "DocumentMenuItem", "WorkflowOutcomes" },
            ChildNames(root));

        var outcomes = Child(root, "WorkflowOutcomes")!.Elements().ToArray();
        Assert.All(outcomes, o => Assert.Equal("AxWorkflowOutcome", o.Name.LocalName));
        Assert.Equal(
            new[] { "Complete", "Reject", "RequestChange" },
            outcomes.Select(o => Child(o, "Name")!.Value).ToArray());
    }

    [Fact]
    public void Document_class_extends_WorkflowDocument_and_returns_its_query()
    {
        var doc = WorkflowScaffolder.WorkflowDocument("ConVehicleReviewDocument");
        var root = doc.Root!;

        Assert.Equal("AxClass", root.Name.LocalName);
        // AxClass has no Extends member; the base class is in the declaration.
        Assert.Null(Child(root, "Extends"));
        Assert.Contains("extends WorkflowDocument", root.ToString());
        Assert.Contains("queryStr(ConVehicleReviewQuery)", root.ToString());
    }

    [Fact]
    public void Can_submit_extension_wraps_the_driving_table_with_CoC()
    {
        var doc = WorkflowScaffolder.CanSubmitExtension("FmVehicle");
        var xml = doc.Root!.ToString();

        Assert.Contains("[ExtensionOf(tableStr(FmVehicle))]", xml);
        Assert.Contains("next canSubmitToWorkflow(_workflowType)", xml);
    }

    [Fact]
    public void Scaffolded_workflow_objects_pass_the_pre_write_guards()
    {
        var dir = Path.Combine(Path.GetTempPath(), "d365fo-wf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ScaffoldFileWriter.Write(
                WorkflowScaffolder.WorkflowTemplate("ConVehicleReview", "ConVehicleReviewDocument", approvalName: "ConVehicleApproval"),
                Path.Combine(dir, "ConVehicleReview.xml"));
            ScaffoldFileWriter.Write(
                WorkflowScaffolder.WorkflowApproval("ConVehicleApproval", "ConVehicleReviewDocument"),
                Path.Combine(dir, "ConVehicleApproval.xml"));
            ScaffoldFileWriter.Write(
                WorkflowScaffolder.WorkflowTask("ConVehicleTask", "ConVehicleReviewDocument"),
                Path.Combine(dir, "ConVehicleTask.xml"));

            // Listed, not counted (issue #158): when this went red the message said
            // "Expected 3, Actual 2" and nothing about which write was missing.
            Assert.Equal(
                new[] { "ConVehicleApproval.xml", "ConVehicleReview.xml", "ConVehicleTask.xml" },
                Directory.GetFiles(dir, "*.xml").Select(Path.GetFileName).Order().ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
