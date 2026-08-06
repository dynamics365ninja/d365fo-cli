> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema (`<AxClass>`, `<AxTable>`, `<AxForm>`, `<Methods>`, `<SourceCode>`) is proprietary — LLMs have not been trained on it reliably. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Authoring object extensions (Table / Form / Edt / Enum)

> Object extensions are the **non-intrusive** way to add fields to standard
> tables, controls to standard forms, members to standard enums, and tighten
> standard EDTs. Unlike CoC class extensions they do not wrap method calls —
> they merge metadata at compile time.

## When to use which

| Standard object you want to change | Use |
|---|---|
| Add a field/index/relation to `CustTable` | `extension Table CustTable --suffix <Suffix>` |
| Add a control / data source / FastTab to a standard form | `extension Form CustTableListPage --suffix <Suffix>` |
| Tighten an EDT (e.g. lengthen a string, adjust label) | `extension Edt CustAccount --suffix <Suffix>` |
| Add new members to a base enum | `extension Enum NoYes --suffix <Suffix>` |
| Add behaviour to a class method | **NOT this** — use `coc-extension-authoring` instead |

## Pre-flight (ONE call)

```sh
d365fo prepare change <Target> --goal "<why>" --output json
```

Returns the resolved object type, existing extensions/CoC, the recommended
strategy, and a **grounding token** for `d365fo generate extension …
--grounding-token <token>`.

Fallback (prepare unavailable):

```sh
# 1) Confirm the target object exists
d365fo get {table|form|edt|enum} <Target> --output json

# 2) Discover existing extensions targeting it (avoid duplicate <Suffix>)
d365fo find extensions <Target> --output json
```

If `count > 0`, list the existing extensions to the user. The naming
convention is `<Target>.<Suffix>` (dot-separated; `Suffix` typically is the
model short-name or the feature name).

## Reuse before creating

`D365FO_CUSTOM_MODELS` constrains the model, not the feature suffix. If the
variable contains multiple models, resolve the active target model first from
the artifact named by the user, the model that already contains the related
extension, or the model currently being edited. If more than one custom model
could own the change, stop and ask. The extension suffix is separate from the
model name: extract `<ExistingSuffix>` from existing related extensions in the
active model, such as `<Target>.<ExistingSuffix>` or
`<Target>_<ExistingSuffix>_Extension`. If no suffix can be derived and the user
did not provide one, stop and ask for the suffix. Do not create a feature-named
extension such as `<Target>.<Feature>` or a class such as
`<Target>_<Feature>_Extension` merely because the request mentions a feature,
ticket, integration, customer name, or model name.

Before scaffolding a new extension, inspect the existing result from
`d365fo find extensions <Target> --output json`:

- If an extension already exists in the target model and suffix family, modify
  that existing extension.
- If multiple extensions exist, stop and ask which artifact should own the
  change unless the user has named one explicitly.
- Create a new extension only when no existing extension owns that target/model
  concern or when the user explicitly asks for isolation.

## Scaffolding

```sh
# Add fields to standard CustTable in the FleetManagement model
d365fo generate extension Table CustTable --suffix Fleet --install-to FleetManagement

# Form extension targeting CustTableListPage
d365fo generate extension Form CustTableListPage --suffix Fleet --install-to FleetManagement

# Tighten the CustAccount EDT
d365fo generate extension Edt CustAccount --suffix Fleet --install-to FleetManagement

# Add an enum member to NoYes
d365fo generate extension Enum NoYes --suffix Fleet --install-to FleetManagement
```

`generate extension` takes exactly two positional arguments (`<KIND> <TARGET>`);
the suffix is a named `--suffix <SUFFIX>` option, not a third positional
argument — passing it positionally fails with "Could not match '<value>' with
an argument." If `--suffix` is omitted it defaults to the `--install-to`
model name (or `Extension`).

The scaffold emits a minimal `<AxXxxExtension>` element with the
`<Name>Target.Suffix</Name>` shape Visual Studio expects. After scaffolding,
hand-edit the XML to add `<Fields>`, `<Controls>`, `<EnumValues>`, etc.
Re-run `d365fo index refresh --model <Model>` so subsequent
`d365fo get` calls reflect the changes.

## Scaffolding new EDTs and enums

When you need a standalone EDT or enum (not an extension of an existing one), use the generate commands directly:

