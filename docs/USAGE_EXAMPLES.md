# Usage examples

Whole tasks, start to finish. [EXAMPLES.md](EXAMPLES.md) shows one invocation per command; this
shows the sequences those commands are actually used in, and the decisions between them.

Every command here was run against a live D365FO installation while this was written. Where the
output taught something, it is quoted.

---

## 1. Add a field to a standard table

The task an agent gets wrong most often, because the obvious move — open `CustTable` and edit it —
is the one that makes the model un-upgradeable.

### Ground first

```sh
d365fo prepare change CustTable --type table --goal "add a fleet reference field"
```

One call returns the object, what already extends it, the strategies that apply, and a
**grounding token**. Two things worth knowing about it:

- **Pass `--type`.** `CustTable` is a table *and* a form; without it, `prepare` resolves to the
  form and cheerfully answers about the wrong object. The payload's `objectType` says which one
  it took — read it.
- The `existingCocExtensions` list is not decoration. On this installation `CustTable` already has
  **98** CoC extensions across a dozen models; a new one that duplicates an existing wrapper is
  worse than no change at all.

### Generate the extension

```sh
d365fo generate extension Table CustTable --suffix ConFleet \
  --grounding-token 4d0251b1a554e9e11ee247ab272d955a \
  --out ./CustTable.ConFleet.xml
```

```json
"grounding": { "tokenSupplied": true, "tokenValid": true,
               "verifiedReferences": 1, "referenceErrors": 0, "bpErrors": 0 }
```

The token is how the tool knows the index was consulted before the write, rather than a name being
invented. With `D365FO_GROUNDING_ENFORCE=true` a write without a valid one is refused outright.

### Or let `modify` do both

On a machine with the bridge enabled (`D365FO_BRIDGE_ENABLED=1`), the extension and the field are
one call — `--extension` writes to `CustTable.<suffix>` instead of to the table itself, creating
the extension if it is not there yet:

```sh
d365fo modify add-field CustTable ConFleetVehicleId --edt VIN \
  --extension ConFleet --extension-model ConFleet
```

Without `--extension` this edits the table **itself**, which is over-layering — the write path
refuses that on a standard model unless it is forced, and the refusal is the feature.

`add-field` resolves the concrete `AxTableField*` subtype from the EDT rather than asking you for
it: a field declared as the wrong subtype parses, saves, and loses its properties on read.

### Check before you build

```sh
d365fo validate xpp ./CustTable.ConFleet.xml
d365fo validate references ./CustTable.ConFleet.xml
```

```
No violations found.
All 1 reference(s) verified against the index. No hallucinated symbols detected.
```

Two different questions: is the document shaped correctly, and does every name in it exist. The
second is the one that catches an invented EDT.

---

## 2. Extend behaviour without over-layering

```sh
d365fo find coc CustTable::validateWrite          # who already wraps it?
d365fo get class CustTable --output json          # the real signature to match
d365fo generate coc CustTable --method validateWrite --out ./CustTable_ConFleet_Extension.xml
```

The order matters. `find coc` first, because a second wrapper on the same method is a merge
conflict waiting for a release; `get class` second, because a CoC wrapper whose signature differs
from the target by one default value does not compile, and the compiler's message points at the
wrapper rather than at the mismatch.

If CoC cannot reach it — a form, a static, an event — the answer is an event handler:

```sh
d365fo generate event-handler CustTableFormHandler \
  --source-kind Form --source-object CustTable --event Initialized \
  --out ./CustTableFormHandler.xml
```

---

## 3. Build a report end to end

```sh
d365fo generate report ConFmVehicleReport --field VIN --field Make \
  --caption "@Fleet:VehicleReport" --out ./ConFmVehicleReport.xml
```

One command, five artefacts: the `AxReport`, a TempDB table for the dataset, the data provider,
the controller, and an output menu item. They land beside `--out`, and they are *not* optional
extras — a report without its controller has no entry point.

Extending a report Microsoft ships is a different job, and the reason is worth stating: you cannot
over-layer the report, so the extension goes on the data provider.

