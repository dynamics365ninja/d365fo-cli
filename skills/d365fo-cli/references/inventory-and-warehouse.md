> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Inventory and warehouse management

> Inventory is the area where "just query the table" does the most damage.
> `InventSum` is a maintained aggregate, `InventDim` is deduplicated by hash, and
> `InventTrans` status transitions are what make financial and physical postings
> agree. Every one of those has an API that must be used instead.

## 1. The three core tables

| Table | Holds | Rule |
|---|---|---|
| `InventTrans` | one row per inventory lot movement | linked to its source document through `InventTransOrigin` |
| `InventDim` | a dimension combination (site, warehouse, location, batch, serial, …) | **always** `InventDim::findOrCreate()` — never insert |
| `InventSum` | aggregated on-hand per `ItemId` + `InventDimId` | system-maintained; **never update** |

```sh
d365fo get table InventTrans --output json
d365fo find relations InventDim --output json
```

## 2. Reading on-hand

Use the `InventOnHand` class, not a hand-rolled `InventSum` query — it applies
the dimension hierarchy and the reservation rules that a raw sum ignores.

```xpp
InventDim     inventDim;
InventDimParm inventDimParm;
InventOnHand  inventOnHand;
Qty           availPhysical;

inventDim.InventSiteId     = 'Site1';
inventDim.InventLocationId = 'WH1';
inventDim = InventDim::findOrCreate(inventDim);

// initFromInventDim flags exactly the dimensions that were filled in.
// (InventDimParm::activeDimFlag() takes an InventDimGroupSetup — wrong call here.)
inventDimParm.initFromInventDim(inventDim);

// newItemDim takes an ItemId, not an InventTable buffer.
inventOnHand  = InventOnHand::newItemDim('ItemId', inventDim, inventDimParm);
availPhysical = inventOnHand.availPhysical();
```

## 3. Movements, updates and reservations

- `InventMovement` is an abstract hierarchy: one subclass per source document
  type, holding that document's inventory business rules. Extend by CoC.
- `InventUpdate` subclasses drive status transitions —
  `InventUpd_Physical` for a packing slip, `InventUpd_Financial` for an invoice.
- Reservation goes through `InventUpd_Reservation`, which respects the reservation
  hierarchy (site → warehouse → location → batch → serial).
- Which dimensions are active is configuration, not code: check `InventDimSetup`
  and control form visibility with the `InventDimCtrl_Frm*` classes.
- Custom inventory dimensions are added by **model extension**, never by
  overlayering.

## 4. Warehouse management (WHS)

| Object | Role |
|---|---|
| `WHSWorkTable` | work header — one per work order (pick, put, count, replenishment) |
| `WHSWorkLine` | the individual pick/put actions with from/to locations |
| `WHSWaveTemplate` | the wave steps: allocate, create work, … |
| `WHSLocDirTable` | location directives — where to pick from, where to put to |

- **Never set `WHSWorkTable.WorkStatus` directly.** Status is owned by the
  `WHSWorkExecute` class hierarchy; a hand-set status leaves work and inventory
  disagreeing.
- Custom wave steps: extend `WHSWaveStepBase` and register the step in the wave
  template configuration.
- Mobile device flows (`WHSMobileAppFlow`) are extended, not modified.
- Zone and location typing lives in `WHSLocationProfile`.
- Wave processing is batch-capable — **always** run it in batch for real volumes.
- Custom post-processing: CoC on the `WHSPostEngine*` classes.

## Hard rules

- `InventDim::findOrCreate()` for every dimension combination. Inserting an
  `InventDim` row by hand creates a duplicate `InventDimId` that quietly splits
  on-hand.
- Never write `InventSum`. Never insert `InventTrans` outside an `InventMovement`
  /`InventUpdate` path.
- On-hand questions go through `InventOnHand`; "available physical" and "physical
  on-hand" are different numbers and the class is what knows the difference.
- Inventory work is high-volume: prefer set-based operations and batch execution,
  and never nest `while select` over `InventTrans`.
- Ground the class names — `d365fo get class InventOnHand --output json` — before
  calling a method; this hierarchy has many near-identical names.
- Mobile-device / scanner flows are their own discipline — the app is a
  stateless container protocol, not a form: `d365fo knowledge get
  warehouse-mobile-app`, and for scan resolution `d365fo knowledge get
  barcode-scanning`.
