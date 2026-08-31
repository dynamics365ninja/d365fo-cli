---
id: xpp-runtime-functions
description: The ~170 predefined (run-time) X++ functions the compiler actually has — argument counts as the compiler enforces them, the optional trailing parameters the language reference presents as fixed, the variadic ones, and the AX 2012 names that are gone. Invoke when calling or debugging strFmt/subStr/conPeek/date2Str-style global functions, or an FN001/FN002 finding.
covers: predefined function catalog, arities, gone/obsolete names
applyTo:
  - "**/*.xpp"
appliesWhen: User intent involves predefined/global functions (strLen, subStr, strFmt, conPeek, date2Str, num2Str, …), wrong-argument-count compile errors, or "does not denote a predefined function".
---

> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Run-time (predefined) functions

The ~170 functions that are not members of any class. The compiler is the
authority on which exist and how many arguments each takes — `d365fo validate
xpp` checks both as `FN001`/`FN002` from a table captured **from the compiler
itself** (xppc 7.0.7996.33), and that table disagrees with the language
reference in both directions.

Call them **unqualified**: X++ requires `this.` or `ClassName::` for methods,
so a bare `name(…)` is a predefined function, a `Global::` static or a local
function — never an instance method.

## The catalog, by family

- **Conversion**: `any2Date/Enum/Guid/Int/Int64/Real/Str`, `str2Date(text,
  sequence)`, `str2Datetime`, `str2Enum(typeVar, text)`, `str2Guid`, `str2Int`,
  `str2Int64`, `str2Num`, `str2Time`, `int2Str`, `int642Str`, `uint2Str` (use
  it for RecIds — `int2Str` overflows), `num2Str(value, chars, decimals, sep1,
  sep2)` — all five arguments, `num2Char`, `char2Num(text, position)`,
  `guid2Str`, `enum2Str(value)` — the value ALONE,
  `enum2Symbol(enumNum(MyEnum), value)` — enum id AND value,
  `symbol2Enum(enumNum(MyEnum), text)`, `enum2int`, `enum2Value`.
- **String**: `strLen`, `strUpr`, `strLwr`, `subStr(text, position, number)` —
  1-based, `strDel`, `strIns`, `strRep`, `strFind`/`strScan`/`strNFind` — all
  FOUR arguments (text, chars, start, count), `strKeep`, `strRem`, `strLTrim`,
  `strRTrim`, `strAlpha`, `strCmp`, `strLine`, `strPoke`, `strPrompt`,
  `strReplace(text, from, to)`, `strSplit(text, separator)` — returns a
  **List**, not a container, `strStartsWith`, `strEndsWith`, `strContains`,
  `strLFix`/`strRFix` (2 or 3 args), `match(pattern, text)`.
- **Container**: `conLen`, `conPeek(container, position)` — 1-based,
  `conDel(container, start, number)`, `conNull`, `con2Str`, `str2Con`.
  `conIns`, `conFind` and `conPoke` are **variadic** — no count to check.
- **Date**: `today`, `timeNow`, `systemDateGet`/`systemDateSet`, `year`,
  `mthOfYr`, `dayOfMth`, `dayOfWk`, `dayOfYr`, `wkOfYr`, `mkDate(day, month,
  year)`, `endMth`, `nextMth`/`nextQtr`/`nextYr`, `prevMth`/`prevQtr`/`prevYr`,
  `dayName`, `mthName`, `dateNull`, `dateMax`, `dateMthFwd`, `dateStartMth`,
  `dateEndMth`.
- **Math**: `abs`, `round(value, decimals)`, `decRound`, `power`, `trunc`,
  `frac`, `exp`, `exp10`, `log10`, `logN`, the trigonometric set. `max` and
  `min` are **variadic**. Business/finance: `cTerm`, `ddb`, `fV`, `pmt`, `pv`,
  `rate`, `sln`, `syd`, `term`, `intvMax`/`intvName`/`intvNo`/`intvNorm`.
- **Reflection/session**: `classIdGet`, `dimOf`, `typeOf`, `tableId2Name`,
  `tableName2Id`, `fieldId2Name(tableId, fieldId [, arrayIndex])`,
  `fieldName2Id`, `indexId2Name`, `indexName2Id`, `classId2Name`,
  `className2Id`, `enumName2Id`, `curExt`, `curUserId`, `funcName`,
  `getPrefix`, `setPrefix`, `sessionId`, `getCurrentPartition`, `prmIsDefault`,
  `runAs` (4–7 args).

## The traps the compiler settled

- **Optional trailing arguments** the reference presents as fixed: `date2Str`
  takes **7 or 8** (the 8th is DateFlags; the platform calls it with 7 in 161
  places), `datetime2Str` 1 or 2, `fieldId2Name` 2 or 3, `con2Str` 1 or 2,
  `str2Con` 1 to 3, `strLFix`/`strRFix` 2 or 3, and
  `info`/`warning`/`error`/`checkFailed` 1 to 3 (message, helpUrl,
  SysInfoAction).
- **GONE on current versions**, though AX 2012 had them: `corrFlagGet`,
  `dateMin`, `int2Enum`, `refPrintAll`, `typeName2Id` — *"The name 'x' does not
  denote a predefined function, a static method on the Global class nor a
  previously defined local function"* (`FN002`).
- **Obsolete** (compiles with a warning): `dateStartWk`, `dateEndWk`,
  `dateStartYr`, `dateEndYr`.
- The enum-conversion family is the classic arity confusion: `enum2Str` takes
  the value alone (it resolves that value's label in the session language, which
  is why it needs no enum id); its neighbours `enum2Symbol`/`symbol2Enum` take
  the enum id AND the value/symbol. The wrong one of the pair compiles in your
  head and not in xppc.
- `today()` compiles but fails `BPUpgradeCodeToday` — use
  `DateTimeUtil::getToday(DateTimeUtil::getUserPreferredTimeZone())`.
- Getting a count wrong is caught **offline**: `d365fo validate xpp` reports
  `FN001` with the exact xppc text ("'subStr' expects 3 argument(s), but 2
  specified") and `FN002` for a function this version does not have.
