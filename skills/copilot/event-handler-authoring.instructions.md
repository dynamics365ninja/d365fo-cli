---
description: Subscribe to a D365FO event (data event on a table, form / form-data-source / form-data-field event, custom delegate on a class) by scaffolding an event-handler class. Use when the user asks to "subscribe to an event", "react to inserted/deleted/updated", or "hook a delegate".
applyTo: '**/AxClass/**EventHandler*.xml,**/AxClass/**Handler*.xml'
---
# Subscribing to D365FO events safely

> Event handlers are the right choice when you need to **react** to a
> platform-emitted event without changing call ordering — choose CoC if you
> need to *modify* a method's behaviour, choose a handler if you need to
> *observe*.

## Pre-flight

```sh
# 1) Discover existing handlers on the target — avoid duplicates
d365fo find handlers <TargetObject> --output json

# 2) Confirm the target exists (for tables) and which events it emits
d365fo get table <Table> --output json
```

## Standard data events on tables → `[DataEventHandler]`

```sh
d365fo generate event-handler MyClass_CustTableHandler \
    --source-kind Table \
    --source CustTable \
    --event Inserted \
    --install-to MyModel
```

Generated attribute: `[DataEventHandler(tableStr(CustTable), DataEventType::Inserted)]`.

D365FO `DataEventType` values: `ValidatingFieldValue`, `ValidatedField`,
`ValidatingDelete`, `ValidatedDelete`, `ValidatingWrite`, `ValidatedWrite`,
`Inserting`, `Inserted`, `Updating`, `Updated`, `Deleting`, `Deleted`,
`InitializingRecord`, `InitializedRecord`, `Modifying`, `Modified`.

## Form / FormDataSource / FormControl events → form-specific attributes

```sh
d365fo generate event-handler MyClass_FormHandler \
    --source-kind Form \
    --source CustTable \
    --event Initialized \
    --install-to MyModel

d365fo generate event-handler MyClass_FormDsHandler \
    --source-kind FormDataSource \
    --source CustTable.CustTable \
    --event ExecuteQuery \
    --install-to MyModel
```

Attribute shapes: `[FormEventHandler(formStr(...), FormEventType::...)]`,
`[FormDataSourceEventHandler(formDataSourceStr(form, ds), FormDataSourceEventType::...)]`.

## Custom delegates on classes → `[SubscribesTo + delegateStr]`

`delegateStr` is **only** for *custom* delegates (your own or a Microsoft-
declared delegate on a framework class). It is **NOT** for standard data
events — those are `DataEventHandler`.

```sh
d365fo generate event-handler MyClass_DelegateHandler \
    --source-kind Class \
    --source SalesFormLetter \
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
- Never modify the buffer in a `Validating*` / `*ing` event without intent —
  changes leak into the persisted record.
- Pre-flight `find handlers <Target>` to detect duplicates and ordering risks.
- After scaffolding, run `d365fo build` only on user request.
