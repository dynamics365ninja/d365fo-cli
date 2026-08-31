> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# X++ runtime types

## 1. Collections and containers

Two families. The kernel collection classes (`List`, `Map`, `Set`, `Struct`,
`Array`) are **reference** types with enumerators. The primitive `container` is a
**value** type — assignment copies it.

| Type | Shape | Key methods |
|---|---|---|
| `List` | ordered, duplicates allowed | `addEnd`, `addStart`, `elements`, `getEnumerator` |
| `Map` | key → value | `insert`, `exists`, `lookup`, `remove`, `elements` |
| `Set` | unordered, unique | `add`, `in`, `remove`, `elements` |
| `Struct` | named fields | `add(name, value)`, `value(name)`, `exists(name)` |
| `container` | value type, 1-based | `conLen`, `conPeek`, `conIns`, `conDel`, `conFind`, `conNull` |

- Element types are declared at construction with the `Types` enum:
  `new List(Types::String)`, `new Map(Types::Int64, Types::Class)`.
- **`Map.lookup()` throws when the key is absent.** Guard with `exists()` or
  iterate with a `MapEnumerator`.
- Use a `Set` for membership and de-duplication instead of scanning a `List`.
- **The container anti-pattern:** `c += [value]` inside a loop reallocates the
  whole container every iteration — O(n²). Accumulate in a `List` and convert once
  at the end.
- The `conXxx` accessors are intrinsics, not methods: they return a new container
  and mutate nothing.
- Only a `container` (and the `pack()`/`unpack()` pattern built on it) can cross
  the client/server boundary or live in a table field. `List`, `Map` and `Set`
  implement `pack()`/`unpack()` so they can be marshalled through one.
- A SysOperation data contract exposes primitives or a container — **never a raw
  `List`/`Map` property**. Round-trip with `List::create(packedContainer)`.
- An enumerator is invalidated by mutating its collection. Collect the changes and
  apply them after the loop.
- None of these are thread-safe and none survive the call.

```xpp
List           accountNums = new List(Types::String);
ListEnumerator enumerator;
CustTable      custTable;

while select AccountNum from custTable
{
    accountNums.addEnd(custTable.AccountNum);
}

enumerator = accountNums.getEnumerator();
while (enumerator.moveNext())
{
    // enumerator.current()
}

// One conversion at the end, when a container is genuinely needed.
container packed = accountNums.pack();
```

## 2. Dates, times and time zones

Every `utcdatetime` is stored in **UTC**. Convert only at the edge — form display,
report, file export.

- `DateTimeUtil::utcNow()` — the current UTC instant; the right default for
  created/modified stamps.
- `DateTimeUtil::getSystemDateTime()` honours a user's session date override;
  `utcNow()` does not. Use the session-aware one for **business** decisions and
  `utcNow()` for audit stamps.
- For a business **date**: `DateTimeUtil::getSystemDate(DateTimeUtil::getUserPreferredTimeZone())`.
  The older `systemDateGet()` still compiles but raises `BPUpgradeCodeSystemDate`.
- **Never `today()`** — it reads the AOS clock, ignores both the session date and
  the user time zone, and is a BP error (`BPUpgradeCodeToday`).
- Convert with `DateTimeUtil::applyTimeZoneOffset(utcValue, tz)` (UTC → local) and
  `DateTimeUtil::removeTimeZoneOffset(localValue, tz)` (local → UTC). The names
  read backwards until you internalise which direction each goes.
- The zone comes from `DateTimeUtil::getUserPreferredTimeZone()` or
  `DateTimeUtil::getCompanyTimeZone()`. Never hardcode a `Timezone` value.
- Build from parts with `DateTimeUtil::newDateTime(date, timeOfDay, tz)`; split
  with `DateTimeUtil::date()` / `::time()`.
- Arithmetic through `DateTimeUtil::addDays/addHours/addMinutes/addSeconds/addMonths/addYears`
  — never by casting to `int64` and adding seconds.
