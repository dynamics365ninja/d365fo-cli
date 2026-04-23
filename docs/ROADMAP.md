# d365fo-cli — Roadmap & odložené body

> Živý dokument. Zachycuje navržené, odložené a nápadově otevřené body,
> kterých jsme se dotkli během vývoje nad rámec toho, co je už v kódu.
> Seřazeno zhruba podle ROI (nejvyšší nahoře). Checkbox u bodu = už hotové.

---

## Stav dnes (baseline)

- Schema index v **v5** (SQLite, auto-migrace přes `PRAGMA user_version`).
- CLI větve: `search`, `get`, `find`, `index`, `models`, `generate`, `test`, `bp`, `review`, `daemon`, `resolve`, `read`, `agent-prompt`, `build`, `sync`, `doctor`, `version`.
- Pokryté AOT typy: Table (+fields/indexes/methods/delete actions/relations), Class (+methods/attributes), Edt, Enum, MenuItem, Form, ObjectExtension, EventSubscriber, SecurityRole/Duty/Privilege (+link tabulky, SecurityMap), Query (+joins), View (+fields), DataEntity (+fields, OData), Report (+datasets), Service (+operations), ServiceGroup (+members), WorkflowType, Label (multijazyk), CoC, ModelDependencies.
- Parallel.ForEach v `ReadAll` (parsing per-file uvnitř modelu).
- `d365fo index extract --model <Name>` pro incremental.
- Testy: 25/25 zelené.

---

## 1. Vysoká priorita — přímý dopad na AI agenty

### 1.1 X++ reverse references (cross-object call graph)
**Co:** Při extraktu skenovat zdroj každé metody a ukládat volání / odkazy.
**Cílová tabulka:**
```sql
CREATE TABLE XppReferences (
    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
    FromKind     TEXT NOT NULL,   -- Class / Table / Form
    FromName     TEXT NOT NULL,
    FromMethod   TEXT,
    TargetKind   TEXT NOT NULL,   -- Class / Table / Edt / Enum / Method / Label
    TargetName   TEXT NOT NULL,
    TargetMember TEXT,
    LineNo       INTEGER
);
```
**Jak:** regex scan per-metodu uvnitř už parsovaného `<Source>` CDATA, bez plného X++ parseru:
- `ClassName::method` → static call
- `new ClassName(` → constructor
- `tableName.find(`, `TableName::find(` → table lookup
- identifiers matching indexed EDT/Enum names → type reference
- `@SYS12345` / `"@Prefix123"` → label reference
**CLI:** `d365fo find refs <Name> [--kind class|table|edt] [--member X]`.
**Dopad:** Největší skok v použitelnosti pro agenta (Copilot umí říct "kdo volá `CustTable::validateField`"). +5–10 % času extraktu.
**Riziko:** String literály / komentáře generují false-positive — filtrovat dopředu `StripComments` + `StripStringLiterals` na source string.

### 1.2 Label inline resolver (auto-nahrazení v `get` výstupech)
**Co:** Přidat globální flag `--resolve-labels [--lang cs]` k `get table|class|form|edt|…`, který v rendered JSONu / textu nahradí `@SYS12345` tokeny textem.
**Kde:** v `RenderHelpers` middleware vrstva — projdi objekt, regex `@[A-Za-z]+[0-9]+` → lookup cache → substitute.
**Stav:** `d365fo resolve label` hotové, inline substituci ještě ne.

### 1.3 Source read — rozšířit o lineno a body slicing
**Co:** `d365fo read class X --method Y --lines 10-40` nebo `--around <regex>`.
**Kde:** `XppSourceReader` už CDATA čte; přidat helper `ExtractLines(body, from, to)` a grep-like mód.

---

## 2. Refresh & observability

### 2.1 `d365fo index refresh` (mtime-based incremental)
**Co:** U každého modelu vypočíst nejvyšší `LastWriteTimeUtc` pod `Descriptor/*.xml` + `Ax*/*.xml`. Porovnat s `Models.LastExtractedUtc` (nový sloupec). Re-extractovat jen změněné.
**Schema bump v6:** přidat `Models.LastExtractedUtc TEXT`, `Models.SourceFingerprint TEXT` (max mtime as UTC ISO8601).
**CLI:** `d365fo index refresh [--force]`.

### 2.2 `d365fo index diff <revision>`
**Co:** Co se v AOT změnilo vs. git revision. Existující `review diff` je na souborové úrovni; chce se strukturní diff — "tři nová pole na CustTable, metoda `validate` u FooClass změnila signaturu". Vyžaduje dvoj-extract nebo snapshot indexu.

### 2.3 Extraction telemetry
**Co:** Per-model časy, chybové souhrny. Logovat do `_index_meta` tabulky nebo `.log` souboru vedle DB.

