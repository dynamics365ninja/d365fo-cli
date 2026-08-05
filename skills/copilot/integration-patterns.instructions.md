---
description: Guidance for building or debugging integrations between D365 Finance & Operations and external systems. Invoke when the user asks about OData, custom services, DMF/Data Management Framework, business events, Power Automate connectors, Service Bus, or external system connectivity.
applyTo: '**/AxDataEntityView/**,**/AxService/**,**/AxServiceGroup/**,**/*Entity.xml,**/*Service.xml'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# D365FO Integration Patterns

> D365FO offers four first-class integration mechanisms. Choose based on
> direction (inbound vs. outbound), latency (synchronous vs. async), and
> volume (single record vs. bulk). Ground every decision in the real
> metadata from the CLI.

## Pattern overview

| Pattern | Direction | Latency | Volume | Entry point |
|---------|-----------|---------|--------|-------------|
| **OData REST API** | In + Out | Synchronous, real-time | Record-level | `data/<PublicCollectionName>` endpoint |
| **Custom Services** | In | Synchronous | Operation-level | SOAP + JSON REST endpoint |
| **Data Management Framework (DMF)** | In + Out | Async batch | Bulk | Import/export jobs, staging tables |
| **Business Events** | Out | Near-real-time | Event-driven | Service Bus, Event Grid, Power Automate, Logic Apps |

---

## 1. OData REST API

**Purpose:** real-time synchronous CRUD from external systems — Power Platform, Logic Apps, third-party ERPs.

**Endpoint:** `https://{env}.operations.dynamics.com/data/{PublicCollectionName}`

**Requirements for a working OData entity:**

- `IsPublic = Yes` on the `AxDataEntityView`
- A unique `PublicEntityName` (the OData entity type) and `PublicCollectionName` (the collection URL segment)
- At least one key field with `AlternateKey = Yes` on a unique index
- All mandatory fields mapped in the entity

**CLI workflow:**

```sh
# 1. Find the entity
d365fo search entity <Name> --output json

# 2. Inspect fields, OData names, key configuration
d365fo get entity <Name> --output json

# 3. Check that key fields have AlternateKey index
d365fo get table <SourceTable> --output json | jq '.data.indexes[] | select(.alternateKey == true)'

# 4. Scaffold a new entity if needed
d365fo generate entity <Name> --table <T> \
  --all-fields \
  --public-entity <Singular> --public-collection <Plural> \
  --out c:/AOT/MyModel/AxDataEntityView/<Name>.xml
```

**Common mistakes:**

- Duplicate `PublicEntityName` across models — OData names are global. Run `d365fo search entity <PublicEntityName>` first.
- No `AlternateKey = Yes` index — the OData `$key` segment will fail.
- Mandatory fields not mapped — `$metadata` will list them as required but writes will error.

---

## 2. Custom Services (SOAP / JSON REST)

**Purpose:** custom business logic exposed as a callable service — for B2B integrations, ISV connectors, and automation tools that need transactional semantics.

**Pattern:**

```
AxServiceGroup
  └── AxService (ServiceGroup reference)
        └── Service class (X++)
```

**REST endpoint:** `https://{env}.operations.dynamics.com/api/services/{ServiceGroupName}/{ServiceName}/{OperationName}`

**SOAP endpoint (legacy):** `https://{env}.operations.dynamics.com/soap/services/{ServiceGroupName}`

**Authentication:** Azure AD OAuth2 (client credentials or user delegation).

**CLI workflow:**

```sh
# 1. Check for existing services
d365fo search service <Name> --output json

# 2. Inspect operations on a known service
d365fo get service <Name> --output json

# 3. Scaffold service class + service XML + service group
d365fo generate custom-service <Name> \
  --class-name <ServiceClassName> --group-name <ServiceGroupName> \
  --operation "processCustomer:CustAccount" \
  --out       c:/AOT/MyModel/AxService/<Name>.xml \
  --out-class c:/AOT/MyModel/AxClass/<ServiceClassName>.xml \
  --out-group c:/AOT/MyModel/AxServiceGroup/<ServiceGroupName>.xml
```

**Hard rules:**

- Use `[DataContractAttribute]` + `[DataMemberAttribute]` on parameter/return contract classes — not `pack()`/`unpack()`.
- Service class must NOT hold state between calls (it is instantiated per request).

---

## 3. Data Management Framework (DMF)

**Purpose:** bulk import/export and migration — nightly feeds, data migrations, staging loads, and periodic reconciliation. Not for real-time use.

**Requirements for DMF-capable entity:**

- `DataManagementEnabled = Yes` on the `AxDataEntityView`
- A staging table (`DataManagementStagingTable` property set)
- Change tracking support (for incremental export)

**CLI workflow:**

```sh
# 1. Find the entity
d365fo search entity <Name> --output json

# 2. Check staging table presence (a non-empty value implies DMF is wired up)
d365fo get entity <Name> --output json | jq '.data.entity.stagingTable'

# 3. Scaffold a DMF-capable entity (staging table must be created separately)
d365fo generate entity <Name> --table <T> --all-fields \
  --data-management --staging-table <Name>Staging --out …
```

**Notes:**

- DMF jobs are configured in the **Data Management** workspace, not the AOT.
- Use `--batch` mode for large datasets; DMF handles parallel execution internally.
- Change tracking is configured per-entity in **Data Management > Configure data source**.

---

## 4. Business Events

**Purpose:** event-driven outbound notifications when something meaningful happens in D365FO — approved purchase orders, posted invoices, status changes. Subscribers can be Power Automate flows, Service Bus, Event Grid, Logic Apps, or HTTP endpoints.

**Pattern:**

```
BusinessEventsBase subclass  ← the event
  + [BusinessEvents(...)]    ← registers it in the catalog
  + BusinessEventsContract   ← the payload schema
```

**CLI workflow:**

```sh
# 1. Find existing events to reference or avoid duplication
d365fo search business-event <Name> --output json

# 2. Inspect a known event — see category + contract class
d365fo get business-event <Name> --output json

# 3. Scaffold a new business event
d365fo generate business-event <Name> \
  --contract-name <ContractClass> \
  --payload "custAccount:CustAccount" --payload "amount:AmountCur" \
  --category "CustomerEvents" --primary-table CustTable \
  --out          c:/AOT/MyModel/AxClass/<Name>.xml \
  --out-contract c:/AOT/MyModel/AxClass/<ContractClass>.xml
```

**After scaffolding:**

1. Activate in **System Administration > Business events catalog** — find the event, activate it per legal entity.
2. Configure the endpoint (Service Bus, Event Grid, HTTP, Power Automate) in the catalog.
3. Test by triggering the business process that fires the event.

**Hard rules:**

- Business events are detected from `AxClass` sources (no separate AOT folder). The `[BusinessEvents(...)]` attribute on the class declaration registers it.
- Contract class implements `BusinessEventsContract`; each payload field has a `parmXxx()` accessor.
- `buildContract()` on the event class populates the contract from the current record context.

---

## Choosing the right pattern

```
External system calls D365FO on demand → OData (simple CRUD) or Custom Service (complex logic)
D365FO notifies external system when something happens → Business Events
Bulk data transfer, migration, nightly feeds → DMF
Power Platform (Power Apps / Power Automate) → OData or Business Events
Legacy SOAP client → Custom Service
```

**Reference:** https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/integration-overview
