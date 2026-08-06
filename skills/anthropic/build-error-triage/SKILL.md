---
name: build-error-triage
description: Turn a D365FO compiler, best-practice, or runtime error into the specific fix it calls for. Invoke when the user pastes a build log, an xppc message, a BP violation, or an infolog stack trace and asks what it means or how to fix it.
applies_when: User pastes or refers to a compiler error, build failure, xppc output, BP warning or error code, an infolog exception, or asks "what does this error mean".
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Triaging a build or runtime error

## Start here — do not read the log by eye

```sh
# A whole log, from a file or stdin
d365fo explain-error --file build.log --output json
cat build.log | d365fo explain-error --output json

# One pasted message
d365fo explain-error "The field 'CreditMaxx' does not exist on table CustTable." --output json
```

The command parses the xppc line grammar into structured diagnostics
(`{object, member, line, column, message, hint}`), scores every message against
the fix-hint rules, and returns the ranked fixes plus the knowledge topic behind
each. A message that matches nothing is returned **verbatim** rather than
answered with the nearest-looking rule — an unexplained message is information,
a wrong explanation is not.

## Before you trust any of it: stale symbols

If the log says a package "has not been successfully compiled since it was last
changed", or asks for a full build, **every other error in that log is suspect**.
Rebuild first (`d365fo build --full` on the VM) and re-read.

## The families, and what each actually means

| Symptom | What it is | Fix |
|---|---|---|
| `SYS10028`, "must call next" | a CoC wrapper without `next` | call `next <method>(...)` unconditionally, at first-level scope |
| "Overlayering not allowed" | direct modification of Microsoft/ISV code | CoC, event handler, or an object extension |
| "cannot be deserialized as Ax…" | the XML names an element type the provider does not know | `d365fo validate metadata <file>`; never hand-author AOT XML |
| `BPUpgradeCodeToday` | `today()` | `DateTimeUtil::getSystemDate(DateTimeUtil::getUserPreferredTimeZone())`, assigned to a local first |
| `BPCheckNestedLoopInCode` | nested `while select` | one statement with `join` / `exists join` |
| "TTS level is not 0" | unbalanced transaction scope | pair every `ttsbegin`; move `try`/`catch` **outside** the block |
| `Exception::UpdateConflict` | two sessions updated one record | retry the tts block; `UpdateConflictNotRecovered` when retries run out |
| "record is not selected for update" | missing `forUpdate` | `select forUpdate optimisticlock …` inside the transaction |
| `Exception::CLRError` | a .NET call threw | catch `CLRError` specifically; read `CLRInterop::getLastException()` |
| "number sequence … not set up" | reference not registered for this company | check `loadModule()`, then Organization administration → Number sequences |
| "field does not exist" | invented or renamed field | `d365fo get table <Name>`; reference through `fieldNum()` |
| "unknown type", "could not be found" | invented identifier | `d365fo validate references --file <f>` — it proves every symbol before the compiler sees it |
| "label … does not exist" | missing label id | `d365fo search label "<text>"`, then `d365fo label create` |
| `';' expected` and friends | syntax | check the reported line — usually a CDATA method body edit |

Each row is a rule in the scored matcher, and each carries the knowledge topic
that explains the underlying model: `d365fo explain-error` returns the topic id
alongside the hint, so the next step is always
`d365fo knowledge get <topic>`.

## Prevention beats triage

Most of the table above is reachable **before** a build, in milliseconds:

```sh
d365fo validate references --file MyClass.xpp --output json   # invented symbols
d365fo validate xpp        --file MyClass.xpp --output json   # BP rules, offline
d365fo validate metadata   MyMenu.xml         --output json   # will the provider read it?
```

Run those in the same turn you write the code, fix everything they report, and
re-run. A build is a slow, Windows-only way to learn something three offline
commands already knew.

## Hard rules

- **Never guess at a message.** Feed it to `d365fo explain-error`; if nothing
  matches, say so and investigate rather than inventing a plausible cause.
- Fix every error from one log in a single pass, then re-build once. Fixing one
  and re-building is the slowest possible loop.
- A "record not found" is usually not an error: in X++ a `select` always succeeds
  and leaves an empty buffer. Test `if (myTable.RecId)`, and decide whether the
  absence is a genuine failure or a normal optional lookup.
- Never run `d365fo build`, `sync`, `bp check` or `test run` unprompted — they are
  slow and Windows-only. Scaffold, validate, then tell the user what to run.
