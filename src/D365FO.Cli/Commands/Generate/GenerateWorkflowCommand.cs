using D365FO.Core;
using D365FO.Core.Scaffolding;
using Spectre.Console.Cli;

using static D365FO.Core.ObjectTypes.ObjectTypeRegistry;

namespace D365FO.Cli.Commands.Generate;

/// <summary>
/// Scaffolds the D365FO workflow pattern: an <c>AxWorkflowTemplate</c> (the AOT
/// node Visual Studio calls a "workflow type"), the approval/task elements it
/// supports, a <c>WorkflowDocument</c> subclass, and optionally a CoC extension
/// that adds <c>canSubmitToWorkflow()</c> to the driving table.
/// </summary>
public sealed class GenerateWorkflowCommand : Command<GenerateWorkflowCommand.Settings>
{
    public sealed class Settings : GenerateSettings
    {
        [CommandArgument(0, "<NAME>")]
        [System.ComponentModel.Description("Workflow type name (e.g. PurchOrderWorkflow).")]
        public string Name { get; init; } = "";

        [CommandOption("--table <TABLE>")]
        [System.ComponentModel.Description("Table that drives the workflow (e.g. PurchTable).")]
        public string? TableName { get; init; }

        [CommandOption("--approval-name <NAME>")]
        [System.ComponentModel.Description("Name of the AxWorkflowApproval element to generate and reference.")]
        public string? ApprovalName { get; init; }

        [CommandOption("--task-name <NAME>")]
        [System.ComponentModel.Description("Name of the AxWorkflowTask element to generate and reference.")]
        public string? TaskName { get; init; }

        [CommandOption("--category <NAME>")]
        [System.ComponentModel.Description("Required. Existing AxWorkflowCategory the workflow belongs to (e.g. FixedAssets, PurchCategory).")]
        public string? Category { get; init; }

        [CommandOption("--document-menu-item <NAME>")]
        [System.ComponentModel.Description("Display menu item opening the document. Defaults to <NAME>MenuItem.")]
        public string? DocumentMenuItem { get; init; }

        [CommandOption("--submit-menu-item <NAME>")]
        [System.ComponentModel.Description("Action menu item submitting the document to workflow. Defaults to <NAME>Submit.")]
        public string? SubmitMenuItem { get; init; }

        [CommandOption("--document-class <NAME>")]
        [System.ComponentModel.Description("WorkflowDocument class name. Defaults to <NAME>Document.")]
        public string? DocumentClassName { get; init; }

        [CommandOption("--query <NAME>")]
        [System.ComponentModel.Description("Query name used by the WorkflowDocument. Defaults to <DocumentClass>Query. Generated over --table unless --no-query is passed.")]
        public string? QueryName { get; init; }

        [CommandOption("--no-query")]
        [System.ComponentModel.Description("Skip generating the AxQuery. Use when --query names a query that already exists.")]
        public bool NoQuery { get; init; }

        [CommandOption("--out-query <PATH>")]
        [System.ComponentModel.Description("Output path for the AxQuery. Defaults to sibling of --out.")]
        public string? OutQuery { get; init; }

        [CommandOption("--out-document <PATH>")]
        [System.ComponentModel.Description("Output path for the WorkflowDocument class. Defaults to sibling of --out.")]
        public string? OutDocument { get; init; }

        [CommandOption("--out-approval <PATH>")]
        [System.ComponentModel.Description("Output path for the AxWorkflowApproval element. Defaults to sibling of --out when --approval-name is supplied.")]
        public string? OutApproval { get; init; }

        [CommandOption("--out-task <PATH>")]
        [System.ComponentModel.Description("Output path for the AxWorkflowTask element. Defaults to sibling of --out when --task-name is supplied.")]
        public string? OutTask { get; init; }

        [CommandOption("--out-submit <PATH>")]
        [System.ComponentModel.Description("Output path for the canSubmitToWorkflow CoC extension. Defaults to sibling of --out when --table is supplied.")]
        public string? OutSubmit { get; init; }

        [CommandOption("--no-submit-stub")]
        [System.ComponentModel.Description("Skip generating the canSubmitToWorkflow CoC extension.")]
        public bool NoSubmitStub { get; init; }
    }