```sh
# New string EDT — check for an existing one first
d365fo search edt <NamePart> --output json

d365fo generate edt CustCustomId \
  --base-type String --size 20 --label "@MyModel:CustCustomId" \
  --out c:/AOT/MyModel/AxEdt/CustCustomId.xml

# Derive from an existing EDT (inherits base type and format)
d365fo generate edt CustCustomAccount \
  --extends CustAccount \
  --label "@MyModel:CustCustomAccount" \
  --out c:/AOT/MyModel/AxEdt/CustCustomAccount.xml

# New extensible enum — check for existing first
d365fo search enum <NamePart> --output json

d365fo generate enum CustCustomStatus \
  --value "None:0:@SYS000" --value "Active:1:@SYS001" --value "Closed:2:@SYS002" \
  --out c:/AOT/MyModel/AxEnum/CustCustomStatus.xml
```

`--base-type` accepts `String`, `Integer`, `Real`, `Int64`, `Date`, `UtcDateTime`, `Enum`, `Guid`. Enums are `IsExtensible=Yes` by default — pass `--non-extensible` to opt out (`--no-extensible` is not a recognized flag and is silently ignored, leaving the enum extensible).

## XDS Security Policy scaffolding

When adding row-level security via Extensible Data Security (XDS):

```sh
# Check for existing policies on the same table first
d365fo search security-policy <ConstrainedTableName> --output json

d365fo generate security-policy CustCustomSecurityPolicy \
  --constrained-table CustCustomTable \
  --policy-query CustCustomPolicyQuery \
  --operation Select \
  --context-type RoleName --context-value CustCustomRole \
  --out c:/AOT/MyModel/AxSecurityPolicy/CustCustomSecurityPolicy.xml
```

`--operation` accepts only `Select` (default) or `All` — `Insert`/`Update`/`Delete` are rejected with "Unknown --operation '<value>'. Expected Select | All." `--operation All` emits `<Operation>AllOperations</Operation>` in the generated XML (the real `AxSecurityPolicy.Operation` property is the `SecurityPolicyApplicability` enum: `Select | Insert | Update | Delete | InsertUpdateDelete | AllOperations` — confirmed against `Microsoft.Dynamics.AX.Metadata.dll`; this CLI only exposes the two most common values). The policy query (`--policy-query`) is a separate `AxQuery` AOT object that must already exist or be scaffolded with `d365fo generate query`.

After scaffolding, verify with:
```sh
d365fo get security-policy CustCustomSecurityPolicy --output json
```

## Hard rules

- Never have two extensions with the same `<Target>.<Suffix>` in the same
  model — `d365fo find extensions` first.
- Never create a feature-specific extension when a model-level extension for
  the same target already exists and is the natural owner of the change.
- Never rewrite an existing extension XML file wholesale. Preserve unrelated
  nodes and only add the requested structural element(s).
- After changing extension XML, validate XML well-formedness, run
  `d365fo validate xpp <file> --code-type xml-any --output json` (file is a
  positional argument, not `--file`), run
  `d365fo index refresh --model <Model>`, and re-read the target metadata.
- Never use `extension` for class behaviour changes — that is CoC's job
  (`d365fo generate coc <Class>`).
- Never modify the standard object directly (over-layering) — extensions are
  the supported mechanism. Over-layering is reserved for ISVs with explicit
  contractual permission.
- Always pass labels (`@File:Key`) for added fields' captions — never
  hardcoded text (BP `BPErrorLabelIsText`).
- Never guess EDT base types — `d365fo get edt <Name>` first.
- Always check for existing security policies before adding a new one — `d365fo search security-policy` first.
- After scaffolding, run `d365fo build` only on user request.


## Rule canon — AOT XML safety

<!-- canon:aot-xml-safety -->
- Never rewrite an existing AOT XML file wholesale. Preserve unrelated
  `<DataSourceModifications>`, `<DataSourceReferences>`, `<DataSources>`,
  `<Controls>`, methods, extension properties, and pattern metadata.
- Validate every changed XML file: XML parser first, then
  `d365fo validate xpp --file <f> --code-type xml-any --output json`, then
  `d365fo index refresh --model <Model>`, then re-read the object with
  `d365fo get ... --output json`.
- For new forms based on an example, read the example and keep the same pattern
  contract unless the user asked otherwise. Required pattern controls/datasources
  are mandatory, not optional inspiration.
<!-- /canon -->
