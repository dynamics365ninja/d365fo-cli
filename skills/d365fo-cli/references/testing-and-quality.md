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
- Test methods are `public void` and **must start with `test`** (case-insensitive).
  `[SysTestMethod]` categorises them.
- `setUp()` / `tearDown()` run before and after **each** test method.
- Assertions: `this.assertEquals()`, `assertNotEquals()`, `assertTrue()`,
  `assertFalse()`, `assertNull()`, `assertNotNull()`, `this.fail()`.
- **Every test runs in a transaction that is always rolled back**, so database
  state needs no cleanup.
- `[SysTestTarget(classStr(X), methodStr(X, y))]` records what the class covers.
- `SysTestSuite` groups test classes for batch execution.
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
    public void testCalculateDiscountIsZeroAtZeroRate()
    {
        AmountMST discount = service.calculateDiscount(1000, 0);
        this.assertEquals(0, discount, 'Discount must be 0 when the rate is 0');
    }

    [SysTestMethod]
    public void testCalculateDiscountRejectsNegativeAmount()
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