    public override int Execute(CommandContext ctx, Settings settings)
    {
        var kind = OutputMode.Resolve(settings.Output);

        if (string.IsNullOrWhiteSpace(settings.Name))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "Workflow name required."));
        if (string.IsNullOrWhiteSpace(settings.TableName))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--table <TABLE> required."));

        // Category is a required property of AxWorkflowTemplate, not a nicety: without
        // it the metadata provider rejects the template outright ("Category: Property
        // cannot be empty"). Emitting one anyway, with a warning that it "cannot be
        // configured in the UI", understated it by a whole severity level — the
        // workflow did not build at all. There is no defensible default, so ask.
        if (string.IsNullOrWhiteSpace(settings.Category))
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(
                D365FoErrorCodes.BadInput,
                "--category <NAME> required: AxWorkflowTemplate.Category cannot be empty. " +
                "Pass an existing AxWorkflowCategory (search for one with `d365fo search --type WorkflowCategory`)."));

        var hasInstall = !string.IsNullOrWhiteSpace(settings.InstallTo);
        var hasOut     = !string.IsNullOrWhiteSpace(settings.Out);
        if (!hasInstall && !hasOut)
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.BadInput, "--out or --install-to is required."));

        var docClassName    = string.IsNullOrWhiteSpace(settings.DocumentClassName)
            ? settings.Name + "Document"
            : settings.DocumentClassName!;
        var submitExtName   = settings.TableName + "_Workflow_Extension";
        var queryName       = string.IsNullOrWhiteSpace(settings.QueryName)
            ? docClassName.Replace("Document", "") + "Query"
            : settings.QueryName!;
        var generateSubmit  = !settings.NoSubmitStub;
        var docMenuItem     = string.IsNullOrWhiteSpace(settings.DocumentMenuItem)
            ? settings.Name + "MenuItem"
            : settings.DocumentMenuItem!;
        var submitMenuItem  = string.IsNullOrWhiteSpace(settings.SubmitMenuItem)
            ? settings.Name + "Submit"
            : settings.SubmitMenuItem!;
        var approvalName    = string.IsNullOrWhiteSpace(settings.ApprovalName) ? null : settings.ApprovalName!.Trim();
        var taskName        = string.IsNullOrWhiteSpace(settings.TaskName)     ? null : settings.TaskName!.Trim();

        // The WorkflowDocument's getQueryName() names a query; generating the document
        // without the query left an unresolvable reference in every scaffold ("Query
        // 'XQuery' is not found"). The driving table is already known, so scaffold it.
        var generateQuery = !settings.NoQuery;

        // Resolve output paths.
        string? workflowPath, documentPath, submitPath, approvalPath, taskPath, queryPath;
        if (hasInstall && !hasOut)
        {
            queryPath = !generateQuery ? null
                : string.IsNullOrWhiteSpace(settings.OutQuery)
                    ? GenerateInstaller.ResolveInstallPath(kind, Folders.Query, queryName, settings.InstallTo!, out _)
                    : settings.OutQuery;
            workflowPath  = GenerateInstaller.ResolveInstallPath(kind, WorkflowScaffolder.TemplateRoot, settings.Name, settings.InstallTo!, out var f1);
            if (f1.HasValue) return f1.Value;
            documentPath  = string.IsNullOrWhiteSpace(settings.OutDocument)
                ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, docClassName, settings.InstallTo!, out _)
                : settings.OutDocument;
            submitPath    = !generateSubmit ? null
                : string.IsNullOrWhiteSpace(settings.OutSubmit)
                    ? GenerateInstaller.ResolveInstallPath(kind, Folders.Class, submitExtName, settings.InstallTo!, out _)
                    : settings.OutSubmit;
            approvalPath  = approvalName is null ? null
                : string.IsNullOrWhiteSpace(settings.OutApproval)
                    ? GenerateInstaller.ResolveInstallPath(kind, WorkflowScaffolder.ApprovalRoot, approvalName, settings.InstallTo!, out _)
                    : settings.OutApproval;
            taskPath      = taskName is null ? null
                : string.IsNullOrWhiteSpace(settings.OutTask)
                    ? GenerateInstaller.ResolveInstallPath(kind, WorkflowScaffolder.TaskRoot, taskName, settings.InstallTo!, out _)
                    : settings.OutTask;
        }
        else
        {
            var dir = System.IO.Path.GetDirectoryName(settings.Out!)!;
            workflowPath  = settings.Out!;
            documentPath  = settings.OutDocument ?? System.IO.Path.Combine(dir, docClassName + ".xml");
            submitPath    = !generateSubmit ? null
                : settings.OutSubmit ?? System.IO.Path.Combine(dir, submitExtName + ".xml");
            approvalPath  = approvalName is null ? null
                : settings.OutApproval ?? System.IO.Path.Combine(dir, approvalName + ".xml");
            taskPath      = taskName is null ? null
                : settings.OutTask ?? System.IO.Path.Combine(dir, taskName + ".xml");
            queryPath     = !generateQuery ? null
                : settings.OutQuery ?? System.IO.Path.Combine(dir, queryName + ".xml");
        }

        try
        {
            var workflowResult = ScaffoldFileWriter.Write(
                WorkflowScaffolder.WorkflowTemplate(
                    settings.Name,
                    docClassName,
                    settings.Category,
                    docMenuItem,
                    submitMenuItem,
                    approvalName,
                    taskName),
                workflowPath!, settings.Overwrite);

            var documentResult = ScaffoldFileWriter.Write(
                WorkflowScaffolder.WorkflowDocument(docClassName, queryName),
                documentPath!, settings.Overwrite);

            ScaffoldFileWriter.WriteResult? queryResult = null;
            if (generateQuery && queryPath is not null)
            {
                queryResult = ScaffoldFileWriter.Write(
                    QueryScaffolder.Query(queryName, [new QueryDataSourceSpec(settings.TableName!)]),
                    queryPath, settings.Overwrite);
            }

            ScaffoldFileWriter.WriteResult? approvalResult = null;
            if (approvalName is not null && approvalPath is not null)
            {
                approvalResult = ScaffoldFileWriter.Write(
                    WorkflowScaffolder.WorkflowApproval(approvalName, docClassName, docMenuItem),
                    approvalPath, settings.Overwrite);
            }

            ScaffoldFileWriter.WriteResult? taskResult = null;
            if (taskName is not null && taskPath is not null)
            {
                taskResult = ScaffoldFileWriter.Write(
                    WorkflowScaffolder.WorkflowTask(taskName, docClassName, docMenuItem),
                    taskPath, settings.Overwrite);
            }

            ScaffoldFileWriter.WriteResult? submitResult = null;
            if (generateSubmit && submitPath is not null)
            {
                submitResult = ScaffoldFileWriter.Write(
                    WorkflowScaffolder.CanSubmitExtension(settings.TableName!),
                    submitPath, settings.Overwrite);
            }

            // Objects the template points at but this command does not create. Naming them
            // keeps the scaffold honest: the workflow does not build until they exist.
            var pending = new List<string>
            {
                $"AxMenuItemDisplay {docMenuItem} (opens the document)",
                $"AxMenuItemAction {submitMenuItem} (submits the document)",
            };
            if (!generateQuery)
                pending.Add($"AxQuery {queryName} (returned by getQueryName)");
            if (approvalResult is not null || taskResult is not null)
                pending.Add("AxMenuItemAction per outcome (Approve/Reject/RequestChange/Complete), then set ActionMenuItem on each outcome");

            var warnings = new List<string>
            {
                "Referenced but not generated — create these before building: " + string.Join("; ", pending),
            };

            return RenderHelpers.Render(kind, ToolResult<object>.Success(new
            {
                kind            = "Workflow",
                name            = settings.Name,
                tableName       = settings.TableName,
                documentClassName = docClassName,
                category        = settings.Category,
                documentMenuItem = docMenuItem,
                submitMenuItem  = submitMenuItem,
                approvalName    = approvalName,
                taskName        = taskName,
                workflow        = new { path = workflowResult.Path,  bytes = workflowResult.Bytes,  backup = workflowResult.BackupPath },
                document        = new { path = documentResult.Path,  bytes = documentResult.Bytes,  backup = documentResult.BackupPath },
                query           = queryResult    is null ? null : new { path = queryResult.Path,    bytes = queryResult.Bytes,    backup = queryResult.BackupPath },
                approval        = approvalResult is null ? null : new { path = approvalResult.Path, bytes = approvalResult.Bytes, backup = approvalResult.BackupPath },
                task            = taskResult     is null ? null : new { path = taskResult.Path,     bytes = taskResult.Bytes,     backup = taskResult.BackupPath },
                submitStub      = submitResult   is null ? null : new { path = submitResult.Path,   bytes = submitResult.Bytes,   backup = submitResult.BackupPath },
                model           = settings.InstallTo,
            }, warnings));
        }
        catch (Exception ex)
        {
            return RenderHelpers.Render(kind, ToolResult<object>.Fail(D365FoErrorCodes.WriteFailed, ex.Message));
        }
    }
}
