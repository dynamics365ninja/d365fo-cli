> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Testing and quality gates

> Two layers, and they cost very different amounts. The offline gates run in
> milliseconds without a VM and catch the majority of generated-code defects; the
> SysTest layer needs a build and a database. Run the cheap ones first, always.

## 1. The offline gates — before every write

```sh
d365fo validate references --file MyClass.xpp --output json   # exit 2 = hallucinated symbols
d365fo validate xpp        --file MyClass.xpp --output json   # exit 2 = BP errors
d365fo lint --output sarif > lint.sarif                       # whole-model sweep
```

`validate references` proves every type, field, method (including arity), enum,
label and intrinsic target against the index — it catches invented names **before**
the compiler does. `validate xpp` runs the BP rule canon offline. Fix everything
they report in the same turn, re-run, and only then write.

## 2. SysTestCase — unit tests

- The test class **extends `SysTestCase`** and lives in the same model as the code
  under test, or a dedicated test model.
- Test methods are `public void`, take **no parameters**, and are discovered two
  ways: the name starts with `test` (case-insensitive) **or** the method carries
  an attribute deriving from `SysTestFilterAttribute` — `[SysTestMethod]`, or
  `[SysTestCheckInTest]` for a test that creates real records. Either is enough,
  so the `test` prefix is **not** required when the attribute is present — the
  discovery loop in `SysTestCase.testMethods()` tries `strStartsWith` on the
  name first and falls back to the attribute. Prefer the attribute plus an
  intention-revealing `<memberUnderTest>_<scenario>_<expectedResult>` name, e.g.
  `calculateDiscount_zeroRate_returnsZero` — which is also what
  `d365fo generate systest` emits.
- `setUp()` / `tearDown()` run before and after **each** test method.
- Assertions (inherited from `SysTestAssert`): `this.assertEquals()`,
  `assertNotEqual()` (no trailing *s*), `assertTrue()`, `assertFalse()`,
  `assertNull()`, `assertNotNull()`, `assertSame()`, `assertNotSame()`,
  `assertRealEquals()`, `assertUTCDateTimeEquals()`, `this.fail()`.
- **There is no `assertExpectedException`.** An expected exception is
  *declared*, not asserted: call `this.parmExceptionExpected(true)` (optionally
  with a message pattern) **before** the call that must throw;
  `clearExceptionExpected()` resets it.
- **Every test runs in a transaction that is always rolled back**, so database
  state needs no cleanup — and there is no rollback attribute to add.
- `[SysTestTarget(classStr(X), UtilElementType::Class)]` records what the class
  covers — the second argument is a `UtilElementType`, **not** a method name
  (xppc: *"Cannot implicitly convert from type 'str' to type
  'Enumeration(utilElementType)'"*).
- `SysTestSuite` groups test classes for batch execution.
- `d365fo generate systest <Name> [--table T | --class C] [--method M] [--atl]
  [--install-to <Model>]` scaffolds the `SysTestCase` skeleton: an
  Arrange/Act/Assert body per subject that opens red with
  `this.fail(...)`, the `[SysTestTarget]` attribute for the resolved
  table/class, and — with `--atl` — an `AtlDataRootNode` field plus the
  `setUpTestCase()` that constructs it. `<Declaration>` and every `<Source>`
  are emitted as CDATA, matching every AxClass file the platform ships, so
  X++ doc comments (`<summary>`, `<param>`) can be written inside them with
  literal angle brackets.
- Edit an already-generated method body with `d365fo modify method` rather than
  a plain text editor: it writes through the Bridge and preserves the CDATA
  wrapper. Hand-editing the raw XML is fine too, but keep the
  `<![CDATA[ ... ]]>` in place — replacing it with XML-escaped text puts a
  literal `&lt;summary&gt;` into Visual Studio and into the compiled X++,
  because D365FO does not decode those entities back.
- X++ has **no mocking framework**. Isolate dependencies by extracting an
  interface or by delegation, and inject the fake in `setUp()`.
- Naming: `<TestedClass>Test` (for example `FmVehicleServiceTest`). Pick one
  convention per model and keep it — a mixed `<Class>_Test` / `<Class>Test` model
  is the sort of inconsistency that makes a suite hard to run selectively.
- Run from Visual Studio's Test Explorer, or `d365fo test run` on the VM.

```xpp
/// <summary>
/// Unit tests for the fleet service-charge calculation.
/// </summary>
[SysTestTarget(classStr(FmVehicleService), methodStr(FmVehicleService, calculateDiscount))]
class FmVehicleServiceTest extends SysTestCase
{
    FmVehicleService service;

    public void setUp()
    {
        super();
        service = new FmVehicleService();
    }

    [SysTestMethod]
    public void calculateDiscount_zeroRate_returnsZero()
    {
        AmountMST discount = service.calculateDiscount(1000, 0);
        this.assertEquals(0, discount, 'Discount must be 0 when the rate is 0');
    }

    [SysTestMethod]
    public void calculateDiscount_negativeAmount_throwsError()
    {
        try
        {
            service.calculateDiscount(-100, 10);
            this.fail('Expected an exception for a negative amount');
        }
        catch (Exception::Error)
        {
            // expected
        }
    }
}
```

## 3. ATL — the acceptance test library

For integration-level tests that need realistic master data.

- The entry point is **`AtlDataRootNode::construct()`**; navigate from it
  (`data.invent()`, `data.sales()`, …).
- The concepts are Creators, Commands, Queries and Specifications — the
  `AtlCommand*` family.
- **There is no `AtlScenario` and no `AtlDataHelper` class.** Both are plausible
  and neither exists.
- Create transient test data through the ATL data-root creators or in `setUp()`.
- Check whether the model already has a shared base test class that wraps
  `AtlDataRootNode::construct()` and exposes the common nodes as members (e.g.
  `invent`, `purch`, `sales`). Extend that base instead of calling
  `AtlDataRootNode::construct()` again in every new test class.

## 4. Where the eval loop fits

This repo's own eval catalog (`d365fo eval list`, `d365fo eval run <case>`) proves
the *generators* rather than the generated code — it replays a case's canonical
arguments and diffs the artifact against a reviewed golden. Add a case whenever
you fix a scaffolder defect, so the fix cannot regress:
`docs/AGENT_EVAL_LOOP.md`.

## Hard rules

- Offline gates before every write; they cost milliseconds and catch invented
  names, which are the most expensive defect class to find later.
- A test that needs cleanup is a test doing too much — the transaction rollback
  already handles database state.
- Never assert on infolog text. Assert on the return value or the record state.
- Test method names say what is being asserted, not what is being called.
- Never call `d365fo build` / `test run` unprompted: both are slow and
  Windows-only. Scaffold, validate, then tell the user what to run.
