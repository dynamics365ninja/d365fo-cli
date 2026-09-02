---
description: Model the D365FO analytical and document-generation surfaces — aggregate measurements and the entity store, tiles/cues and KPIs, and Electronic Reporting (ER). Invoke when the user asks about a workspace tile, a KPI, an aggregate measurement, the entity store, Power BI content, or running/binding an ER format.
applyTo: '**/AxAggregateMeasurement/**,**/AxAggregateDimension/**,**/AxTile/**,**/AxKPI/**'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Analytics and Electronic Reporting

## 1. Tiles, cues and KPIs

A **tile** (`AxTile`) renders a count or a link on a workspace. A **KPI**
(`AxKPI`) scores a measure from an aggregate measurement against a goal. Both are
metadata-only — no X++ required.

| Element | Properties that matter |
|---|---|
| `AxTile` | `Label`, `Query` (the AOT query whose row count shows), `MenuItemName` (what opens on click), `Size` (Medium / Wide / ShortWide / Large — the `TileSize` enum has no Small) |
| `AxKPI` | `Measurement`, `Value` (Measure + MeasureGroup), `Goal`, `ScoringPattern` (LessIsBetter / MoreIsBetter / CloserIsBetter), `RefreshFrequency`, `ShowStatus` |

- A tile **without** a `Query` is a link tile; **with** one it becomes a cue and
  shows a live count.
- The tile query runs **per user, per refresh**. Keep it narrow and indexed, or
  the workspace becomes the slowest page in the application.
- Range the query on the current user or company where that is the intent — a
  count nobody can act on is worse than no tile.
- A tile element carries no layout: surface it through a tile/cue-group control on
  a workspace form.
- **A KPI requires a deployed aggregate measurement.** A KPI over a plain table is
  not possible.
- Labels are mandatory on both; they are user-facing surfaces.
- Extension story: add **new** elements plus a form extension that places them.
  A Microsoft tile's query cannot be redefined in place.
- Consider whether a saved view or an embedded Power BI report delivers the same
  insight without the entity-store dependency — for most custom work it does.

```sh
# A cue: Count tile over a query, opening the list page
d365fo generate tile FmOverdueRentalsTile --menu-item FmRental \
  --type Count --query FmOverdueRentalsQuery --label "@Fleet:OverdueRentals" \
  --install-to FleetManagement
```

`Type` is written only when it is not the default `Standard`; a KPI tile needs
`--kpi <AxKPI>`. The command checks the menu item against the index and reports
one it cannot find in `unknownMenuItems` rather than refusing — the index is a
mirror of what was extracted.

## 2. Aggregate measurements and the entity store

An aggregate measurement is the star schema D365FO ships to analytics: measure
groups (facts) plus dimension attributes (the keys you slice by), deployed to the
entity store (AxDW).

- `AxAggregateMeasurement` carries `Name`, `Usage` (`StagedEntityStore` for
  entity-store deployment) and its `MeasureGroups`.
- Each `AxMeasureGroup` binds to exactly **one** table — in practice a
  denormalised data entity or view — and lists `Measures` and `Attributes`
  (`AxDimensionAttribute` → `KeyFields` → `DimensionField`).
- **A measure's aggregation element is `<DefaultAggregate>`.** `AggregateFunction`
  does not exist and is dropped silently, leaving the measure on `Sum`. Legal
  values: `Sum`, `DistinctCount`, `AverageOfChildren`, `Max`, `Min`. A field with
  no aggregation is a dimension attribute, not a measure.
- Model the fact source as an entity or a view. The refresh reads it as-is, so a
  join done at query time is paid on every refresh.
- Shared dimensions live in `AxAggregateDimension` elements so several measure
  groups slice consistently.
- **Deployment is a runtime operation** (Data management → Entity store →
  Refresh), not part of the build. A measurement that compiles can still be
  undeployed and therefore invisible to Power BI.
- Refresh is incremental only when the source entity supports change tracking;
  without it every refresh is a full reload.
- Not for operational reporting — use an SSRS report or a query for row-level
  output (`d365fo knowledge get ssrs-report-authoring`).
- There is **no aggregate-measurement extension element**: adding a measure group
  to a Microsoft measurement means creating your own.

### Aggregate data entities

An **aggregate data entity** (`AxAggregateDataEntity`) is the read-only entity
that exposes a measurement's measures and dimension attributes to workspaces,
Power BI and OData. It is not a view over tables: its single
`AggregateViewDataSource` names the **measurement**, and each field is an
`AxAggregateDataEntityMappedField` bound to a measure or a dimension attribute
inside one measure group.