---

## 3. Runtime/živá data (mimo čistě offline index)

### 3.1 Live OData konektor
**CLI:** `d365fo live entity <Name> --tenant ... --env ...`  
Volá `/data/$metadata` + `/data/<Collection>?$top=1` proti běžící AOS.  
Autentikace přes `Azure.Identity.DefaultAzureCredential` (dev AAD) nebo `D365FO_CLIENT_ID/SECRET` env.  
**Další operace:** `live call <Service>.<Op> --json body.json`, `live batch …`.

### 3.2 Live metadata reconciliation
**Co:** Porovnat offline index (`DataEntities`) s live `$metadata`. Odhalí entity, co jsou v AOT, ale neaktivní v AOS; nebo chybí v jednom z prostředí (Tier-2 vs. Tier-1).

### 3.3 Health/DMF checks (Windows VM)
**CLI:** `d365fo health entities`, `d365fo dmf push <Project>.zip`. Navazuje na existující `build`/`sync`.

---

## 4. Další AOT typy (dokrytí "long tail")

### 4.1 Aggregate dimensions / KPIs / Perspectives
- `AxAggregateDimension`, `AxKpi`, `AxPerspective` — minoritní, ale odkazují na tabulky/pole.
- Navrhovaná schema: `AggregateDimensions(Name, Table, ModelId, SourcePath)`, `Kpis(Name, Unit, ...)`.

### 4.2 Tiles / Workspaces
- `AxTile`, `AxWorkspace` — dnes úplně neindexované; užitečné pro navigaci.

### 4.3 Reference groups / Maps / Composite EDT
- `AxReferenceGroup`, `AxMap`, `AxMapExtension` — občas se objeví v usage grafu.

### 4.4 Configuration keys / Licence codes
- `AxConfigurationKey`, `AxLicenseCode` — pro rozhodování "kam se hodí feature flag".
- Mapovat, které tabulky/pole/EDT jsou pod kterým config key (`ConfigurationKey` attribute v XML).

### 4.5 Features (Feature management)
- `AxFeature` metadata + provázání na typy, které feature zpřístupňuje.

---

## 5. Vyhledávání a ergonomie

### 5.1 Full-text search (SQLite FTS5)
**Co:** Virtuální tabulka `LabelFts(Value, Key, File, Language)` + případně nad `Source` code.
**Dopad:** `d365fo search label "customer invoice"` přejde z LIKE na rank-sorted FTS. Desítky ms místo stovek.

### 5.2 Fuzzy name resolution
**Co:** Jemně fuzzy `get` — když `d365fo get class CustTbl` neexistuje, navrhni nejbližší (`CustTable`, `CustTables`) Levenshteinem.
**Kde:** společný helper `ResolveName<T>(name, repo.SearchXxx)` s thresholdem.

