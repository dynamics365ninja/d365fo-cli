---
id: runtime-frameworks
description: Use the D365FO platform frameworks that decide behaviour at runtime — Feature Management, the SysExtension plug-in factory, telemetry and infolog, and the Global Address Book. Invoke when the user asks to add a feature flag, replace an if/switch chain with a registered strategy, log or capture infolog output, or read a party's address or contact details.
covers: Feature Management, SysExtension, telemetry, Global Address Book
applyTo:
  - "**/AxClass/**Feature*.xml"
  - "**/AxClass/**Attribute.xml"
  - "**/AxClass/**Provider*.xml"
appliesWhen: User intent mentions feature management, feature flag, IFeatureMetadata, FeatureStateProvider, SysExtension, plug-in or strategy factory, SysAttribute, telemetry, infolog, SysGlobalTelemetry, global address book, DirPartyTable, or postal/electronic addresses.
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Runtime platform frameworks

## 1. Feature Management

Feature Management turns behaviour on and off **at runtime**, with no
recompilation — the opposite of a configuration key (see
`d365fo knowledge get security-modeling`).

**Registration shape.** A custom feature is a singleton class implementing
`IFeatureMetadata`, exported through `[ExportAttribute(...)]`. There is **no
`FeatureClassAttribute`** — that name resolves to nothing.

```xpp
using System.ComponentModel.Composition;
using Microsoft.Dynamics.ApplicationPlatform.FeatureExposure;

/// <summary>
/// Enables the enhanced vehicle-service validation introduced for fleet contracts.
/// </summary>
[ExportAttribute(identifierStr(Microsoft.Dynamics.ApplicationPlatform.FeatureExposure.IFeatureMetadata))]
public final class MyEnhancedValidationFeature implements IFeatureMetadata
{
    private static MyEnhancedValidationFeature instance;

    private void new()
    {
    }

    private static void TypeNew()
    {
        instance = new MyEnhancedValidationFeature();
    }

    [Hookable(false)]
    public static MyEnhancedValidationFeature instance()
    {
        return MyEnhancedValidationFeature::instance;
    }

    [Hookable(false)]
    public FeatureLabelId label()
    {
        return literalStr("@MyModel:EnhancedValidationLabel");
    }

    [Hookable(false)]
    public FeatureLabelId summary()
    {
        return literalStr("@MyModel:EnhancedValidationDesc");
    }

    [Hookable(false)]
    public int module()
    {
        return FeatureModuleV0::SystemAdministration;
    }

    [Hookable(false)]
    public boolean isEnabledByDefault()
    {
        return false;
    }

    [Hookable(false)]
    public boolean canDisable()
    {
        return true;
    }

    /// <summary>
    /// The only way callers should ask — nothing outside this class touches FeatureStateProvider.
    /// </summary>
    internal static boolean isEnabled()
    {
        return FeatureStateProvider::isFeatureEnabled(MyEnhancedValidationFeature::instance());
    }
}
```

**Rules:**

- `IFeatureMetadata` members are **instance** methods, every one
  `[Hookable(false)]`: `label()`, `summary()`, `module()`, `isEnabledByDefault()`,
  `canDisable()`, `learnMoreUrl()`. The workspace shows `summary()` — there is no
  `description()`.
- `FeatureStateProvider::isFeatureEnabled()` takes the feature **instance**, not
  `classStr()`.
- Wrap the call in a static `isEnabled()` so callers never reach for
  `FeatureStateProvider` themselves.
- **Never call `isFeatureEnabled()` inside a loop** — cache it in a local.
- Give `summary()` real content; it is what an administrator reads before
  flipping the switch.

## 2. SysExtension — registered strategies instead of if/switch

`SysExtension` resolves an implementation from an extensible enum value, so a new
strategy is a new class plus a new enum value and **zero** changes to the calling
code.

Four parts: an abstract base (or interface), an extensible enum, one *factory
attribute* class per family, and a lookup.

```xpp
/// <summary>
/// Base for the per-shipping-type processors resolved through SysExtension.
/// </summary>
public abstract class MyProcessorBase
{
    public abstract void process(MyTable _record);
}

/// <summary>
/// Registration attribute: carries the enum value each concrete processor handles.
/// </summary>
class MyProcessorAttribute extends SysAttribute implements SysExtensionIAttribute
{
    MyProcessorType processorType;

    public void new(MyProcessorType _processorType)
    {
        super();
        processorType = _processorType;
    }

    public str parmCacheKey()
    {
        return strFmt('%1;%2', classStr(MyProcessorAttribute), int2str(enum2int(processorType)));
    }

    public boolean useSingleton()
    {
        return false;
    }
}

/// <summary>
/// Express-shipping processor, discovered through its attribute rather than a switch.
/// </summary>
[MyProcessorAttribute(MyProcessorType::Express)]
public class MyExpressProcessor extends MyProcessorBase
{
    public void process(MyTable _record)
    {
        // express-specific logic
    }
}

/// <summary>
/// Resolves the processor for a shipping type. No if/switch chain to maintain.
/// </summary>
public static void runProcessor(MyTable _record, MyProcessorType _type)
{
    MyProcessorBase processor = SysExtensionAppClassFactory::getClassFromSysAttribute(
        classStr(MyProcessorBase),
        new MyProcessorAttribute(_type)) as MyProcessorBase;

    if (processor)
    {
        processor.process(_record);
    }
}
```