```sh
d365fo get class AssetBarCodeDP --output json     # read the dataset accessor off the DP
d365fo generate report-extension dataset --dp AssetBarCodeDP --tmp-table AssetBarCodeTmp \
  --dataset-accessor geAssetBarCodeTmp --suffix ConFleet --out ./AssetBarCodeDPConFleet.xml
```

Read the accessor, never derive it. The platform ships `geAssetBarCodeTmp` — the typo is in the
product, and a handler that calls the name you would expect does not compile.

---

## 4. Write a test for a table method

```sh
d365fo prepare test CustTable.validateWrite
```

The reply is a test *shape*, not a template: it names the class convention (`CustTableTest extends
SysTestCase`), lists the methods worth testing with their real signatures, and gives the steps —
including the one that matters most:

> Arrange a buffer, do not select one: `CustTable rec; rec.initValue();` then set exactly the
> fields the rule reads. A row fetched from the database drags in whatever demo data the box
> happens to hold.

Then scaffold it, red first:

```sh
d365fo generate systest CustTableTest --table CustTable --method validateWrite \
  --out ./CustTableTest.xml
```

A test that has never failed is not evidence that anything works. If you have a VM, prove the
runner itself discriminates before trusting a green suite:

```sh
d365fo oracle runtime                     # is the runner even wired to a database?
d365fo oracle runtime --negative-control  # a class that passes, fails and throws on purpose
```

---

## 5. Work out what a change will break

```sh
d365fo analyze impact CustTable --output json      # CoC wrappers, handlers, extensions downstream
d365fo find refs CustTable::validateWrite          # every caller
d365fo security coverage CustTableListPage --type Menuitem   # who can reach it
```

`find refs` reports how it searched, not just what it found. A `count: 0` with a caveat means the
corpus could not be read — a degraded zero, not "unused". Deleting on the strength of the first
kind of zero is how a method disappears from a build three weeks later.

---

## 6. Triage a build error

```sh
d365fo explain-error BPErrorPrivilegeNotCoveredByDuty
d365fo bp-moniker search "not covered"       # case-sensitive, like xppbp itself
```

`explain-error` maps a compiler or best-practice code to what it actually means and the fix; the moniker
catalog is 409 canonical monikers extracted from the rule sets of a real installation, so a
misspelled moniker gets the right spelling back rather than "unknown".

---

## 7. Fix a label without breaking the translations

```sh
d365fo labels search "Vehicle" --lang en-us,cs
d365fo labels update Fleet:Vehicle --lang en-us,cs \
  --text en-us=Vehicle --text cs=Vozidlo --install-to ConFleet
```

One `--text` per language, and it is **required** as soon as more than one language is targeted:
a translation is not the same string in every language. The earlier shape took a single value for
all of them and wrote the English string into the Czech file, reporting success. `update` also
refuses a key that does not exist rather than creating it — a typo in a correction would otherwise
produce a second label and report a fix.

---

## 8. After editing XML outside the tool

```sh
d365fo index sync ./AxTable/CustTable.xml     # re-read the model that file belongs to
d365fo get table CustTable --output json      # confirm the index sees the change
```

The index is a mirror. Visual Studio does not tell it when you save, and an index built before
your edit will answer about your file with complete confidence and yesterday's content.

---

## 9. Ship a model

```sh
d365fo verify ConFleet                  # every object on disk is listed in the .rnrproj
d365fo lint                             # best practice over the index — custom models only, by default
d365fo build --project ./ConFleet.rnrproj
d365fo test run --suite ConFleet.Tests
```

`verify` is the cheap one to run first: an object the project file does not list does not compile
into the deployable package, and nothing else in the chain reports it. The failure surfaces as a
missing type at runtime, on someone else's machine.

---

## See also

- [EXAMPLES.md](EXAMPLES.md) — one example per command.
- [CAPABILITIES.md](CAPABILITIES.md) — everything the tool can do.
- [QUICK_START.md](QUICK_START.md) — from nothing to the first answer.
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — when a step above does not do what it says.
