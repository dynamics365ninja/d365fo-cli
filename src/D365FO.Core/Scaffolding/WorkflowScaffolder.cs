using System.Xml.Linq;

namespace D365FO.Core.Scaffolding;

/// <summary>
/// Scaffolds the standard D365FO workflow pattern: an <c>AxWorkflowTemplate</c>
/// (the AOT node Visual Studio labels "Workflow type"), the approval/task
/// elements it supports, a <c>WorkflowDocument</c> subclass, and a CoC
/// <c>canSubmitToWorkflow()</c> stub on the driving table.
/// </summary>
/// <remarks>
/// Element names and their order are ground-truthed against the 88
/// <c>AxWorkflowTemplate</c>, 82 <c>AxWorkflowApproval</c> and 34
/// <c>AxWorkflowTask</c> files shipped in <c>ApplicationSuite\Foundation</c>:
/// the serializer writes <c>Name</c> first, then the remaining scalar
/// properties alphabetically, then the collections. There is no
/// <c>AxWorkflowType</c> root or AOT folder — that name matches nothing on a
/// real AOS.
/// </remarks>
public static class WorkflowScaffolder
{
    /// <summary>AOT subfolder / root element for a workflow type.</summary>
    public const string TemplateRoot = ObjectTypes.ObjectTypeRegistry.Folders.WorkflowTemplate;

    /// <summary>AOT subfolder / root element for a workflow approval element.</summary>
    public const string ApprovalRoot = ObjectTypes.ObjectTypeRegistry.Folders.WorkflowApproval;

    /// <summary>AOT subfolder / root element for a workflow task element.</summary>
    public const string TaskRoot = ObjectTypes.ObjectTypeRegistry.Folders.WorkflowTask;

