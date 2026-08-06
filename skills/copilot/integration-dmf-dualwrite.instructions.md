---
description: Move data in and out of D365 Finance & Operations in bulk or in sync — the Data Management Framework (DMF/DIXF), dual-write to Dataverse, virtual entities, and the Power Platform surface. Invoke when the user asks about data import/export, staging tables, recurring integrations, data packages, dual-write, Dataverse sync, virtual entities, or Power Automate.
applyTo: '**/AxDataEntityView/**,**/AxTable/**Staging*.xml'
---
> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Bulk and synchronised integration

> Four mechanisms, three of which are built on the same data entities. Pick by
> latency and direction before you write anything:

| Need | Mechanism |
|---|---|
| Bulk load / export, nightly feeds, migrations | **DMF** |
| Near-real-time two-way sync with Dataverse | **Dual-write** |
| Dataverse reads F&O live, no copy | **Virtual entities** |
| Event-driven outbound to Power Automate / Service Bus | **Business events** (see `d365fo knowledge get business-events-authoring`) |

## 1. Data Management Framework (DMF / DIXF)

**Prerequisites on the entity:**

- `DataManagementEnabled = Yes` — without it the entity never appears in the Data
  management workspace.
- A staging table (`DataManagementStagingTable`). Setting `DataManagementEnabled`
  without an existing staging table **fails the build**.
- `EntityCategory` — `Parameter | Reference | Master | Document | Transaction`.
  This drives import ordering inside a data package, so a wrong category
  produces "parent not found" errors that look like data problems.

```sh
d365fo get entity CustCustomerV3 --output json | jq '.data.entity | {stagingTable, dataManagementEnabled, entityCategory}'

d365fo generate entity FmVehicleEntity --table FmVehicle --all-fields \
  --data-management --staging-table FmVehicleStaging \
  --public-entity FmVehicle --public-collection FmVehicles \
  --install-to FleetManagement
```

**Operational rules:**

- After deploying a new or changed entity, **refresh the entity list**:
  Data management → Framework parameters → Entity settings → Refresh entity list.
  Skipping this is the single most common "my entity is missing" cause.
- `validateWrite()` and the insert/update chain run **per record** during import —
  keep them cheap, or a 100k-row load becomes a 100k-round-trip load.
- Composite entities group a header entity with its line entities for
  hierarchical import.
- Recurring integrations expose a queue-based REST endpoint:
  `POST /api/connector/enqueue/{DataProject}` to push,
  `GET /api/connector/dequeue/{DataProject}` to pull.
- Staging rows carry `DMFTransferStatus` (`NotStarted` / `Completed` / `Error`) —
  that is where import troubleshooting starts.
- Configuration keys apply: elements under a disabled key are excluded from DMF
  entirely.

## 2. Dual-write

Near-real-time bidirectional sync between F&O and Dataverse, driven by table maps.

**Change tracking is a two-sided prerequisite, and the entity half alone is not
enough.** Both artifacts need it:

```xml
<!-- On the AxDataEntityView -->
<AllowRowVersionChangeTracking>Yes</AllowRowVersionChangeTracking>

<!-- And on every source AxTable the entity reads.
     It sits after the title/label block, immediately before <CacheLookup>. -->
<AllowRowVersionChangeTracking>Yes</AllowRowVersionChangeTracking>
```

Miss the table half and the entity syncs on the initial load, then silently stops
picking up changes. The element is `AllowRowVersionChangeTracking` on **both** —
`ChangeTrackingEnabled` is not a property of `AxDataEntityView` at all.

**Other rules:**

- The entity must be OData-enabled and public (`IsPublic=Yes`, plus
  `PublicEntityName` / `PublicCollectionName`).
- `DataManagementEnabled` is a **DMF** concern, not a dual-write prerequisite.
- Match on a stable business key, never `RecId`: give the entity an
  `AxDataEntityViewKey` over a real alternate key — a unique index with
  `AlternateKey=Yes`, backed by `ReplacementKey` on the table.
- Run the initial sync from the side holding the more complete data set.
- Keep transforms to field mapping and defaults. For anything with business
  logic, use business events plus Power Automate instead.
- Failed records land in an error queue and are retried; design writes to be
  idempotent.
- **Do not dual-write high-volume transaction tables.** Use async integration
  (business events → Service Bus) for those.
- `DataAreaId` matters: dual-write respects legal-entity context.

