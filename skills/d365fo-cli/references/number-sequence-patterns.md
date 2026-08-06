> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Number sequences

> This area has an unusually high density of plausible-but-wrong API names. Every
> rule below states the wrong spelling next to the right one, because the wrong
> ones compile in an agent's head and nowhere else.

```sh
d365fo get class NumberSeqApplicationModule --output json
d365fo get class NumberSeqDatatype --output json
# <MODULE_NAME> is the module suffix; the CoC target is derived from it.
d365fo generate number-sequence Fleet --edt FmVehicleId --edt-label "@Fleet:VehicleId" \
  --scope Company --table FmVehicle --install-to FleetManagement
```

## 1. The module class

- The class **extends `NumberSeqApplicationModule`** — exact name.
  ❌ `NumberSequenceApplicationModule` does not exist.
- It is a **subclass**, so `loadModule()` is an override that calls `super()`.
  ❌ `next()` is only for `[ExtensionOf]` CoC classes, never for an `extends`
  subclass.
- `loadModule()` registers each reference through **`NumberSeqDatatype`**:
  `NumberSeqDatatype::construct()`, then the `parm*()` methods, then
  `this.create(datatype)`.
  ❌ Do **not** assign fields on a `NumberSeqReference` buffer — `DataTypeId`,
  `WizardContinuous`, `AllowManual` and friends are `parm*()` methods on
  `NumberSeqDatatype`, not table fields.
  ❌ There is no `this.addModuleEntry()`.
- Override `numberSeqModule()` to return your `NumberSeqModule` enum value.
- **A new module class is not auto-loaded.** Extend the `NumberSeqModule` enum and
  register the module from an event handler on `NumberSeqGlobal` (or CoC), so
  `loadModule()` actually runs.

```xpp
/// <summary>
/// Registers the fleet module's number-sequence references with the framework.
/// </summary>
public class NumberSeqModuleFleet extends NumberSeqApplicationModule
{
    protected void loadModule()
    {
        NumberSeqDatatype datatype = NumberSeqDatatype::construct();

        datatype.parmDatatypeId(extendedTypeNum(FmVehicleId));
        datatype.parmReferenceHelp(literalStr("@Fleet:VehicleIdReferenceHelp"));
        datatype.parmWizardIsContinuous(false);
        datatype.parmWizardIsManual(NoYes::No);
        datatype.parmWizardIsChangeDownAllowed(NoYes::Yes);
        datatype.parmWizardIsChangeUpAllowed(NoYes::Yes);
        datatype.parmWizardHighest(0);
        datatype.parmSortField(1);
        datatype.addParameterType(NumberSeqParameterType::DataArea, true, false);

        this.create(datatype);   // not a NumberSeqReference field assignment
    }

    public NumberSeqModule numberSeqModule()
    {
        return NumberSeqModule::Fleet;
    }
}
```

## 2. The parameters-table accessor

The `numRef<Id>()` accessor is a **static `NumberSequenceReference` method on the
module's own parameters table** — mirroring `CustParameters::numRefCustAccount()`
returning `NumberSeqReference::findReference(extendedTypeNum(CustAccount))`.

It does **not** belong on `CompanyInfo`. (`CompanyInfo` is real, and
`CompanyInfo::find()` is the standard way to read the current legal entity — it is
simply not where a module's number-sequence accessor lives.)

## 3. Form auto-numbering

```xpp
NumberSeqFormHandler numberSeqFormHandler;   // form member

/// <summary>
/// Lazily builds the handler that drives auto-numbering for this data source.
/// </summary>
public NumberSeqFormHandler numberSeqFormHandler()
{
    if (!numberSeqFormHandler)
    {
        numberSeqFormHandler = NumberSeqFormHandler::newForm(
            MyModuleParameters::numRefMyNumberId().NumberSequenceId,  // RefRecId, not a string
            element,
            MyTable_ds,
            fieldNum(MyTable, MyNumberId));
    }

    return numberSeqFormHandler;
}
```

- The first argument is a **`RefRecId`** taken from `.NumberSequenceId`.
  ❌ Not `.NumberSequence`, and ❌ not a string code.
- Then call the handler from the data source's `create()`, `write()`,
  `validateWrite()` and `delete()` overrides —
  `formMethodDataSourceCreate(...)`, `formMethodDataSourceWrite()`,
  `formMethodDataSourceValidateWrite(...)`, `formMethodDataSourceDelete()`.
  **This is not a one-time call in `init()`.**
- `MyModuleParameters` above stands in for your module's parameters table.

## 4. Runtime fetch

```xpp
NumberSequenceReference numSeqRef = NumberSeqReference::findReference(extendedTypeNum(FmVehicleId));
NumberSeq               numSeq    = NumberSeq::newGetNum(numSeqRef);
FmVehicleId             newId     = numSeq.num();

// numSeq.used();    // confirm the number was consumed
// numSeq.abort();   // release it if the insert is rolled back
```

## 5. Configuration choices

| Choice | Cost |
|---|---|
| **Continuous** (no gaps) | slower — the sequence is locked per fetch. Use only where legally required (vouchers, invoices). |
| **Non-continuous** (default) | gaps allowed, much faster. Correct for internal ids. |

Scope is `DataArea` (per company), `Global` (cross company) or `OperatingUnit`.

## Hard rules

- Verify every `parm*()` name against the SDK before relying on it:
  `d365fo get class NumberSeqDatatype --output json`.
- The EDT carries `NumberSequence = Yes` and a `NumberSequenceModule`.
- Always `abort()` the number when the surrounding transaction rolls back, or the
  sequence leaks a value.
- Never generate an id in X++ by reading MAX+1 — that is a race, and it bypasses
  the configured format entirely.