    /// <summary>
    /// Scaffolds an <c>AxClass</c> extending <c>WorkflowDocument</c>.
    /// The generated <c>getQueryName()</c> returns the companion query name.
    /// </summary>
    public static XDocument WorkflowDocument(string documentClassName, string? queryName = null)
    {
        var effectiveQuery = queryName ?? documentClassName.Replace("Document", "") + "Query";

        var declaration =
            $"class {documentClassName} extends WorkflowDocument\n" +
            "{\n" +
            "}\n";

        var getQuerySrc =
            "public QueryName getQueryName()\n" +
            "{\n" +
            $"    return queryStr({effectiveQuery});\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", documentClassName),
                new XElement("Extends", "WorkflowDocument"),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "getQueryName"),
                            new XElement("Source", getQuerySrc))))));
    }

    /// <summary>
    /// Scaffolds the <c>AxWorkflowTemplate</c> that ties the workflow document
    /// class to its menu items and lists the approval/task elements it supports.
    /// </summary>
    /// <param name="workflowTypeName">Name of the workflow type.</param>
    /// <param name="documentClassName">The <c>WorkflowDocument</c> subclass driving the workflow.</param>
    /// <param name="category">Existing <c>AxWorkflowCategory</c>; omitted when null (the workflow will not surface in the UI until one is set).</param>
    /// <param name="documentMenuItem">Display menu item opening the document.</param>
    /// <param name="submitMenuItem">Action menu item submitting the document.</param>
    /// <param name="approvalName">Approval element to reference in <c>SupportedElements</c>.</param>
    /// <param name="taskName">Task element to reference in <c>SupportedElements</c>.</param>
    public static XDocument WorkflowTemplate(
        string workflowTypeName,
        string documentClassName,
        string? category = null,
        string? documentMenuItem = null,
        string? submitMenuItem = null,
        string? approvalName = null,
        string? taskName = null)
    {
        var root = new XElement(TemplateRoot, new XElement("Name", workflowTypeName));

        // Scalar properties in serializer order (alphabetical after Name).
        if (!string.IsNullOrWhiteSpace(category))
            root.Add(new XElement("Category", category));
        root.Add(new XElement("Document", documentClassName));
        if (!string.IsNullOrWhiteSpace(documentMenuItem))
            root.Add(new XElement("DocumentMenuItem", documentMenuItem));
        if (!string.IsNullOrWhiteSpace(submitMenuItem))
            root.Add(new XElement("SubmitToWorkflowMenuItem", submitMenuItem));

        // Collections last: LineItemWorkflows then SupportedElements.
        root.Add(new XElement("LineItemWorkflows"));

        var supported = new XElement("SupportedElements");
        if (!string.IsNullOrWhiteSpace(approvalName))
            supported.Add(ElementReference("Approval", approvalName!));
        if (!string.IsNullOrWhiteSpace(taskName))
            supported.Add(ElementReference("Task", taskName!));
        root.Add(supported);

        return new XDocument(root);
    }

    /// <summary>
    /// Scaffolds an <c>AxWorkflowApproval</c> element with the four outcomes every
    /// shipped approval carries: Approve, Deny, Reject (Return) and RequestChange.
    /// </summary>
    /// <remarks>
    /// <c>ActionMenuItem</c> is deliberately left unset on every outcome: pointing
    /// it at a menu item this command does not create would be an unresolvable
    /// reference at build time. Create the action menu items, then set them here.
    /// </remarks>
    public static XDocument WorkflowApproval(
        string approvalName,
        string documentClassName,
        string? documentMenuItem = null)
    {
        var root = new XElement(ApprovalRoot, new XElement("Name", approvalName));

        root.Add(new XElement("Document", documentClassName));
        if (!string.IsNullOrWhiteSpace(documentMenuItem))
            root.Add(new XElement("DocumentMenuItem", documentMenuItem));

        // The four outcome properties are fixed members of AxWorkflowApproval —
        // they are named elements, not a collection.
        root.Add(new XElement("Approve",
            new XElement("Name", "Approve")));
        root.Add(new XElement("Deny",
            new XElement("Name", "Deny"),
            new XElement("Enabled", "No"),
            new XElement("Type", "Deny")));
        root.Add(new XElement("Reject",
            new XElement("Name", "Reject"),
            new XElement("Type", "Return")));
        root.Add(new XElement("RequestChange",
            new XElement("Name", "RequestChange"),
            new XElement("Enabled", "No"),
            new XElement("Type", "RequestChange")));

        return new XDocument(root);
    }

    /// <summary>
    /// Scaffolds an <c>AxWorkflowTask</c> element with the Complete / Reject /
    /// RequestChange outcomes. Same <c>ActionMenuItem</c> caveat as
    /// <see cref="WorkflowApproval"/>.
    /// </summary>
    public static XDocument WorkflowTask(
        string taskName,
        string documentClassName,
        string? documentMenuItem = null)
    {
        var root = new XElement(TaskRoot, new XElement("Name", taskName));

        root.Add(new XElement("Document", documentClassName));
        if (!string.IsNullOrWhiteSpace(documentMenuItem))
            root.Add(new XElement("DocumentMenuItem", documentMenuItem));

        root.Add(new XElement("WorkflowOutcomes",
            new XElement("AxWorkflowOutcome",
                new XElement("Name", "Complete")),
            new XElement("AxWorkflowOutcome",
                new XElement("Name", "Reject"),
                new XElement("Type", "Return")),
            new XElement("AxWorkflowOutcome",
                new XElement("Name", "RequestChange"),
                new XElement("Type", "RequestChange"))));

        return new XDocument(root);
    }

    /// <summary>
    /// Scaffolds a CoC extension on <paramref name="tableName"/> that adds a
    /// <c>canSubmitToWorkflow()</c> override — the entry point that controls
    /// whether the Submit button on the form is enabled.
    /// </summary>
    public static XDocument CanSubmitExtension(string tableName)
    {
        var extensionName = tableName + "_WorkflowExtension";

        var declaration =
            $"[ExtensionOf(tableStr({tableName}))]\n" +
            $"final class {extensionName}\n" +
            "{\n" +
            "}\n";

        var canSubmitSrc =
            "public boolean canSubmitToWorkflow(str _workflowType = '')\n" +
            "{\n" +
            "    boolean canSubmit = next canSubmitToWorkflow(_workflowType);\n" +
            "\n" +
            "    // Add conditions under which this record can be submitted:\n" +
            "    // canSubmit = canSubmit && this.Status == MyStatus::Draft;\n" +
            "\n" +
            "    return canSubmit;\n" +
            "}\n";

        return new XDocument(
            new XElement("AxClass",
                new XElement("Name", extensionName),
                new XElement("SourceCode",
                    new XElement("Declaration", declaration),
                    new XElement("Methods",
                        new XElement("Method",
                            new XElement("Name", "canSubmitToWorkflow"),
                            new XElement("Source", canSubmitSrc))))));
    }

    /// <summary>
    /// A <c>SupportedElements</c> entry. Shipped templates name the reference
    /// after the element kind followed by the element name
    /// (<c>ApprovalBankReconciliationApproval</c> → <c>BankReconciliationApproval</c>).
    /// </summary>
    private static XElement ElementReference(string kind, string elementName) =>
        new("AxWorkflowElementReference",
            new XElement("Name", kind + elementName),
            new XElement("ElementName", elementName));
}
