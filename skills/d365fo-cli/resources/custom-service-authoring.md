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
1. Service class  — decorated with [ServiceAttribute]
   - Parameters/return types use [DataContractAttribute] classes

2. AxService XML  — declares the service class + operation bindings

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
d365fo generate custom-service VendorLookupService \
  --operation Lookup:lookupVendor \
  --operation Create:createVendor \
  --install-to MyModel
```

This produces:
- `AxClass/VendorLookupService.xml` — the service class
- `AxService/VendorLookupService.xml` — service descriptor
- `AxServiceGroup/VendorLookupServiceGroup.xml` — service group

---

## Service class skeleton

```xpp
[ServiceAttribute]
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
  <Name>VendorLookupService</Name>
  <ClassName>VendorLookupService</ClassName>
  <Operations>
    <AxServiceOperation>
      <Name>lookup</Name>
      <MethodName>lookupVendor</MethodName>
    </AxServiceOperation>
  </Operations>
</AxService>
```

## AxServiceGroup XML structure

```xml
<AxServiceGroup>
  <Name>VendorLookupServiceGroup</Name>
  <Services>
    <AxServiceGroupService>
      <ServiceName>VendorLookupService</ServiceName>
    </AxServiceGroupService>
  </Services>
</AxServiceGroup>
```

---

## REST endpoint format

After deployment the service is available at:

```
POST https://<env>/api/services/<ServiceGroupName>/<ServiceName>/<OperationName>
Authorization: Bearer <AAD token>
Content-Type: application/json

{ "AccountNum": "US-001" }
```

Example for the scaffold above:
```
POST https://myenv.operations.dynamics.com/api/services/VendorLookupServiceGroup/VendorLookupService/lookup
```

---

## Authentication

Use Azure AD OAuth2:

- **Client credentials** (server-to-server): Register an app in Azure AD, grant it the D365FO "Dynamics 365 Finance" API permission, use client_id + client_secret.
- **Delegated** (user context): Interactive user sign-in flow; the service runs as the signed-in user.

---

## Hard rules

- **Request/response types must be `[DataContractAttribute]` classes.** Primitive types (`str`, `int`) are also accepted for simple services.
- **Public methods in a `[ServiceAttribute]`-decorated class are automatically exposed** and do not require `[SysEntryPointAttribute]`.
- **`[DataMemberAttribute]` on every parmXxx accessor** — the JSON serializer uses member names from this attribute.
- **Service group name determines the URL** — choose a stable, module-scoped name; renaming it breaks all callers.
- **Never include `ttsbegin/ttscommit` in service methods** unless you own the full transaction scope. If the service calls a framework method that manages its own transaction, wrap at a higher level.
- **Use EDTs for parameter types** (e.g. `AccountNum`, `Name`) instead of `str` — provides type safety and label resolution.
