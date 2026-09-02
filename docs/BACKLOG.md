# Backlog

What is known to be missing, written down so it is a decision rather than a surprise. Anything
here is either measured by a gate in this repository or was found by running the tool against a
live installation.

Last reviewed: 2026-09-02.

---

## Ten AOT families with no generator

`d365fo eval coverage` scores every family and every `generate` subcommand on **K ∧ E ∧ T** —
taught by the knowledge corpus, proven by an eval case, built by a command. **64 of 74** leaves
are complete. The ten that are not all fail on the same leg:

| Family | Has | Missing |
|---|---|---|
| `AxConfigurationKey` | — | A `generate` subcommand. Small, well-understood XML. |
| `AxWorkflowCategory` | — | Same shape — a name and a module. |
| `AxMenu` | knowledge | A generator. `generate menu-item` builds the items; nothing builds the menu that holds them. |
| `AxTile` | knowledge | A generator. |
| `AxFormPart` | — | A generator. |
| `AxResource` | — | A generator, and a decision about how the payload file is supplied. |
| `AxLabelFile` | knowledge | A `generate` route. `labels create` writes label files today; the family is reported untooled because no `generate` subcommand claims it. |
| `AxMapExtension` | knowledge | Nothing to build it with, and nothing to build: `generate extension` has no Map kind, and no standard model ships a file in the folder. Extending the mapped tables is the real answer. |
| `AxAggregateDataEntity` | — | A generator. Analytical entities are a larger design than the rest of this list. |
| `AxCompositeDataEntityView` | — | A generator, and a composition model for the entities it wraps. |

This is surface work — wave 04's kind, not corpus work — which is why wave 05 closed the corpus
side and left these named. Six of the ten are a day's work; `AxResource`,
`AxAggregateDataEntity` and `AxCompositeDataEntityView` are not.

---

## L4: no runtime dimension in the eval loop

The eval corpus scores four dimensions and can prove three: the artefact validates, its references
resolve, and it **compiles** (`eval verify-build`, 60 of 60 goldens against the real `xppc`).
Nothing runs it.

`oracle runtime` is the groundwork — it says whether `SysTestConsole` is configured and whether it
can tell a passing test from a failing one — but a case's golden is still never executed. Until it
is, "correct" in this repository means "compiles and matches a reviewed golden", which is not the
same as "does what it should at runtime". `EvalScoreCard.RuntimeFailures` exists and is always
zero for the honest reason: nothing has run.

---

## Known limits worth stating

**The index is a mirror.** Everything read out of it is as current as the last `index extract` /
`index refresh` / `index sync`. `get method` and the `analyze` modes read X++ from the
`SourcePath` the index remembers; an external edit that was not synced is answered confidently
with yesterday's file.

**The bridge needs Windows and the metadata assemblies.** `modify`, `generate --install-to` and
`find refs --xref` are the commands that cannot be offline, and they say so rather than degrading.

**`prepare change` resolves one object.** Where a name is a table *and* a form — `CustTable` is —
it picks one and reports which in `objectType`. Pass `--type` when you know.

**Warnings are not the bar.** `oracle sweep` reports zero errors and 41 473 warnings across a full
installation. The warnings are style rules that shipped code legitimately breaks; nobody should
read that number as 41 473 problems, and nothing gates on it.

---

## Not planned

| | Why |
|---|---|
| Entra ID / OAuth for the HTTP transport | An API key matches the deployment this is for — one shared team instance — and an app registration is real operational cost for a shared secret. See [MIGRATION_FROM_MCP.md](MIGRATION_FROM_MCP.md#http-transport--shared-deployment-azure-app-service). |
| A member-order lint rule | Written, then withdrawn: Microsoft's own shipped files deviate from contract order and the provider reads them back without loss. The reasoning is recorded on `ContractOrderCanonicalizer` so it is not rediscovered and re-added. |
| A generics rule in the C#-ism validator | ApplicationSuite ships `private List<str> …;`. A rule here would flag compiling platform code. |
| Session/SSE MCP transport | `POST /mcp` is one JSON-RPC request per call, which is what the clients this targets use. |

---

## Where the numbers come from

```sh
d365fo eval coverage             # the 74 leaves and their K/E/T status
d365fo eval verify-build         # do the goldens compile
d365fo oracle sweep              # errors and warnings on a whole installation
python scripts/measure-context-cost.py
```

Nothing in this document is an estimate. If a line here cannot be reproduced by one of those, it
does not belong.
