---
description: The kernel (system) types X++ has that no AOT file declares — xRecord, Common, xSession, xApplication, Args, ClassFactory, the collection and reflection classes — why the metadata index cannot see them, and what that means for validation. Invoke when a type resolves at compile time but not in the index, when `validate references` warns about a declared type, or when reasoning about what every table buffer or object inherits.
applyTo: '**/*.xpp'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# System (kernel) objects

## The thing to understand first

X++ has two populations of types, and only one of them is in the AOT.

**Application types** — every `AxClass`, `AxTable`, `AxEnum`, `AxEdt` — live as
XML under `PackagesLocalDirectory`. The metadata index reads those files, so
`d365fo get class CustTable`, `search`, and the anti-hallucination gate in
`validate references` all work by looking them up.

**Kernel types** live in the AOS binaries. There is no file for them anywhere
in `PackagesLocalDirectory`, so the index cannot carry them, and an
index-only existence check is in no position to judge them. Checked against a
real installation: `xRecord`, `Common`, `xSession`, `xApplication`, `xGlobal`,
`Args` and `ClassFactory` have **no AOT file at all**, while `xUserInfo` — which
looks like it belongs to the same family — is an ordinary `AxClass`. The
spelling does not tell you which is which.

This is why `d365fo validate references` treats an unknown DECLARED type as a
**warning** rather than an error: the list of kernel names is known-incomplete
by construction, and failing a write because a name is absent from the index
would refuse correct code.

## The families

- **Record and object roots** — `Object`, `xRecord`, `Common`, `xSession`,
  `xInfo`, `xGlobal`, `xApplication`, `xVersion`, `Args`, `ClassFactory`
- **Forms at run time** — `FormRun`, `FormDataSource`, `FormControl` and every
  `Form*Control` subtype
- **Query object model** — `Query`, `QueryRun`, `QueryBuildDataSource`,
  `QueryBuildRange`, `QueryBuildLink`, `QueryFilter`
- **Collections** — `Map`, `Set`, `List`, `Array`, `Struct`, their enumerators,
  `RecordInsertList`, `RecordSortedList`
- **Reflection** — `DictTable`, `DictField`, `DictClass`, `DictEnum`,
  `DictType`, `DictIndex`, `TreeNode`
- **IO and interop** — `TextBuffer`, `Binary`, the `Xml*` family, `TextIo`,
  `CommaIo`, `Connection`, `Statement`, `ResultSet`, `CLRInterop`
- **Kernel enums** — `NoYes`, `Exception`, `JoinMode`, `MenuItemType`,
  `AccessType`, `TableGroup`, `UtilElementType`, `SortOrder` and others. These
  are the ones most often mistaken for hallucinations: `d365fo search any NoYes`
  offers `NoYesBlank`, `NoYesCombo` and `DefaultNoYes`, which are different
  types with real AOT files.

## What every table buffer already has

`xRecord`/`Common` give every buffer methods the table itself never declares —
which is exactly why the index has no row for them:

`insert`, `doInsert`, `update`, `doUpdate`, `delete`, `doDelete`, `write`,
`validateWrite`, `validateDelete`, `validateField`, `initValue`,
`modifiedField`, `clear`, `selectForUpdate`, `reread`, `orig`, `data`,
`setTmp`, `isTmp`, `skipDataMethods`, `skipEvents`, `skipDeleteActions`,
`recordLevelSecurity`, `canSubmitToWorkflow`, `renamePrimaryKey`.

`d365fo prepare test CustTable.validateWrite` lists these with their real
signatures for exactly this reason: the method has no AOT row, so an
index-only answer would say it does not exist.

**System fields**, likewise present on every table and declared by none:
`RecId`, `TableId`, `DataAreaId`, `RecVersion`, `Partition`,
`createdDateTime`, `createdBy`, `modifiedDateTime`, `modifiedBy`,
`createdTransactionId`, `modifiedTransactionId`.

## What every object has

`Object` gives every class instance `new`, `finalize`, `toString`, `handle`,
`notify`, `wait`, `usageCount`, `owner`, `equal`. A class that "overrides
`toString()`" is overriding a kernel method, not one its own hierarchy
declares.

## Checks

- A type that compiles but `d365fo search any <Name>` cannot find is very
  likely kernel. Confirm before concluding the code is wrong — that conclusion,
  drawn from an index miss, is the failure this topic exists to prevent.
- `d365fo validate references` reports unknown declared types as warnings.
  Treat a warning on a name from the families above as expected, and a warning
  on an application-looking name as worth chasing.
