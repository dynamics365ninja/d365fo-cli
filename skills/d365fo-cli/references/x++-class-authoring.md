> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Authoring X++ classes with the d365fo index

Before you write or modify any X++ class, **ground yourself in the index**. The
`d365fo` CLI replaces guessing with one-shot lookups that never pollute the
conversation with long metadata dumps.

## Workflow

1. **Resolve the base class**
   ```sh
   d365fo search class <namePart> --output json
   d365fo get class <FullName> --output json
   ```
   Read `methods[*].signature` to anchor overrides to the real signatures.

2. **Check for existing Chain-of-Command extensions** before writing a new one:
   ```sh
   d365fo find coc <TargetClass>::<method> --output json
   ```
   If the result has `count > 0`, prefer extending existing logic or coordinate
   with the owning team rather than stacking a duplicate wrapper.

3. **Label lookups** (never hardcode display strings):
   ```sh
   d365fo labels search "<free text>" --lang en-us,cs --output json
   ```
   Use the returned `key` (e.g. `@SYS4724`) in your X++ code.

4. **Validate at the end**:
   ```sh
   d365fo review diff          # (when available) — best-practice delta
   d365fo bp check             # (when available) — xppbp.exe runner
   ```

## Hard rules

- **Never** emit X++ that references a field you have not verified with
  `d365fo get table <Name>`.
- **Never** create a CoC wrapper without first running `d365fo find coc`.
- **Prefer** EDTs over primitive types — resolve with `d365fo get edt <Name>`.
- **Expect** a `ToolResult` envelope on every command. On `ok:false`, surface
  `error.message` to the user and stop the task.

---

## SysOperation — standard for new batch operations

Modern replacement for `RunBaseBatch`. **Always use SysOperation for new batch code.**

1. Structure: **DataContract** (parameters) + **Service** (logic) + **Controller** (execution mode).
2. DataContract: decorate `parmXxx()` methods with `[DataMemberAttribute]`. Never use `pack()`/`unpack()`.
3. Controller sets execution mode: `Synchronous`, `Asynchronous`, or `ScheduledBatch`.
4. For SSRS report data providers: extend `SRSReportDataProviderBase` instead of `SysOperationServiceBase`.
5. Custom dialog: use `SysOperationAutomaticUIBuilder`; link via `[SysOperationContractProcessingAttribute(classStr(MyUIBuilder))]` on the DataContract.

**Scaffold with the CLI** — generates the DataContract, Service, and Controller XML in one command:

```sh
d365fo generate sysoperation <Name> \
  --param "fromDate:TransDate" --param "toDate:TransDate" \
  --execution-mode Asynchronous \
  --out-contract c:/AOT/MyModel/AxClass/<Name>Contract.xml \
  --out-service  c:/AOT/MyModel/AxClass/<Name>Service.xml \
  --out          c:/AOT/MyModel/AxClass/<Name>Controller.xml
```

## RunBase / RunBaseBatch — legacy batch operations

For teams maintaining older codebases that cannot yet migrate to SysOperation. New code should use SysOperation instead.

Key overrides: `pack()`, `unpack()`, `dialog()`, `getFromDialog()`, `canGoBatch()` (must return `true` for batch-capable jobs), `run()`.

**Scaffold with the CLI:**

```sh
d365fo generate runbase <Name> \
  --batch \
  --dialog-param "fromDate:TransDate" --dialog-param "toDate:TransDate" \
  --out c:/AOT/MyModel/AxClass/<Name>.xml
```

`--batch` adds `canGoBatch() { return true; }` and the `pack()`/`unpack()` container member list automatically.

## SysPlugin — extensible dispatch without `if`/`else`

For enum-based strategy dispatching where new implementations must be addable without changing existing code:

1. Define an extensible enum (`IsExtensible=Yes`) with a value per strategy.
2. Create an interface or abstract class for the strategy.
3. Decorate concrete implementations with `[ExportMetadataAttribute(enumStr(MyEnum), MyEnum::Value)]`.
4. Resolve at runtime: `SysPluginFactory::Instance(enumStr(MyEnum), enumValue)`.

New strategies require only a new class + enum value — no changes to callers.

## Number Sequence Integration

Key classes: `NumberSeqModule`, `NumberSeqApplicationModule`, `NumberSeqScope`.

**Adding a new sequence:**
1. Extend `NumberSeqApplicationModule` via CoC; add a reference in `loadModule()`.
2. Create an EDT for the field; set `NumberSequence=Yes` and `NumberSequenceModule` on it.
3. In form `init()`: call `NumberSeqFormHandler::newForm()` for auto-generation in UI.

**Manual consumption:**
```xpp
NumberSeq numSeq = NumberSeq::newGetNum(CompanyInfo::numRefMySequence());
str nextNum = numSeq.num();
// ... use nextNum ...
numSeq.used();   // or numSeq.abort() to roll back
```

## Workflow Development

Key base classes: `WorkflowDocument`, `WorkflowType`, `WorkflowApproval`, `WorkflowTask`.

**Every workflow needs:**
- `WorkflowDocument` subclass — defines which table fields are available as conditions.
- `SubmitToWorkflowMenuItem` action menu item — the submit button on the form.
- `canSubmitToWorkflow()` method on the table — controls when submit is enabled.

Structure: Document → Type → Approvals/Tasks → EventHandlers.
Approval/Task event handlers use `WorkflowWorkItemActionManager` for complete/reject/delegate.

```sh
d365fo search class WorkflowDocument --output json   # find existing patterns
```