- Sentinels are `DateTimeUtil::minValue()` / `::maxValue()`, not `0` or
  `dateNull()`. Date-effective (`ValidTimeState`) tables use `maxValue()` for
  "no end date".
- Interchange with `DateTimeUtil::toStr()` / `::parse()` (ISO 8601,
  culture-invariant). `datetime2Str()` / `str2Datetime()` are locale-dependent and
  belong to the UI only.
- Compare `utcdatetime` values directly — they are all UTC. Converting both sides
  first is redundant and breaks across DST.
- **A whole local day is a UTC range**: convert the local day boundaries once and
  range on the stored UTC column. Never range on a converted expression.

```xpp
Timezone    userTimeZone = DateTimeUtil::getUserPreferredTimeZone();
date        businessDate = DateTimeUtil::getSystemDate(userTimeZone);
utcdatetime dayStartUtc;
utcdatetime dayEndUtc;
FmVehicle   vehicle;

dayStartUtc = DateTimeUtil::removeTimeZoneOffset(
    DateTimeUtil::newDateTime(businessDate, 0), userTimeZone);
dayEndUtc = DateTimeUtil::addSeconds(
    DateTimeUtil::removeTimeZoneOffset(
        DateTimeUtil::newDateTime(businessDate + 1, 0), userTimeZone), -1);

while select vehicle
    where vehicle.CreatedDateTime >= dayStartUtc
       && vehicle.CreatedDateTime <= dayEndUtc
{
    // one local day, expressed as a UTC range on the stored column
}
```

## 3. .NET interop

Three things go wrong: the call runs on the wrong tier, the CLR exception is
swallowed, and the type is written fully qualified everywhere.

- Declare `using System.Text;` above the class declaration. X++ has a `using`
  **declaration** for namespaces — there is no `using` *statement*, so dispose
  deterministically in a `finally`.
- CLR calls must run where the assembly is deployed: a `server` static method, or
  a class with `RunOn = Server`. A client-tier call against a server-only assembly
  fails at runtime, not at compile time.
- Assert first: `new InteropPermission(InteropKind::ClrInterop).assert();`, and
  `CodeAccessPermission::revertAssert()` in the `finally`.
- **Catch `Exception::CLRError`, not `Exception::Error`** — a bare `Error` handler
  does not catch a CLR exception, and the diagnostic is lost. Pull the message
  from `CLRInterop::getLastException()`.
- Marshalling: `str` ↔ `System.String` and the numeric primitives convert
  implicitly; `anytype` needs `CLRInterop::getAnyTypeForObject()` /
  `getObjectForAnyType()`.
- A CLR enum parameter needs
  `CLRInterop::parseClrEnum('System.StringComparison', 'OrdinalIgnoreCase')` — an
  X++ enum literal will not bind.
