---
id: event-handler-authoring
description: Subscribe to a D365FO event (data event on a table, form / form-data-source / form-data-field event, custom delegate on a class) by scaffolding an event-handler class. Use when the user asks to "subscribe to an event", "react to inserted/deleted/updated", or "hook a delegate".
applyTo:
  - "**/AxClass/**EventHandler*.xml"
  - "**/AxClass/**Handler*.xml"
appliesWhen: User intent mentions event handler, SubscribesTo, DataEventHandler, FormEventHandler, FormDataSourceEventHandler, or reacting to a D365FO platform event.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Subscribing to D365FO events safely

> Event handlers are the right choice when you need to **react** to a
> platform-emitted event without changing call ordering — choose CoC if you
> need to *modify* a method's behaviour, choose a handler if you need to
> *observe*.

## Pre-flight

```sh
# 1) Discover existing handlers on the target — avoid duplicates
d365fo find event-handlers <TargetObject> --output json

# 2) Search likely handler classes in the target model/prefix
d365fo search class <TargetObject> --output json

# 3) Confirm the target exists (for tables) and which events it emits
d365fo get table <Table> --output json
```

If a suitable handler class already exists in the target custom model, add the
new method to that class instead of creating another handler. `D365FO_CUSTOM_MODELS`
can contain multiple models, so first resolve the active target model from the
artifact named by the user, the model that already contains the related handler,
or the model currently being edited. The handler suffix is separate from the
model name: extract `<ExistingSuffix>` from existing related handler classes in
the active model, such as `<Form>_<ExistingSuffix>_Form_EH` or
`<Form>_<ExistingSuffix>_Form_EventHandler`. If no suffix can be derived and
the user did not provide one, stop and ask for the suffix. If both `_EH` and
`_EventHandler` naming styles exist, follow the existing style in that model.
Do not create `<Form>_<Feature>_EH` or `<Form>_<Feature>_EventHandler` unless
the user explicitly requests a separate class.

## Standard data events on tables → `[DataEventHandler]`

```sh
d365fo generate event-handler MyClass_CustTableHandler \
    --source-kind Table \
    --source-object CustTable \
    --event Inserted \
    --install-to MyModel
```

Generated attribute: `[DataEventHandler(tableStr(CustTable), DataEventType::Inserted)]`.

D365FO `DataEventType` values: `ValidatingFieldValue`, `ValidatedField`,
`ValidatingDelete`, `ValidatedDelete`, `ValidatingWrite`, `ValidatedWrite`,
`Inserting`, `Inserted`, `Updating`, `Updated`, `Deleting`, `Deleted`,
`InitializingRecord`, `InitializedRecord`, `ModifyingField`, `ModifiedField`.

## Form / FormDataSource / FormControl events → form-specific attributes

```sh
d365fo generate event-handler MyClass_FormHandler \
    --source-kind Form \
    --source-object CustTable \
    --event Initialized \
    --install-to MyModel

d365fo generate event-handler MyClass_FormDsHandler \
    --source-kind FormDataSource \
    --source-object "CustTable, CustTable" \
    --event QueryExecuting \
    --install-to MyModel
```

`--source-object` for `FormDataSource` is passed through verbatim into
`formDataSourceStr(...)`, so it must already be the two comma-separated
`<Form>, <DataSource>` arguments (e.g. `"CustTable, CustTable"`) — a dotted
`Form.DataSource` value produces invalid X++.

Attribute shapes: `[FormEventHandler(formStr(...), FormEventType::...)]`,
`[FormDataSourceEventHandler(formDataSourceStr(form, ds), FormDataSourceEventType::...)]`.

## Custom delegates on classes → `[SubscribesTo + delegateStr]`

`delegateStr` is **only** for *custom* delegates (your own or a Microsoft-
declared delegate on a framework class). It is **NOT** for standard data
events — those are `DataEventHandler`.

```sh
d365fo generate event-handler MyClass_DelegateHandler \
    --source-kind Class \
    --source-object SalesFormLetter \
    --event onPosted \
    --install-to MyModel
```

Attribute: `[SubscribesTo(classStr(SalesFormLetter), delegateStr(SalesFormLetter, onPosted))]`.

## Hard rules

- Standard data events use `[DataEventHandler]`, NEVER `[SubscribesTo + delegateStr]`.
- `delegateStr` is for *custom* delegates only.
- Handlers do NOT chain via `next` — they are notification-only.
- Handler methods must be `public static` (the runtime invokes them
  reflectively).
- Never create a parallel handler class when an existing target/model handler
  class already owns the same object/event family.
- Never modify the buffer in a `Validating*` / `*ing` event without intent —
  changes leak into the persisted record.
- Pre-flight `find handlers <Target>` to detect duplicates and ordering risks.
- After scaffolding, run `d365fo build` only on user request.