```sh
d365fo generate aggregate-entity FmRentalsByColor \
  --measurement FMAggregateMeasurements \
  --measure NoRentals:FMRentalCharges:NoRentals:BIRCount \
  --dimension VehicleColor:FMRentalCharges:FMVehicle:VehicleColor:FMColorName \
  --install-to FleetManagement
```

- The mapped field's members (`Measure`, `Dimension`, `Attribute`,
  `MeasureGroup`, `ExtendedDataType`) belong to the
  `Microsoft.Dynamics.AX.Metadata.V2` contract namespace and are written with
  its prefix, as every shipped file does. Unprefixed, the reader keeps the field
  and **drops every mapping on it**.
- The measurement and its groups are not in the symbol index, so the scaffold
  cannot prove them — the build does. Compile before relying on the entity.
- Shipped ones are `IsReadOnly = Yes` and carry the five automatic field groups
  (`AutoReport`, `AutoLookup`, `AutoIdentification`, `AutoSummary`, `AutoBrowse`).

## 3. Electronic Reporting (ER)

ER generates configurable business documents (invoices, SEPA files, VAT returns).
**The model, mapping and format are configured in the UI and are not AOT
elements** — do not try to author them in code.

**Running a format from X++:**

- `ERObjectsFactory::createFormatMappingRunByFormatMappingId(formatMappingId, fileName, showPromptDialog, showInfologMessage, forceRunDraft)`
  — five arguments, all required. It returns an **`ERIFormatMappingRun`**; note
  the `ERI…` prefix, there is no `IERFormatMappingRun`.
- Parameters go through the run's own definition parameters:
  `ERModelDefinitionInputParametersAction::addParameter(name, value)` then
  `applyTo(formatRun.getDatasourceDefinitionParameters())`. Unattended execution
  is `runUnattended(parameters)`.
- Many `ERI…` names (`ERIDataSource`, `ERIModelDefinitionParameters`,
  `ERIModelDefinitionParamsAction`) are **.NET types** from
  `Microsoft.Dynamics365.LocalizationFramework`, not AOT artifacts — searching the
  index will not find them. Only the ones backed by an `AxClass`
  (`ERIFormatMappingRun`, `ERIDataSourceProvider`) are verifiable.
- `ERIDataSourceProvider` exists but declares only `getDataSource()` and nothing
  in the AOT implements it — do not build on it.

**Exposing X++ data to a format:**

Write an **ordinary public class** with a static `construct()` and public members,
then bind it in the model mapping as a data source of type
"Dynamics 365 for Operations \ Class". There is no interface to implement and no
registration — ER reflects over the public members, so anything the format reads
must be public.

```xpp
/// <summary>
/// ER binding: the model mapping's "FmInvoiceProvider" data source, declared as
/// "Dynamics 365 for Operations \ Class", reflects over this class's public members.
/// </summary>
public class FmErInvoiceProvider
{
    private Num       documentId;
    private AmountMST totalAmount;

    public static FmErInvoiceProvider construct()
    {
        return new FmErInvoiceProvider();
    }

    public Num parmDocumentId(Num _documentId = documentId)
    {
        documentId = _documentId;
        return documentId;
    }

    /// <summary>
    /// Every string a format reads is a label, never a literal.
    /// </summary>
    public Description parmTotalsCaption()
    {
        return "@Fleet:ErInvoiceTotals";
    }
}
```

**Other ER rules:**

- ER **framework classes are extensible by CoC** (`ERParameters`,
  `ERInvoicingServiceParameters` and others already carry extensions). What cannot
  be edited in code is the *configuration*, not the framework.
- Configurations live in `ERSolutionTable` / `ERVendorTable`. **Never touch those
  tables directly** — import from LCS or the ER designer.
- Country-specific formats arrive through localization features; check
  `ERSolutionRepositoryTable`.
- The class's documentation should name the model-mapping data source that binds
  it, so the coupling is discoverable from the code side.

## Hard rules

- Ground the class names first — this area mixes AOT classes and .NET types
  freely: `d365fo get class ERIFormatMappingRun --output json`.
- `<DefaultAggregate>`, not `AggregateFunction`. The wrong element is dropped
  silently.
- A KPI without a deployed measurement resolves to nothing; deploy before you wire.
- Tile and KPI labels are label tokens.
- Never hand-author these XML shapes — generate, then prove with
  `d365fo validate metadata <file> --output json`.