## 3. Virtual entities and Power Platform

- **Virtual entities** expose F&O data as Dataverse tables with **no copy** —
  queries route to F&O at runtime. Read-only by default. Enable in the Dataverse
  admin centre and configure under Power Platform integration in F&O.
- **OData**: any entity with `IsPublic=Yes` is served at
  `{env}/data/{PublicCollectionName}`. Throttled — use `$batch` for bulk CRUD.
- **Business events** appear automatically as Power Automate triggers ("When a
  Business Event occurs"); a class extending `BusinessEventsBase` needs no extra
  registration to show up.
- The **Finance and Operations connector** in Power Automate exposes entity CRUD,
  business events and batch-job execution.
- Authentication is Entra ID (Azure AD) app registrations for both OData and
  virtual entities.
- Security still applies: entity-level privileges gate OData. **Never publish an
  entity carrying sensitive fields without a privilege over it.**
- For canvas / model-driven apps that need live F&O data, use virtual entities —
  dual-write is for bidirectional *sync*, not for reads.

## 4. Reading uploaded Excel and CSV files

A cloud AOS is sandboxed: no Office, no arbitrary file-system access. Every
reader must be **stream-based**.

| AX 2012 | D365FO |
|---|---|
| `SysExcelApplication` / `SysExcelWorksheet` (COM) | `DocumentFormat.OpenXml.Packaging.SpreadsheetDocument` over a `System.IO.Stream` |
| `CommaIo('C:\\file.csv')` / `AsciiIo` | `CommaTextStreamIo::constructForRead(stream)` or `System.IO.StreamReader` |

- ⛔ **Never** `SysExcelApplication`, `SysExcelWorksheet` or
  `Microsoft.Office.Interop.Excel`. COM Office is not installed on a cloud AOS
  and throws at runtime.
- ⛔ **Never** a file-path `CommaIo` / `AsciiIo`. The AOS cannot see a client or
  server path.
- Get the stream from the upload: `FileUploadTemporaryStorageStrategy` gives a
  storage URL, `File::UseFileFromURL(url)` turns it into a stream. In a
  SysOperation, carry the URL as a contract member — never a path.
- CSV encoding is a real failure mode: read with the right
  `System.Text.Encoding` (UTF-8 vs 1252) or accented characters corrupt silently.
- Dispose everything: `System.IO` streams in `try`/`finally`, and
  `doc.Dispose()` on the spreadsheet package.
- For anything large, use DMF with a file entity. Hand-rolled readers are for
  ad-hoc and lightweight cases.

```xpp
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

/// <summary>
/// Reads the first worksheet of an uploaded workbook, row by row, from its stream.
/// </summary>
public void readExcel(System.IO.Stream _stream)
{
    SpreadsheetDocument doc = SpreadsheetDocument::Open(_stream, false);
    try
    {
        WorkbookPart  wbPart = doc.get_WorkbookPart();
        WorksheetPart wsPart = wbPart.get_WorksheetParts().get_Item(0);
        DocumentFormat.OpenXml.OpenXmlReader reader =
            DocumentFormat.OpenXml.OpenXmlReader::Create(wsPart);

        while (reader.Read())
        {
            if (reader.get_ElementType() == typeof(Row))
            {
                // read the cells of this row
            }
        }
    }
    finally
    {
        doc.Dispose();
    }
}
```

```xpp
/// <summary>
/// Reads a semicolon-separated upload from its stream — no file path anywhere.
/// </summary>
public void readCsv(System.IO.Stream _stream)
{
    CommaTextStreamIo io = CommaTextStreamIo::constructForRead(_stream);
    container         line;

    io.inFieldDelimiter(';');
    line = io.read();                       // header

    while (io.status() == IO_Status::Ok)
    {
        line = io.read();
        if (io.status() != IO_Status::Ok)
        {
            break;
        }
        // conPeek(line, 1) …
    }
}
```

## Hard rules

- Entity names are global: run `d365fo search entity <PublicEntityName>` before
  choosing one. A duplicate `PublicEntityName` breaks OData for both entities.
- Key fields need an `AlternateKey=Yes` unique index, or the OData `$key` segment
  and dual-write matching both fail.
- Never bypass the entity to write staging tables directly — the framework
  maintains status and error state alongside the row.
- Prefer set-based work inside entity methods; per-row loops are what turn a
  10-minute import into an overnight one.
