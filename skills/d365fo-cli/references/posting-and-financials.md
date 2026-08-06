> ⛔ **NEVER write X++ AOT XML files directly** via PowerShell, terminal file commands (`Set-Content`, `Out-File`, `New-Item`), editor write tools, or any raw text approach. The XML schema is proprietary. **ALWAYS use `d365fo generate …` commands** to produce correct AOT XML. If `d365fo` is unavailable in PATH, stop and ask the user to install it.

# Posting, dimensions, currency and pricing

> Every subject on this page has a framework API that must be used instead of
> touching the underlying tables. The tables are normalised in ways that make
> hand-written SQL wrong in silent, expensive ways — a mis-set `DefaultDimension`
> is a RecId pointing at the wrong dimension combination, not a bad string.

Ground every name below before you use it:

```sh
d365fo get class DimensionAttributeValueSetStorage --output json
d365fo get table InventTrans --output json
d365fo find usages LedgerVoucher --output json
```

## 1. Financial dimensions

Financial dimensions are multi-part keys stored as **RecId references**, never
as strings.

| Field | Type | Points at |
|---|---|---|
| `DefaultDimension` | `Int64` (EDT `DimensionDefault`) | `DimensionAttributeValueSet` — the dimension combination |
| `LedgerDimension` | `Int64` (EDT `LedgerDimensionAccount`) | `DimensionAttributeValueCombination` — main account **plus** dimensions |

**Hard rules:**

- **NEVER store dimension values in custom string fields.** Add a
  `DefaultDimension` field (EDT `DimensionDefault`) and let the framework own the value.
- Read a combination with `DimensionAttributeValueSetStorage::find(defaultDimension)`,
  then walk it with `elements()` / `getAttributeRecId(i)` / `getValueRecId(i)`.
- Write with the find → `addItem` → `save` pattern; `save()` returns the new RecId.
- Merge two sets with `DimensionAttributeValueSetStorage.mergeValues()`.
- Display string: `DimensionAttributeValueSetStorage.toString()` for a
  `DefaultDimension`; `DimensionAttributeValue::find(recId).getValue()` for a
  single `LedgerDimension` segment.
- Dimension names are **configurable per company** — resolve them with
  `DimensionAttribute::findByName()`, never hardcode `'CostCenter'` in logic.
- The Financial dimensions FastTab is a **form control**
  (`DimensionEntryControl` placed in the form design over the `DefaultDimension`
  field), not a controller you construct in `init()`. There is no
  `DimensionDefaultingController` and no `DimensionDefaultingService` class.
  `DimensionController` is the abstract base behind the entry controls — subclass
  it only for a custom account-type control.

```xpp
// Default one dimension on insert, without disturbing the others.
[ExtensionOf(tableStr(MyTable))]
final class MyTable_MyModel_Extension
{
    public void insert()
    {
        DimensionAttributeValueSetStorage dimStorage;
        DimensionAttribute                dimAttribute;
        DimensionAttributeValue           dimValue;

        dimStorage   = DimensionAttributeValueSetStorage::find(this.DefaultDimension);
        dimAttribute = DimensionAttribute::findByName('CostCenter');

        if (dimAttribute.RecId && !dimStorage.containsDimensionAttribute(dimAttribute.RecId))
        {
            dimValue = DimensionAttributeValue::findByDimensionAttributeAndValue(
                dimAttribute,
                this.MyCostCentreCode,
                false,      // _forUpdate
                true);      // _createIfNecessary

            dimStorage.addItem(dimValue);
            this.DefaultDimension = dimStorage.save();
        }

        next insert();
    }
}
```

## 2. Posting — vouchers and the accounting framework

**NEVER insert into `GeneralJournalEntry`, `GeneralJournalAccountEntry` or
`SubledgerJournalAccountEntry` directly.** The AX 2012 `LedgerTrans` table no
longer exists; there is no supported table-level shortcut.

- **`SubledgerJournalizer`** — the modern API for creating accounting entries.
  Prefer it in new modules.
- **`LedgerVoucher`** — legacy but still the path most standard modules
  (sales, purchase) take. Shape:
  `LedgerVoucher::newLedgerPost()` → `LedgerVoucherObject::newVoucher()` →
  `addVoucher()` → one `LedgerVoucherTransObject::newTransactionAmountDefault()`
  per line → `addTrans()` → `end()`.
- **Amounts are signed.** A debit is a positive `Money`, the balancing credit is
  the same amount negated. `end()` fails if the voucher does not balance.
- `LedgerPostingType` selects the posting profile the line hits.
- Post against a **`LedgerDimension`** (main account + dimensions), never a
  `DefaultDimension`.
- Currency conversion is the framework's job — hand the posting API a
  `CurrencyExchangeHelper`, never a hand-computed accounting amount.