**Rules:**

- The enum must have `IsExtensible = Yes`, or another model cannot add a value.
- `parmCacheKey()` must be unique per strategy — a colliding key silently returns
  the wrong class.
- Use `classStr()` / `enumStr()`, never string literals, so a rename is a compile
  error.
- **`ExportMetadataAttribute` and `SysExtensionAppSuiteDecoratorForward` do not
  exist in D365FO** — both are AX 2012 / MEF-era names.
- `SysPluginFactory::Instance(namespace, className, metadataCollection)` is the
  .NET-plugin sibling. Different mechanism; do not mix the two.
- Do not reach for this with a single implementation. It earns its complexity
  only with several strategies.

## 3. Telemetry, infolog and diagnostics

| Call | Use |
|---|---|
| `info(...)` | informational, shown in the infolog |
| `warning(...)` | completed, but the user should know |
| `error(...)` | failed |
| `checkFailed(...)` | same as `error()` but returns `false` — for `validateWrite()` |

- Every message is a **label token**, never a literal (`BPErrorLabelIsText`).
- Structured telemetry goes through **`SysGlobalTelemetry`** (`logTrace`,
  `logEvent`, `logMetric`, `logMetricWithCustomProperties`). **There is no
  `SysTelemetry` class.** For richer Application Insights logging use
  `SysApplicationInsightsTelemetryLogger` from the Monitoring and Telemetry model.
- To capture infolog output programmatically, snapshot `infolog.infologData()`
  and walk the delta with `SysInfologEnumerator::newData()`. There is no
  `SysInfoLogScope` class.
- `print` writes to job output, not the infolog — it is not a logging call.
- Batch jobs: infolog output is persisted to batch history; use
  `BatchHeader.addRuntimeTask()` for progress.
- **Never log secrets or PII.**

```xpp
container               beforeData = infolog.infologData();
container               produced;
SysInfologEnumerator    enumerator;
SysInfologMessageStruct msgStruct;

myService.doSomething();

// Everything the service added since the snapshot.
produced   = conDel(infolog.infologData(), 1, conLen(beforeData));
enumerator = SysInfologEnumerator::newData(produced);

while (enumerator.moveNext())
{
    msgStruct = SysInfologMessageStruct::construct(enumerator.currentMessage());
    // currentException() returns Exception::Info / Warning / Error — not a SysInfologLevel.
    this.record(enumerator.currentException(), msgStruct.message());
}
```

## 4. Global Address Book

Every party with a real-world identity — customer, vendor, worker, contact —
hangs off `DirPartyTable` through a `Party` field.

- **Never add address or contact columns to a custom table.** Add a `Party` field
  (EDT `DirPartyRecId`) and use the GAB.
- Postal address: `LogisticsPostalAddress` joined through `DirPartyLocation`, or
  the convenience view `DirPartyPostalAddressView` for the primary one.
- Contact info: `LogisticsElectronicAddress` joined through `DirPartyLocation`,
  filtered by `LogisticsElectronicAddressMethodType`.
- `DirPartyType` is `Person | Organization | Team`.
- Create through `DirPartyTable::createNew(DirPartyType::Organization)` or the
  `DirParty` class (`constructFromPartyRecId`, `constructFromCommon`,
  `createOrUpdatePostalAddress`, `createOrUpdateContactInfo`). **Never insert into
  `DirPartyTable` directly**, and note there is no `GlobalAddressBookHelper` and
  no `DirPartyService` class.

```xpp
DirPartyRecId             partyRecId = custTable.Party;
DirPartyPostalAddressView addrView;

select firstonly addrView
    where addrView.Party     == partyRecId
       && addrView.IsPrimary == NoYes::Yes;
```

## Hard rules

- Ground every class name here before you call it:
  `d365fo get class FeatureStateProvider --output json`.
- Feature Management for new behaviour; configuration keys only for licensing and
  schema-level gating.
- A framework name that "sounds right" is the most common defect in this area —
  `SysTelemetry`, `FeatureClassAttribute`, `GlobalAddressBookHelper` and
  `ExportMetadataAttribute` are all plausible and all fictional.
