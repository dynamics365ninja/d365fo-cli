---
id: business-events-authoring
description: Author or extend custom Business Events in D365 Finance & Operations. Invoke when the user asks to "create a business event", "add a business event", "build a custom business event", or wire D365FO outbound notifications to Power Automate / Service Bus / Event Grid.
applyTo:
  - "**/AxClass/**"
  - "**/*BusinessEvent*.xml"
  - "**/*BusinessEvent.xml"
appliesWhen: User intent mentions business events, BusinessEventsBase, BusinessEventsContract, outbound notifications, Power Automate triggers, Service Bus events, or Event Grid from D365FO.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Business Events Authoring in D365FO

> Business events are the standard D365FO mechanism for outbound event-driven
> notifications. They decouple D365FO from subscribers: Power Automate, Azure
> Service Bus, Azure Event Grid, Logic Apps, or any HTTP endpoint can receive
> them without polling or custom integration code.

**Reference:** https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/business-events/home-page

---

## Pattern overview

A custom business event consists of exactly two classes:

```
1. Event class — extends BusinessEventsBase
   - Decorated with [BusinessEvents(...)] — registers it in the catalog
   - Implements buildContract() to populate the payload
   - Has a static newFrom<Context>(...) factory

2. Contract class — extends BusinessEventsContract
   - Decorated with [DataContractAttribute]
   - One parmXxx() accessor per payload field, decorated with [DataMemberAttribute]
```

Both classes are X++ `AxClass` XML files. Use `d365fo generate business-event` to scaffold them correctly.

---

## Pre-flight

```sh
# 1. Check for existing events to avoid duplication
d365fo search business-event <namePart> --output json

# 2. Inspect a similar event for reference pattern
d365fo get business-event <ExistingName> --output json

# 3. Find the primary table if grounding on a table record
d365fo get table <PrimaryTable> --output json
```

---

## Scaffolding

```sh
d365fo generate business-event CustPaymentBusinessEvent \
  --contract-name CustPaymentBusinessEventContract \
  --payload "custAccount:CustAccount" \
  --payload "paymentAmount:AmountCur" \
  --payload "currencyCode:CurrencyCode" \
  --category "CustomerPayments" \
  --primary-table CustTrans \
  --out          c:/AOT/MyModel/AxClass/CustPaymentBusinessEvent.xml \
  --out-contract c:/AOT/MyModel/AxClass/CustPaymentBusinessEventContract.xml
```

---

## Event class skeleton

```xpp
[BusinessEvents(
    classStr(CustPaymentBusinessEventContract),
    'MyModel:CustPaymentBusinessEventName',
    'MyModel:CustPaymentBusinessEventDescription',
    ModuleAxapta::Customer)]
public final class CustPaymentBusinessEvent extends BusinessEventsBase
{
    private CustTrans custTrans;

    // Factory method — called from the business process that fires the event
    public static CustPaymentBusinessEvent newFromCustTrans(CustTrans _custTrans)
    {
        var event = new CustPaymentBusinessEvent();
        event.parmCustTrans(_custTrans);
        return event;
    }

    private void parmCustTrans(CustTrans _custTrans)
    {
        custTrans = _custTrans;
    }

    // Required: populate the contract from the current record context
    [Wrappable(false), Replaceable(false)]
    public BusinessEventsContract buildContract()
    {
        var contract = new CustPaymentBusinessEventContract();
        contract.parmCustAccount(custTrans.AccountNum);
        contract.parmPaymentAmount(custTrans.AmountCur);
        contract.parmCurrencyCode(custTrans.CurrencyCode);
        return contract;
    }
}
```

---

## Contract class skeleton

```xpp
[DataContractAttribute]
public final class CustPaymentBusinessEventContract extends BusinessEventsContract
{
    private CustAccount custAccount;
    private AmountCur   paymentAmount;
    private CurrencyCode currencyCode;

    [DataMemberAttribute('CustAccount')]
    public CustAccount parmCustAccount(CustAccount _custAccount = custAccount)
    {
        custAccount = _custAccount;
        return custAccount;
    }

    [DataMemberAttribute('PaymentAmount')]
    public AmountCur parmPaymentAmount(AmountCur _paymentAmount = paymentAmount)
    {
        paymentAmount = _paymentAmount;
        return paymentAmount;
    }

    [DataMemberAttribute('CurrencyCode')]
    public CurrencyCode parmCurrencyCode(CurrencyCode _currencyCode = currencyCode)
    {
        currencyCode = _currencyCode;
        return currencyCode;
    }
}
```

---

## Firing the event

Call the static factory from the business process at the right lifecycle point — typically in a table `insert` / `update` override, a posting engine, or a workflow action. `BusinessEventsBase` exposes the send operation as a `public final` **instance** method (`send()`), not a static publisher — construct the event, then call `.send()` on it:

```xpp
// In CustTrans.insert() CoC or a posting service method:
[ExtensionOf(tableStr(CustTrans))]
final class CustTrans_MyExt_Extension
{
    public void insert()
    {
        next insert();

        // Fire after successful insert
        CustPaymentBusinessEvent::newFromCustTrans(this).send();
    }
}
```

**Grounding rule:** always run `d365fo find coc CustTrans::insert --output json` first to check for existing CoC wrappers before adding a new one.

---

## Activation lifecycle

After scaffolding and compiling:

1. **System administration > Setup > Business events > Business events catalog** — a new/changed event does **not** appear automatically after compiling; from the catalog form's **Manage** menu run **Rebuild business event catalog** first, then refresh the grid.
2. **Activate** — select the event, click Activate, choose the legal entity scope.
3. **Configure endpoint** — click Endpoints, create or reuse a Service Bus / Event Grid / HTTP / Power Automate connection.
4. **Test** — trigger the business process; the event payload arrives at the endpoint within seconds.

---

## Hard rules

- **`[BusinessEvents(...)]` must be on the event class declaration** — not on methods. The `BusinessEventsAttribute` constructor is `new(ClassName _businessEventsContractClassStr, LabelString _nameLabel, LabelString _descriptionLabel, ModuleAxapta _module)` — four arguments: `classStr(<ContractClass>)`, a name-label token, a description-label token, and a `ModuleAxapta::<Module>` enum value (not a free-text category, and no `classStr(<EventClass>)` — the attribute already decorates the event class itself).
- **`buildContract()` is called by the framework** — return the populated contract instance; never return `null`.
- **Contract `parmXxx()` accessors must be decorated with `[DataMemberAttribute]`** — the serialization layer uses these to build the JSON payload.
- **Use EDTs for payload fields** (e.g. `CustAccount`, `AmountCur`) — not primitive types. Run `d365fo get edt <Name>` to confirm the EDT exists.
- **Never call `.send()` inside a `ttsbegin`/`ttscommit` block** unless you intend to publish on rollback too. Call it after the outermost `ttscommit` or in the `postInsert`/`postUpdate` framework hook. `send()` is a `public final` instance method on `BusinessEventsBase` — there is no static `publish()` method.
- **The catalog category comes from the `ModuleAxapta` enum value passed to `[BusinessEvents(...)]`**, not free text — pick the enum member matching the module the event belongs to (e.g. `ModuleAxapta::Customer`, `ModuleAxapta::Inventory`; run `d365fo get enum ModuleAxapta` for the full list).