### 5.3 Parametrická agregace
**CLI:** `d365fo stats` — per-model counts, top 10 největších tabulek (#polí), classes bez Best-Practice atributů, atd. Hezké pro review.

### 5.4 Multi-scope search
**CLI:** `d365fo search any <substring>` napříč všemi typy, vrátí `kind/name/model` list. Existuje `find usages`, ale ten je pro substring match; chce se scope-agnostic rychlý skok.

---

## 6. Výstup & integrace

### 6.1 Persistent daemon mode (JSON-RPC)
**Stav:** skeleton existuje (`D365FO.Cli.Commands.Daemon`).
**Doplnit:** request routing 1:1 jako CLI, warm SQLite pool, file-watcher triggering `index refresh`.
**Dopad:** bez daemonu platíme ~200 ms na volání jen za JIT + DB open.

### 6.2 MCP stdio server (už existuje `D365FO.Mcp`)
- Dotáhnout 1:1 paritu CLI → MCP tool surface.
- Přidat `tools/list` s krátkými popisy a ukázkovými JSON argumenty (tool definition metadata for LLM prompting).

### 6.3 Structured diff output
**Co:** `--output patch` módy pro `generate *` — aby byly výstupy aplikovatelné jako textový patch bez zásahu do workspace.

### 6.4 Session cache soubor
**Co:** `.d365fo-session.json` vedle `.d365fo/index.sqlite`; uchovává poslední query kontext (aktivní model, recent gets) pro CLI prompt hints.

---

## 7. Generate (scaffolding) — rozšíření

- `generate extension <table|form|edt|enum> <Target>` — nic moc proti současnému `generate coc`, ale s datasources a fields pre-filled.
- `generate entity <Table>` — vytvoř `AxDataEntityView` s fields mapping = všechny pole cílové tabulky, OData názvy podle konvence.
- `generate privilege <EntryPoint>` + `generate duty <Privilege[]>` + wire to role — "přidej tomuto menu-itemu Maintain na roli X".
- `generate event-handler <SourceKind> <SourceObject> <Event>` — skeleton třídy s atributy a správnou signaturou handleru.

---

## 8. Kvalita kódu & X++ Best Practices

### 8.1 Vlastní BP runner bez VM
**Co:** část Best-Practice kontrol jde dělat staticky nad indexem:
- "tabulka nemá cluster index" → `TableIndexes` obsah,
- "class bez `[ExtensionOf]` ale jméno `*_Extension`" → nomenklatura,
- "metoda s public API bez doc-commentu" → regex na source,
- "hard-coded literal string pro UI" → source scan bez `@Label`.
**CLI:** `d365fo lint [--category X,Y]` s JSON/SARIF výstupem (`--output sarif` → CI friendly).

### 8.2 Coupling metrics
Grafové metriky nad `ModelDependencies` + `XppReferences` (až budou) — odhalí cyklické závislosti.

---

## 9. Dokumentace & onboarding

- Aktualizovat `docs/USAGE.md` s novými `models`, `resolve`, `read`, `find extensions|handlers|refs`, `get` rozšířeními.
- `docs/ARCHITECTURE.md` — diagram vrstev (Packages → Extractor → ExtractBatch → Repo → CLI/MCP).
- `docs/TOKEN_ECONOMICS.md` — změřit velikost typických odpovědí (`get table CustTable` je ~X tokenů), dát tabulku.
- Příklady pro Copilot prompt: „Najdi všechna místa, kde se volá `CustTable::validateField` a potřebují extenzi o field `MyFlag`" → sekvence příkazů.

---

## 10. Testy

- End-to-end: zafixovat malý sample `AxRepo` v `tests/Samples/MiniAot/`, spustit `MetadataExtractor` + `MetadataRepository` a ověřit counts.
- Regresní: snapshot testy na JSON výstupy `get table`, `get class`, `models deps` — detekuje neúmyslné změny shape.
- Performance smoke: `MeasureExtract(ApplicationSuite)` cap v CI (běží jen s `D365FO_PACKAGES_PATH`).

---

## 11. Drobnosti / technický dluh

- `tests/D365FO.Cli.Tests` je prázdný — naplnit aspoň smoke testem Spectre command registrace.
- `StringSanitizer` guardrail se volá jen sporadicky — auditovat všechny `Render` volání.
- `Models.IsCustom` se aktualizuje přes `UPDATE Models SET …` v `ApplyExtract`, ale `UpsertModelInternal` posílá `IsCustom` = false při first-seen dependency — dořešit jednoznačný zdroj pravdy.
- Error taxonomy (`TABLE_NOT_FOUND`, `MODEL_NOT_FOUND`, …) by měla žít v enumu, ne v magic stringech.
- `SchemaSql.Value` je eager — u cold startu načtený z resx. OK, ale hodilo by se log `schema v{X} applied` do `index build`.

---

## 12. Security / guardrails

- MCP tool handlers nesmí vrátit neescapovaný HTML/Spectre markup. Ověřit.
- Label `Value` sanitizer (StringSanitizer) se aplikuje, ale neescapuje control-char sekvence (BOM, 0x00). Přidat.
- `read class` vrací raw X++ source — je-li tam připojené secret (není idiom, ale stává se), agent by ho mohl uniknout do kontextu. Volitelný `--redact-literals` flag.

---

## 13. Skills (dokumentace/promptová knihovna)

Aktuální `skills/` adresář drží pět skill prompt šablon (pro Copilot + anthropic). Nápady:
- `skill: metadata-search` — best-practice, kdy použít `search` vs. `find` vs. `get`.
- `skill: coc-vs-event-handler` — rozhodovací strom kdy dělat CoC a kdy SubscribesTo.
- `skill: data-entity-design` — kdy složená entita, kdy přes computed, kdy staging.

---

## Plán další vlny (navržené pořadí)

1. **X++ reverse references** (kapitola 1.1) — největší viditelná hodnota.
2. **Inline label resolver flag** (1.2) — malý, velký přínos v čitelnosti výstupu.
3. **`index refresh` mtime** (2.1) — umožní rychlý vývojový cyklus.
4. **FTS5 labels/source** (5.1) — vyřeší pomalost `LIKE '%…%'` na velkém korpusu.
5. **Daemon + MCP parity** (6.1/6.2) — unblock pro stálé agenty.
6. Zbytek ad-hoc podle potřeb review / CI.

---

_Udržujte tento dokument při uzavření tématu: hotové řádky přesuňte do
"Stav dnes (baseline)" na začátek, odložené nechte v odpovídající kapitole._
