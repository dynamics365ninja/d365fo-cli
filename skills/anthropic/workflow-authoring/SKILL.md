---
name: workflow-authoring
description: Build a D365FO workflow — the workflow template, its document class, approvals and tasks, the submit menu item, and the generated event handlers. Invoke when the user asks to create an approval workflow, add a workflow to a table or form, or wire canSubmitToWorkflow.
applies_when: User intent mentions workflow, approval, workflow template, workflow document, submit to workflow, canSubmitToWorkflow, workflow task, work item, or delegating an approval.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Workflow authoring

## The object stack

```
AxWorkflowTemplate          ← the AOT element ("workflow type" in the UI)
  ├── document class        ← extends WorkflowDocument: which fields are conditions
  ├── AxWorkflowApproval    ← an approval step
  ├── AxWorkflowTask        ← a task step
  └── event handler classes ← started / canceled / completed, per element
```

**The AOT folder is `AxWorkflowTemplate`.** There is no `AxWorkflowType` folder on
any AOS — a name that looks right, matches the UI wording, and resolves to
nothing.

Only two X++ base classes are involved: **`WorkflowDocument`** and
**`WorkflowType`**. Approvals and tasks are AOT elements whose code lives in
generated event handlers — there is **no `WorkflowTask` class**, and
`WorkflowApproval` is a field, not a base class.

```sh
# What does a real one look like?
d365fo search workflow PurchReq --output json
d365fo get workflow PurchReqReview --output json

d365fo generate workflow FmVehicleServiceApproval \
  --table FmVehicleService \
  --category FmVehicleServiceCategory \
  --approval-name FmVehicleServiceApprovalStep \
  --document-class FmVehicleServiceDocument \
  --submit-menu-item FmVehicleServiceSubmit \
  --install-to FleetManagement
```

## Rules

- The **document class extends `WorkflowDocument`** and exposes the table whose
  fields become workflow conditions.
- A **`SubmitToWorkflowMenuItem`** action menu item provides the submit button on
  the form.
- **`canSubmitToWorkflow()`** on the table controls when submit is enabled —
  return `false` while the record is incomplete, so the user never submits a
  record the workflow will reject.
- Approval and task event handlers act through
  **`WorkflowWorkItemActionManager`** (complete, reject, delegate).
- Workflow categories bind the workflow to a module; the category must exist
  before the template resolves.
- Model a new workflow on a real one:
  `d365fo search class WorkflowDocument --output json` lists the real
  implementations worth copying.
- Every user-facing string — template name, step names, instructions — is a label
  token.

## Hard rules

- Generate the template, never hand-write it. The element carries a property set
  the compiler does not check, and a wrong root element is only caught when the
  metadata step deserializes the file. Prove it with
  `d365fo validate metadata <file> --output json`.
- The document class is the workflow's contract: adding a condition field later
  means re-configuring every deployed workflow instance, so decide the field set
  up front.
- Never drive workflow state by writing the status field directly — go through the
  work-item action manager, or the work items and the record disagree.
