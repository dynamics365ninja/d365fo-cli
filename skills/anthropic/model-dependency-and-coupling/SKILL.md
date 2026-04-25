---
name: model-dependency-and-coupling
description: Inspect indexed D365FO models, their declared dependencies, and architectural coupling metrics (fan-in, fan-out, instability, cycles). Use when the user asks "what models depend on X", "is there a cycle", "what's the layer of model Y", or "show coupling".
applies_when: User intent mentions model dependencies, layer (sys/syp/isv/iss/cus/cup/usr/usp), coupling, fan-in / fan-out / instability metrics, or "which model contains object X".
---
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

Output shape: `{name, publisher, layer, version, customizable, dependsOn[], dependedBy[]}`.

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

- Before introducing a new dependency: confirm the target model isn't
  customer-layer (`layer: cus*`) when you're an ISV.
- During architectural review: `cycles[]` must be empty; fan-out outliers
  flag candidates for splitting.
- When CoC / extensions don't take effect: `models deps` reveals if your
  model actually references the host model.

## Hard rules

- Never extend / depend on a `cus*` (customer-layer) model from an ISV model
  — D365FO disallows the upward dependency.
- Never introduce a cycle — even a 2-node cycle blocks compilation.
- Layer ordering (lowest → highest): `sys → syp → isv → iss → cus → cup → usr → usp`.
  Each layer can only consume *lower* layers.
- After modifying any `Descriptor/*.xml`, run `d365fo index refresh` so
  subsequent `models deps` / `models coupling` reflects reality.