- A CLR array is created with `new System.String[3]()` and read/written with
  **`GetValue()` / `SetValue()`** — X++ `[]` indexing on a managed array is a
  compile error whose message says exactly that (*"…Use the SetValue and
  GetValue methods on managed array types"*). Properties are `get_X()` /
  `set_X()`.
- Null-check with `if (clrObject == null)`, never against an X++ empty value.
- Reference the assembly from the model's References node. A GAC-only assembly
  compiles locally and breaks on a clean build machine.
- Prefer the X++ equivalent when one exists — interop costs marshalling and blinds
  the compiler.

```xpp
using System.Text;

/// <summary>
/// Joins the values into one CSV line on the server tier, where the assembly lives.
/// </summary>
public static server str buildCsvLine(container _values)
{
    str result;
    int i;

    new InteropPermission(InteropKind::ClrInterop).assert();
    try
    {
        StringBuilder builder = new StringBuilder();

        for (i = 1; i <= conLen(_values); i++)
        {
            if (i > 1)
            {
                builder.Append(',');
            }
            builder.Append(any2Str(conPeek(_values, i)));
        }

        result = builder.ToString();
    }
    catch (Exception::CLRError)
    {
        // Without this branch the real .NET message is lost.
        System.Exception clrException = CLRInterop::getLastException();
        error(clrException.get_Message());
    }
    finally
    {
        CodeAccessPermission::revertAssert();
    }

    return result;
}
```

## 4. Reflection — the `Dict*` API

`DictTable`, `DictField`, `DictClass`, `DictEnum` expose the AOT at runtime. Use
them for genuinely generic code — never as a substitute for the compile-time
intrinsics, which the compiler and the cross-reference can check.

- **Seed from an intrinsic, never a string**: `new DictTable(tableNum(CustTable))`,
  `new DictField(tableNum(CustTable), fieldNum(CustTable, AccountNum))`,
  `new DictEnum(enumNum(NoYes))`.
- `DictTable`: `name()`, `label()`, `fieldCnt()`, `fieldCnt2Id(i)`,
  `fieldObject(fieldId)`, `makeRecord()`.
- `DictField`: `name()`, `label()`, `baseType()`, `enumId()`, `typeId()`.
- `DictEnum`: `value2Label()`, `value2Symbol()`, `symbol2Value()`, `values()` —
  the correct way to display an enum whose type is only known at runtime.
- **`DictClass` has no `hasStaticMethod()`/`hasObjectMethod()`.** Those live on the
  application-layer `SysDictClass`, which extends `DictClass`, so one object can do
  both the guard and the call.
- `SysDictTable` / `SysDictField` / `SysDictClass` add security-aware convenience
  helpers over the kernel classes.
- `fieldId2Name()` / `tableId2Name()` and their inverses are the lightweight
  lookups when only a name↔id translation is needed.
- **Reflection defeats the cross-reference.** A table or method reached only
  through a `Dict*` call is invisible to "find references" and survives a rename
  as a runtime error. Keep the reflective surface small and covered by tests.
- Resolve metadata **once outside** the record loop — reflective calls per row are
  expensive.
- `Dict*` reads metadata only; it cannot author AOT elements.

```xpp
/// <summary>
/// Guarded dynamic dispatch — a missing method is a handled case, not a crash.
/// </summary>
public static void runIfPresent(ClassId _classId, str _methodName)
{
    // SysDictClass, not DictClass: hasStaticMethod() is application-layer only.
    SysDictClass dictClass = new SysDictClass(_classId);

    if (dictClass.hasStaticMethod(_methodName))
    {
        dictClass.callStatic(_methodName);
    }
}
```

## 5. Macros — legacy, use sparingly

A macro library is an `AxMacroDictionary` element whose entire body is its
`Source` property, included with `#<LibraryName>`.

- Macros expand **before** compilation: no type check, no IntelliSense, no
  cross-reference, no debugger step. An error surfaces in the expanded line.
- Macros are **not extensible** — anything modelled as a macro is a hard fork
  point.
- Prefer instead: `public const int MyLimit = 100;` for a value, a base enum for a
  closed set, a static method for a code fragment, a label for user-facing text.
- Never put business logic in a `#localmacro` — untestable and invisible to a
  refactor.
- The remaining mainstream use is flight names
  (`#define.MyFeatureFlight('MyFeatureFlight')`), matching the platform's own
  convention.
- `#if.Never` / `#endif` conditional compilation should never ship. Dead code
  belongs deleted.

## Hard rules

- `List` to accumulate, `container` to marshal. Never build a container in a loop.
- Every `utcdatetime` in the database is UTC; convert only for display.
- `Exception::CLRError` for interop failures, with the message from
  `CLRInterop::getLastException()`.
- Seed every `Dict*` object from `tableNum` / `fieldNum` / `classNum` / `enumNum`.
- Ground the class names — `d365fo get class SysDictClass --output json` — before
  calling a method; the kernel/application split in this area is easy to get wrong.
