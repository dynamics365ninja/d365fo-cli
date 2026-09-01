> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Global class statics

`Global` is an ordinary `AxClass` — `d365fo get class Global` finds it, unlike
the kernel types in [system-objects](system-objects.md). Measured on a real
installation it declares **375 static methods**.

## The distinction that decides how you call it

There are two ways a name with no receiver can resolve in X++: as a **compiler
predefined function**, or not at all. `Global` statics are not automatically
predefined. Of its 375 methods:

| | count | how to call it |
|---|---:|---|
| also a predefined function | **34** | bare `name(…)` works, and `Global::name(…)` also compiles |
| Global only | **341** | `Global::name(…)` — a bare call does not compile |

The 34 overlapping ones are the infolog and date helpers everyone writes
unqualified: `checkFailed`, `error`, `warning`, `info`, `classId2Name`,
`className2Id`, `con2Str`, `dateStartMth`, `dateEndMth`, `dateStartWk`,
`dateEndWk`, `dateStartQtr`, `dateEndQtr`, `dateEndYr`, `dateMax`, `dateNull`,
`dateMthFwd`, and their neighbours.

The other 341 — `aosClientMode`, `appl`, `base64str2con`,
`accessRight2NeededPermission`, `arabic2Roman`, `GetCurrentUri`,
`GetDeploymentEndPoint` and the rest — need the `Global::` prefix. Writing them
bare is a compile error, not a style choice.

## The trap in the other direction

Plenty of names that feel like Global statics are **predefined functions that
Global does not declare**. Checked against the compiler-captured catalog
(xppc 7.0.7996.33, 170 predefined functions) and against `Global` itself:

| name | predefined | on Global |
|---|:-:|:-:|
| `strFmt` | ✅ | ❌ |
| `today` | ✅ | ❌ |
| `setPrefix` | ✅ | ❌ |
| `curUserId` | ✅ | ❌ |
| `fieldId2Name` | ✅ | ❌ |
| `conPeek` | ✅ | ❌ |
| `funcName` | ✅ | ❌ |

So `Global::strFmt(...)` does **not** compile, however natural it reads.
Predefined functions are covered in
[xpp-runtime-functions](xpp-runtime-functions.md); this topic is only about
what `Global` itself declares.

## Deciding, in practice

1. Bare call fails to compile → check `Global` with
   `d365fo get class Global --output json` and qualify it.
2. `Global::` fails to compile → the name is a predefined function, not a
   Global static. Drop the prefix.
3. Neither works → `d365fo search any <Name>`; it may be a static on a
   different class, or not exist at all.

`d365fo validate xpp` catches the second case as `FN001`/`FN002` from the
compiler-captured table, so it is worth running before a build cycle rather
than after.

## Adding to Global

Do not. `Global` belongs to `ApplicationPlatform`, so a new method there is a
customisation of a Microsoft model that every later platform update has to
merge. Put the helper on a class of your own; if it truly must be reachable
without a receiver, that is what `Global` extensions via Chain of Command are
for — see [coc-extension-authoring](coc-extension-authoring.md).

Note that a CoC extension of `Global` still does not make a new method
callable bare: the predefined-function set is the compiler's, not the AOT's.
