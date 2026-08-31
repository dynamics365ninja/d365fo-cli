---
name: warehouse-mobile-app
description: Customize the D365FO warehouse app / mobile device flows — the stateless screen protocol, the ProcessGuide framework (controller/step/page builder/data processor/navigation agent/action), the legacy WHSWorkExecuteDisplay hierarchy, and the work-execution rules a scanner step must respect. Invoke when the user mentions the warehouse app, mobile device menu items, RF/handheld flows, ProcessGuide, work execution, or scanner screens.
applies_when: User intent mentions the warehouse app, mobile device, handheld, RF gun, WMDP, ProcessGuide, WHSWorkExecuteDisplay, work execution, scanner screens, mobile device menu items, or warehouse app steps.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Warehouse app & mobile device flows

The warehouse app (and its predecessor, the warehouse mobile device portal) is
**not a form**. It is a stateless request/response protocol: the server builds a
screen as a container, the device posts the whole screen back, and the next
round trip may land on a different AOS. Menu items, menus, app steps and field
names are **configured data** — the only AOT surface you customize is the
flow-class hierarchy plus the extensible activity enum. Treating a step like a
form (member state, form events, direct table writes) is the failure mode this
topic exists to prevent.

## 1. Two frameworks — know which one owns the flow FIRST

D365FO builds the same mobile screens with **two frameworks**, and picking the
wrong one is a rewrite rather than a refactor:

- **ProcessGuide** (current) — controller → step → page builder → data
  processor → navigation agent → action, one responsibility per class, each an
  extension point. It carries **no WHS prefix** on purpose: production and
  inventory flows use it too.
- **Legacy `WHSWorkExecuteDisplay`** — one `displayForm()` per work-execute
  mode that processes input, runs the logic, increments the step counter and
  builds the next screen, all in one method.

Both are instantiated by SysExtension off the same work-execute-mode attribute,
so the only way to tell which owns a flow is **what the registered class derives
from** — read it with `d365fo get class <Name>` before touching anything. New
flows go to ProcessGuide where it exists.

## 2. The protocol rules (both frameworks)

- A screen is a **container of controls built server-side** — there is no
  FormRun, no datasource, no control event. Nothing from the form topics
  applies to a scanner step.
- Every round trip is **stateless** and may be served by a different AOS: carry
  state in the pass-through data the framework round-trips, **never** in class
  member variables, static fields or globals. Member state survives a
  single-box dev machine and silently loses the worker's progress under load
  balancing.
- Define the layout of the pass-through container in **one place**. Two methods
  that each hard-code `conPeek` indexes is the classic cause of "wrong value
  after the operator pressed back".
- Mobile device menu items and menus are **configured data, not AOT elements**.
  "Add a scanner menu item" is setup or a data package. The AOT half of a
  custom flow is the activity enum value and the flow class behind it.
- **What a scan DOES is configuration, not code**: the device menu item binds a
  MODE (work-driven vs indirect activity) and an ACTIVITY, and that pair
  selects the class that runs. "The scanner does nothing" is a setup question
  first — check the menu item's mode/activity before debugging X++.
- **Never write the work tables directly from a step.** Work status, work-line
  transactions and inventory move together through the work-execution
  hierarchy; a direct update leaves the work header, the inventory transactions
  and the license plate inconsistent, and the standard undo cannot roll it
  back. Undo is a first-class requirement: a step that bypasses the framework
  has no undo and no compensating transaction.
- The action a scan triggers must **complete inside the one server call** that
  received it (a device that walks out of range mid-conversation must not leave
  a half-posted document), must be **idempotent with the guard inside the
  transaction** (devices retry and operators re-scan), and ends in a document
  posted through the journal/posting framework rather than a raw insert.
- The work user is **not** the D365FO user: a device signs in as a work user
  with its own menu, while X++ runs under the linked system user. Resolve the
  operator through the work-user/session record — `curUserId()` gives you the
  service account.
- Prompt and field text comes from **labels** (and the field-name
  configuration): a raw literal fails `BPErrorLabelIsText` and cannot be
  translated for the shop floor.
- Performance is per screen: every step is a server round trip over a handheld
  network. Keep each query indexed and `firstOnly`; never scan a table in a
  step.
- A scanned string is not an item number — resolve it through the barcode setup
  first (`d365fo knowledge get barcode-scanning`).

## 3. ProcessGuide — the current model

Six responsibilities, one class each: **controller** owns the process, **step**
is one screen, **page builder** makes its controls, **data processor** handles
what the worker typed, **navigation agent** decides what comes next, **action**
is a button. If your change does not fit one of those, it is going into the
wrong class.

- **Registration is by attribute, not by editing a factory**: the controller
  carries the work-execute-mode attribute, the step a step-name attribute, the
  page builder and action their own name attributes. Forgetting the attribute
  is the signature failure here — the class compiles cleanly and the screen
  simply never appears.
- Name values are the class name through `classStr(...)`, never a string
  literal — a literal survives a rename and fails at run time on a device.
- A step **with** a screen names its page builder and answers `isComplete`. The
  base marks a screen complete on OK alone, so a screen that collects a value
  and does not override `isComplete` moves on **before your validation ran**.
- A step **without** a screen derives from the without-prompt base and does the
  work in `doExecute` — that is where a post, a journal or a work confirmation
  belongs.
- OK and the two Cancel actions are special (data-process-then-rebuild,
  reset-to-first-step, exit) — never reimplement them as custom actions.
- Default data processing delegates to the legacy control-data class, which
  already validates the standard fields (item, location, license plate). Write
  a data processor only for a field the platform does not know.
- Navigation is a route map of "after this step, that step". **Inserting a
  step means re-pointing BOTH edges of the route**; conditional branching needs
  its own navigation agent plus factory.
- Extending an existing flow, by intent: add a control → wrap `addDataControls`
  on the page builder; replace a screen → a new page builder plus a wrapper on
  `pageBuilderName`; insert a screen → wrap the route initializer and re-point
  both edges; change when a step finishes → wrap `isComplete`.
- An exception inside a step is the **framework's rollback**: the process
  returns to the previous step. Do not wrap a step body in try/catch to keep
  the worker in place — you will swallow the rollback.
- Naming: `<Area>ProcessGuide<Process>Controller` with matching Step /
  PageBuilder names — a convention the factories and readers depend on.

## 4. Legacy WHSWorkExecuteDisplay

One `displayForm()` per mode: it reads the posted screen, validates, runs the
action, increments the internal step counter and builds the next screen
container. Extend it with CoC on the specific mode class; keep the step-counter
discipline (every branch must set the next step explicitly), and prefer
converting the flow to ProcessGuide when the change is substantial.

## Hard rules

- Know which framework owns the flow before you touch it (what does the
  registered class derive from?).
- No member state — the pass-through container is the only state that survives.
- No direct writes to work tables; the hierarchy or nothing.
- Registration attribute present and spelled through `classStr` — a missing one
  compiles and never runs.
- The activity enum value goes on the extensible enum via an enum extension
  (no `<Value>` elements in the XML — see `object-extension-authoring`).
- Confirm exact factory/registration members against the installed version with
  `d365fo get class` — they differ across platform versions and are the single
  most hallucinated part of a warehouse-app customization.
