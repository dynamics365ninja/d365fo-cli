> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Models, dependencies, coupling

> The CLI's `models` group reads the `Descriptor/*.xml` files indexed during
> `d365fo index extract` — no live AOT round-trip needed. All commands return
> JSON envelopes; pair with `jq` for narrow projections.

## Inspect models

```sh
# List indexed models with publisher / layer / customisation flag
d365fo models list --output json

# Direct + transitive dependencies
d365fo models deps FleetManagement --output json
```

`models list` output shape: `{count, items: [{modelId, name, publisher,
layer, isCustom}]}` (`publisher`/`layer` are frequently absent — omitted
when the Descriptor XML didn't populate them). `models deps` output shape:
`{model: {modelId, name, publisher, layer, isCustom}, dependsOn: string[],
dependedBy: string[]}`.

## Coupling metrics

```sh
d365fo models coupling --output json
d365fo models coupling --output json | jq '.data.cycles'
```

Output highlights:

| Metric | Meaning | Action threshold |
|---|---|---|
| `fanIn`         | How many models depend on **this** one | Stable foundations should have high fan-in. |
| `fanOut`        | How many models **this** one depends on | High fan-out → refactor candidate. |
| `instability`   | `fanOut / (fanIn + fanOut)` ∈ [0, 1] | Stable=0; volatile=1. Domain models near 0; edge integrations near 1. |
| `cycles[]`      | Strongly-connected component groups | Any non-empty entry is a hard error. |

## When to invoke

- Before introducing a new dependency: check `models list`/`models deps` for
  the target's layer — a model can only *depend on* strictly lower-or-equal
  layers (e.g. a `cus`-layer model must not depend on an `isv`- or
  `usr`-layer one).
- During architectural review: `cycles[]` must be empty; fan-out outliers
  flag candidates for splitting.
- When CoC / extensions don't take effect: `models deps` reveals if your
  model actually references the host model.

## Hard rules

- Layer ordering (lowest → highest): `sys → syp → gls → glp → dis → dip →
  cus → cup → var → vap → isv → isp → usr → usp` (each `Descriptor/*.xml`
  stores the numeric layer id, e.g. `<Layer>8</Layer>` = `var`). Each layer
  can only consume (depend on) *lower* layers — a `cus`-layer model cannot
  depend on an `isv`- or `usr`-layer model; the reverse is allowed. In
  practice most customer extension work happens in `usr` (the highest
  layer) precisely so it can reference everything below it, including
  installed ISV solutions.
- Never introduce a cycle — even a 2-node cycle blocks compilation.
- After modifying any `Descriptor/*.xml`, run `d365fo index refresh` so
  subsequent `models deps` / `models coupling` reflects reality.
