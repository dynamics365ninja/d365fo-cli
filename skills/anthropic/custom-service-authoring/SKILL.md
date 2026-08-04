---
name: custom-service-authoring
description: Build or extend a D365FO custom service (JSON/SOAP REST endpoint) using the AxService + AxServiceGroup + SysOperation or plain class pattern. Invoke when the user asks to "create a custom service", "expose an X++ method as a REST endpoint", "build a service class", or "register a service group".
applies_when: User intent mentions custom service, service class, service group, JSON endpoint, SOAP endpoint, REST API from X++, AxService, or AxServiceGroup.
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Custom Service Authoring in D365FO

> Custom services expose X++ methods as synchronous REST/SOAP endpoints.
> They are ideal for real-time inbound integrations (e.g. Logic Apps calling
> D365FO to create a record, or Power Automate looking up a balance).

**Reference:** https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-attribute-classes

---

## Pattern overview

A D365FO custom service requires three artifacts:

```
1. Service class  — a plain X++ class (no class-level service attribute
   exists in X++ — the AxService object below is what wires it up)
   - Parameters/return types use [DataContractAttribute] classes

2. AxService XML  — declares the service class + operation bindings
   (each AxServiceOperation maps an operation Name to a Method on the class)

3. AxServiceGroup XML  — registers the service into a named group
   (determines the REST URL path segment)
```

## Pre-flight

```sh
# 1. Check for existing services to avoid duplication
d365fo search service <namePart> --output json

# 2. Inspect an existing service for reference
d365fo get service <ExistingName> --output json

# 3. Report all integration surface in the model
d365fo report-integrations --model <ModelName> --output json
```

---

## Scaffolding

```sh
d365fo generate custom-service VendorLookup \
  --group-name VendorLookupServiceGroup \
  --operation lookupVendor:VendorLookupResponse \
  --operation createVendor:boolean \
  --contract-param VendorLookupRequest \
  --install-to MyModel
```

`--operation` takes `<name>:<returnType>` — the same `<name>` is used both as the
`AxServiceOperation` `Name` and as the generated X++ method name (there is no
separate operation-name-to-method-name mapping). `--contract-param <ContractClass>`
applies that single parameter type to every generated operation method.
`--class-name` defaults to `<NAME>Service` and `--group-name` defaults to `<NAME>Group`.

This produces:
- `AxService/VendorLookup.xml` — service descriptor (`Name` = the positional argument)
- `AxClass/VendorLookupService.xml` — the service class (`--class-name` defaulted)
- `AxServiceGroup/VendorLookupServiceGroup.xml` — service group

---

## Service class skeleton

```xpp
public class VendorLookupService
{
    public VendorLookupResponse lookupVendor(VendorLookupRequest _request)
    {
        var response = new VendorLookupResponse();
        // ... business logic ...
        return response;
    }
}
```

There is no class-level attribute that marks a class as a service — the
class is just a plain X++ class. The `AxService` XML object (below) is what
exposes it: it references the class by name and lists each callable method
as an `AxServiceOperation`.

## Request / Response contract classes

```xpp
[DataContractAttribute]
public class VendorLookupRequest
{
    private AccountNum accountNum;

    [DataMemberAttribute('AccountNum')]
    public AccountNum parmAccountNum(AccountNum _accountNum = accountNum)
    {
        accountNum = _accountNum;
        return accountNum;
    }
}

[DataContractAttribute]
public class VendorLookupResponse
{
    private Name vendorName;

    [DataMemberAttribute('VendorName')]
    public Name parmVendorName(Name _vendorName = vendorName)
    {
        vendorName = _vendorName;
        return vendorName;
    }
}
```

---

## AxService XML structure

```xml
<AxService>
  <Name>VendorLookup</Name>
  <Class>VendorLookupService</Class>
  <ServiceOperations>
    <AxServiceOperation>
      <Name>lookupVendor</Name>
      <Method>lookupVendor</Method>
    </AxServiceOperation>
  </ServiceOperations>
</AxService>
```

The operation's `Method` element names the X++ method on the class named by
`Class`; `Name` is the external operation name used in the REST URL and is
typically the same string as `Method`.

## AxServiceGroup XML structure

```xml
<AxServiceGroup>
  <Name>VendorLookupServiceGroup</Name>
  <Services>
    <AxServiceGroupService>
      <Name>VendorLookup</Name>
      <Service>VendorLookup</Service>
    </AxServiceGroupService>
  </Services>
</AxServiceGroup>
```

`Service` references the `AxService` object's `Name`; `Name` on the
`AxServiceGroupService` is conventionally the same value.

---

## REST endpoint format

After deployment the service is available at:

```
POST https://<env>/api/services/<ServiceGroupName>/<ServiceName>/<OperationName>
Authorization: Bearer <AAD token>
Content-Type: application/json

{ "AccountNum": "US-001" }
```

Example for the scaffold above (`<ServiceName>` is the `AxService` object's
`Name`, i.e. the positional argument passed to `d365fo generate custom-service`
— not the `AxClass` service-class name):
```
POST https://myenv.operations.dynamics.com/api/services/VendorLookupServiceGroup/VendorLookup/lookupVendor
```

---

## Authentication

Use Azure AD OAuth2:

- **Client credentials** (server-to-server): Register an app in Azure AD, grant it the D365FO "Dynamics 365 Finance" API permission, use client_id + client_secret.
- **Delegated** (user context): Interactive user sign-in flow; the service runs as the signed-in user.

---

## Hard rules

- **Request/response types must be `[DataContractAttribute]` classes.** Primitive types (`str`, `int`) are also accepted for simple services.
- **There is no `[ServiceAttribute]` class-level decorator in X++.** A service class is a plain class; exposure comes entirely from the `AxService` object listing its methods as `AxServiceOperation` entries. Exposed methods do not require `[SysEntryPointAttribute]`.
- **`[DataMemberAttribute]` on every parmXxx accessor** — the JSON serializer uses member names from this attribute.
- **Service group name determines the URL** — choose a stable, module-scoped name; renaming it breaks all callers.
- **Never include `ttsbegin/ttscommit` in service methods** unless you own the full transaction scope. If the service calls a framework method that manages its own transaction, wrap at a higher level.
- **Use EDTs for parameter types** (e.g. `AccountNum`, `Name`) instead of `str` — provides type safety and label resolution.