- AxBC classes (`AxSalesTable`, `AxSalesLine`, …) are the business-component
  wrappers for posting; extend them with CoC, never by modification. Posting
  validation goes in a CoC wrapper of `validate()`.

```xpp
LedgerVoucher            ledgerVoucher;
LedgerVoucherObject      voucherObj;
LedgerVoucherTransObject debit, credit;
CurrencyExchangeHelper   exchangeHelper;

ttsbegin;

ledgerVoucher = LedgerVoucher::newLedgerPost(
    DetailSummary::Detail,
    SysModule::Ledger,
    '');                                  // voucher series: '' = module default

voucherObj = LedgerVoucherObject::newVoucher(
    voucher,                              // from the number sequence
    transDate,
    SysModule::Ledger,
    LedgerTransType::None);
ledgerVoucher.addVoucher(voucherObj);

exchangeHelper = CurrencyExchangeHelper::newExchangeDate(Ledger::current(), transDate);

debit = LedgerVoucherTransObject::newTransactionAmountDefault(
    voucherObj, LedgerPostingType::LedgerJournal, ledgerDimension,
    currencyCode, amount, exchangeHelper);
ledgerVoucher.addTrans(debit);

// Same amount, negated, so the voucher balances.
credit = LedgerVoucherTransObject::newTransactionAmountDefault(
    voucherObj, LedgerPostingType::LedgerJournal, offsetLedgerDimension,
    currencyCode, -amount, exchangeHelper);
ledgerVoucher.addTrans(credit);

ledgerVoucher.end();

ttscommit;
```

## 3. Currency and exchange rates

- Entry point is `CurrencyExchangeHelper::newExchangeDate(Ledger::current(), rateDate)`
  — a **ledger RecId plus a date**, not a currency pair. Reuse the helper for
  every amount on the same date.
- Convert with the helper's `calculateTransactionToAccounting()` (AmountCur →
  AmountMST), `calculateAccountingToTransaction()`, or
  `calculateTransactionToTransaction()`.
- Read the rate itself with `ExchangeRateHelper::getExchangeRate1_Static(ledger, currency, date)`
  / `getExchangeRate2_Static()`. There is no plain `getExchangeRate()`.
- The accounting currency of a legal entity is `CompanyInfo::find().CurrencyCode`.
- Rate types (Default, Budget, Cost accounting) come from Ledger setup — never
  pick one in code.
- **NEVER hardcode a rate or multiply by hand.**

```xpp
CurrencyExchangeHelper exchangeHelper;
AmountMST              amountMST;

exchangeHelper = CurrencyExchangeHelper::newExchangeDate(Ledger::current(), rateDate);
amountMST = exchangeHelper.calculateTransactionToAccounting(
    salesLine.CurrencyCode,
    salesLine.LineAmount,
    true);                  // _roundResult
```

## 4. Multi-company

- `SaveDataPerCompany=Yes` (the default) gives the table a `DataAreaId` and makes
  its rows company-specific. `No` makes them shared (`DirPartyTable` and friends).
- `changeCompany('DAT') { … }` switches company context for a block. It closes
  and re-opens the connection — **never put it inside a loop**.
- `crossCompany` queries several companies in one statement; it belongs on the
  **outer** buffer, and an optional container narrows the companies. Full grammar:
  `d365fo knowledge get xpp-database-queries`.
- **NEVER hardcode a `DataAreaId`** — use `curExt()` or
  `CompanyInfo::current().DataArea`.
- Inter-company transactions go through `InterCompanyTradingRelationship`, never
  hand-written cross-company writes.

```xpp
CustTable custTable;
container companies = ['DAT', 'USMF', 'DEMF'];

while select crossCompany : companies
    AccountNum, Name, DataAreaId from custTable
    where custTable.CustGroup == 'DOM'
{
    // per-company processing
}
```

## 5. Trade agreements and pricing

- Agreement types: sales price, purchase price, line discount, multiline
  discount, total discount.
- Evaluate with `PriceDisc` — `findPrice()` / `findDisc()` — never by querying
  `PriceDiscTable` yourself. The class walks the specificity ladder
  (customer+item → group+item → all+item → all+all), applies date effectivity,
  quantity breaks and dimension matching.
- Journal lines live in `PriceDiscAdmTrans`; posting validates and transfers them
  into the active `PriceDiscTable`.
- Custom pricing: CoC on `PriceDisc.findPriceAgreement()`.
- **NEVER hardcode a price** — always go through the pricing framework.

## Hard rules

- Every API above is grounded: run `d365fo get class <Name>` before calling a
  method you have not used in this environment.
- Wrap posting and dimension writes in `ttsbegin`/`ttscommit`; the posting API
  assumes it runs inside a transaction.
- Never `doInsert`/`doUpdate` on a ledger or dimension table — the framework
  keeps derived tables in step.
- Amount fields are `AmountCur` (transaction currency) or `AmountMST` (accounting
  currency); mixing them is the most common defect in posting code.
