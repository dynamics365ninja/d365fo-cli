---
id: data-entity-scaffolding
description: Scaffold an AxDataEntityView (data entity) in D365 Finance & Operations for OData / DMF (Data Management Framework) integration. Use when the user asks to "create a data entity", "expose a table to OData", or "scaffold an entity for DMF".
applyTo:
  - "**/AxDataEntityView/**"
  - "**/*Entity.xml"
appliesWhen: User intent mentions data entity, AxDataEntityView, OData, DMF, public entity name, public collection name, or exposing tables to integrations.
---

# Authoring AxDataEntityView XML

> Data entities are the supported integration surface for D365FO — OData v4,
> Power Platform, DMF imports/exports, and inbound/outbound async services
> all consume them. `d365fo generate entity` emits a minimal but
> compile-clean `AxDataEntityView` XML.

## Pre-flight

```sh
d365fo search entity <Name>     --output json    # collision check
d365fo get table   <Table>      --output json    # field list to expose
d365fo search edt  <Edt>        --output json    # for any EDT mappings
```

## Scaffolding

```sh
# Minimal — exposes one table; OData names default to <Name>Entity / <Name>Entities
d365fo generate entity FmVehicleEntity \
    --table FmVehicle \
    --all-fields \
    --install-to FleetManagement

# Explicit OData names + per-field selection
d365fo generate entity FmVehicleEntity \
    --table FmVehicle \
    --field VIN --field Make --field Year \
    --public-entity-name FleetVehicle \
    --public-collection-name FleetVehicles \
    --install-to FleetManagement
```

The CLI returns `{path, bytes, backup, fieldCount, publicEntityName,
publicCollectionName}`. Never request the full XML back.

## OData naming conventions (D365FO)

| AOT property | Convention | Used for |
|---|---|---|
| `Name` | `<Domain><Concept>Entity` | Internal AOT identifier |
| `PublicEntityName` | `<Domain><Concept>` (singular) | OData entity type |
| `PublicCollectionName` | `<Domain><Concept>s` (plural) | OData collection (`/data/<Plural>`) |

If the singular ends in `s`, set the plural explicitly (`FleetStatus` →
`FleetStatuses`).

## Hard rules

- Never expose a table without confirming `IsPublic = Yes` on the entity (the
  scaffold emits this — preserve it).
- Never hardcode label captions for entity fields — they inherit from the
  underlying EDT or table field.
- Never duplicate an existing public entity / collection name across models —
  OData names are global. `d365fo search entity` first.
- Run `d365fo build` (and the OData metadata refresh) only on user request.
