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
4. For SSRS report data providers: extend `SrsReportDataProviderBase` instead of `SysOperationServiceBase`.
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

For strategy dispatching where new implementations must be addable without changing existing code, use the real `SysPluginFactory` platform API:

1. Define an interface or abstract base class for the strategy, identified by its namespace + class name (`_baseClassNamespace` / `_baseClassName`).
2. Tag concrete implementations so the managed extensibility layer can discover them (see existing platform plugin implementations for current attribute usage).
3. Build a `SysPluginMetadataCollection` and populate it with `.SetValue(key, val)` name/value pairs identifying the specific derived type you want (`SetValue(str key, Object val)` takes native X++ types; there's also a `.SetManagedValue(System.String, System.Object)` overload for callers already holding managed CLR values — both wrap the same underlying `ExportMetadataCollection.SetValue`).
4. Resolve at runtime with the real signature: `SysPluginFactory::Instance(str _baseClassNamespace, str _baseClassName, SysPluginMetadataCollection _metadataCollection)` — **not** an enum-keyed call; there is no `enumStr(...)`/`ExportMetadataAttribute` overload.

New strategies require only a new class + metadata registration — no changes to callers.

## Number Sequence Integration

Key classes: the abstract base `NumberSeqApplicationModule` and `NumberSeqScope`.
Concrete per-module classes are named `NumberSeqModule<Module>` (e.g.
`NumberSeqModuleCustomer`, `NumberSeqModuleVendor`) — there is no bare
`NumberSeqModule` class; it is only a naming convention.

**Adding a new sequence:**
1. For an **existing** module, CoC-extend its concrete class, e.g.
   `[ExtensionOf(classStr(NumberSeqModuleCustomer))]`, and add a reference in
   `loadModule()`. For a **brand-new** custom module, create a new class that
   `extends NumberSeqApplicationModule` directly (there's no existing subclass
   to CoC against).
2. Create an EDT for the field; set `NumberSequence=Yes` and `NumberSequenceModule` on it.
3. On the form: add a lazy-getter method (conventionally named
   `numberSeqFormHandler()`) that returns
   `NumberSeqFormHandler::newForm(<Module>Parameters::numRef<Edt>().NumberSequenceId, element, <table>_ds, fieldNum(<Table>, <Field>))`,
   then call `element.numberSeqFormHandler().formMethodDataSourceCreate(...)` /
   `formMethodDataSourceWrite()` / `formMethodDataSourceValidateWrite(...)` /
   `formMethodDataSourceDelete()` from the datasource's `create()`, `write()`,
   `validateWrite()`, and `delete()` overrides — this is not a one-time call in `init()`.

**Manual consumption** — the `numRef<Edt>()` accessor is a `static
NumberSequenceReference` method on the module's own parameter table (e.g.
`CustParameters::numRefCustAccount()` calling
`NumberSeqReference::findReference(extendedTypeNum(CustAccount))`), not on `CompanyInfo`:
```xpp
NumberSeq numSeq = NumberSeq::newGetNum(FmParameters::numRefMySequence());
str nextNum = numSeq.num();
// ... use nextNum ...
numSeq.used();   // or numSeq.abort() to roll back
```

## Workflow Development

Key base classes: `WorkflowDocument` and `WorkflowType` are hand-authored X++
subclasses. Approvals and tasks are **not** subclassed directly in X++ — they
are configured declaratively in the AOT Workflow editor and are backed by
framework classes named `WorkflowModelApproval`/`WorkflowStep_Approval` and
`WorkflowModelTask`/`WorkflowStep_Task`/`WorkflowModelAutomatedTask` (there is
no bare `WorkflowApproval` or `WorkflowTask` class).

**Every workflow needs:**
- `WorkflowDocument` subclass — defines which table fields are available as conditions.
- A submit action, conventionally a per-module class named `<Module>SubmitToWorkflow`
  (e.g. `CatProductSubmitToWorkflow`, `TrvSubmitToWorkflow`) wired to a menu item —
  there is no shared `SubmitToWorkflowMenuItem` base class to extend; each module
  implements its own.
- `canSubmitToWorkflow()` method on the table — controls when submit is enabled.

Structure: Document → Type → Approvals/Tasks (configured in the Workflow editor) → EventHandlers.
Approval/Task event handlers use `WorkflowWorkItemActionManager` for complete/reject/delegate.

```sh
d365fo search class WorkflowDocument --output json   # find existing patterns
```
