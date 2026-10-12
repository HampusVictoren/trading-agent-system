# Var Jev hör hemma

**Skriven:** 2026-10-01. **Gäller commit:** `a06f3ae` (master efter PR #48). **Status:** plan — ingen kod, ingen tjänst, ingen kontraktsändring.

Det här dokumentet utreder var TypeSafe AI:s System One-modell *Jev* passar i trading-agent-systemet, och var den uttryckligen inte hör hemma. Det är skrivet i samma anda som `docs/arkitektur-roadmap.md`: läs *Sammanfattning* och *Föreslaget enda införande* först; resten är underlag. Det ersätter inte roadmapen — det preciserar ett enda kandidatsteg som kan byggas senare, när någon väljer att göra det.

Bakgrundsuppgifterna om Jev nedan kommer från leverantörens offentliga material (lansering 2026-09-15). Där repots kod och beslut strider mot leverantörspåståenden gäller repot. Kalibreringspåståenden behandlas som påståenden, inte som fakta.

---

## Sammanfattning / rekommendation

**En plats:** ersätt *portföljförvaltarens* omdöme — steget som idag producerar `TradeView` (`stance`, `conviction`, `horizon_days`) — med ett Jev-anrop. Analytikern och riskansvarig behålls på lokal LLM (Ollama/`qwen2.5:14b`) tills vidare: de levererar kategorier plus korta anteckningar som Jev inte kan skriva. Tesen och `key_risks` på tråden sammansätts från de tidigare stegens redan kapade `Notes`, inte från ny fritext.

**Avrådda platser:** screening, motorns `RiskEngine`, `PositionSizer`/`ConvictionTier`, deterministiska exits, minneshämtning, och all annan kod som flyttar eller stoppar pengar. Det är beslut 1 och 5 i roadmapen — Jev ändrar dem inte.

**Öppen fråga som måste avgöras innan något byggs:** hostad Jev skickar `FactSheet` (och stegresultat) till TypeSafe. Det är ett eget beslut om data får lämna maskinen, inte något den här planen rundar.

---

## Vad Jev är

Jev är TypeSafe AI:s första *System One*-modell. Den genererar ingen löpande text. Klienten skickar ett `state` och en karta av frågor med låsta svarstyper; svaret är typat och bär sannolikheter.

| Frågetyp | Vad den ger | Typiskt bruk här |
|---|---|---|
| `choice` | Ett av *dina* alternativ + `probabilities` + `confidence` | `stance`: BUY / SELL / HOLD |
| `score` | En nivå på en *skala du definierar* | `conviction` 0–1; `horizon_days` 1–30 |
| `noul` | Sannolikhet 0–1 att ett påstående stämmer | t.ex. "är en position försvarbar?" — används *inte* som riskspärr |

API: `POST https://api.typesafe.ai/v1/systemone`. Modellalias `jev-latest`, eller pin `jev-1.13.0` när trösklar väljs. Flera frågor utvärderas parallellt mot samma state. Hostad officiell modell — inga publicerade vikter, ingen temperatur/seed att styra. Pris enligt leverantör: **0,042 USD / miljon inputtokens**, output gratis, latens enligt leverantör **70–500 ms**. Rate limits och kontextfönster finns i leverantörens dokumentation och ska verifieras igen före implementation.

Leverantören beskriver sannolikheterna som kalibrerade. Det är ett påstående. I det här systemet är det enda ärliga måttet samma som för agenternas `conviction`: logga sannolikheten *framåt*, mät utfallet efteråt, jämför. Se etapp 4 och 8 i roadmapen — lookahead-varningen gäller även här.

Jev passar systemets gränsdragning ovanligt väl *i teorin*: beslut 1 säger att agenterna lämnar tes + conviction, aldrig belopp; Jev är byggd för typade beslut med sannolikhet, inte för att hitta på belopp. Det som *inte* passar är att kontraktets `TradeView` också kräver `thesis` och `key_risks` som text — och att analytikerns värde ligger i att *tolka* siffror till observationer, vilket Jev inte skriver.

---

## Nuvarande omdömeskartläggning

Kartan är läst ur koden på `master` (`a06f3ae`), inte ur den äldre bedömningstexten i roadmapen. Ägare = vilken komponent som faktiskt producerar signalen idag.

| Ställe | Ägare | Signal | Passar Jev? | Varför |
|---|---|---|---|---|
| **Screening** (`ScreeningService` / `domain.screening`) | Kod (Python) | Rankning + likviditetsfilter → kortlista | **Nej** | Beslut 5: siffror och urval är deterministiska. En LLM/Jev över universumet är just det roadmapen förbjuder. Screeningen *är* kontrollen agenterna mäts mot. |
| **FactSheet** (`build_fact_sheet`) | Kod | Pris, avkastning, volatilitet, P/E, … | **Nej** | Ren aritmetik. Ingen omdömesfråga. |
| **market_analyst** → `MarketRead` | LLM (AG2) | `trend` (UP/DOWN/SIDEWAYS), `valuation` (CHEAP/FAIR/EXPENSIVE/UNKNOWN), `observations` (≤3 korta strängar) | **Delvis, men inte här** | `choice` skulle kunna ge trend/valuation. `observations` kräver text Jev inte skriver. Att ersätta bara kategorierna sparar lite och tar bort den prosa som risk- och portföljsteg läser — dåligt byte som *enda* införande. |
| **risk_manager** → `RiskAssessment` | LLM (AG2) | `downside` (LOW/MEDIUM/HIGH), `veto` (bool, *rådgivande*), `risks` (≤3 strängar) | **Delvis, men inte här** | `choice`/`noul` passar kategorierna. `risks` är text. Viktigare: `veto` är medvetet *inte* en spärr (`steps.py`, prompten). Att låta Jev "vetoa" skulle lätt läsas som kontroll — och kontrollen ligger i motorn. |
| **portfolio_manager** → `TradeView` | LLM (AG2) | `stance`, `conviction` ∈ [0,1], `horizon_days` ∈ [1,30], plus `thesis` / `key_risks` | **Ja — rekommenderad plats** | Det är systemets enda ställe där riktning och övertygelse fattas. Jevs tre primitiv mappar rakt på stance/conviction/horisont. Tes-texten kan sammansättas från tidigare stegs `Notes` utan ny LLM. |
| **Pipeline-sammansättning** (`TradeSignal.from_view`) | Kod | Instrument, `reference_price`, `quote_as_of`, `run` | **Nej** | Kod äger fakta. Modellen får inte hitta på pris eller instrument (beslut 1, en nivå ner). |
| **Minne** (`AnalysisMemory.recall` / `remember`) | Kod + embeddings | Liknande `MarketRead`-prosa → tidigare utfall | **Nej** | Hämtning är vektorsökning. Att låta Jev *tolka* minnet vore ett andra omdöme ovanpå risksteget — utanför enda-införandet. |
| **ProcessProposalUseCase** | Motor (.NET) | Orkestrerar signal → size → risk → order | **Nej** | Orkestrering, inte omdöme. |
| **PositionSizer** + **ConvictionTier** | Motor | Conviction → 0 / 0,5 / 1,0 × budget eller innehav | **Nej** | Motorn äger storlek. Roadmapen varnar uttryckligen mot att behandla LLM-conviction som kalibrerad — samma varning gäller Jev tills utfall visar annat. Diskreta nivåer stannar. |
| **RiskEngine** | Motor | Godkänner/avvisar order (NAV-tak, kassa, quote-ålder, minsta innehavstid, …) | **Nej** | Sista grinden före pengar. Får inte bero på en hostad modell. |
| **ExitRules** / **ApplyExitsUseCase** | Motor | Stop-loss, tidsgräns — säljer utan att fråga agenter | **Nej** | Deterministiska exits finns just för att agenterna inte kan litas på att stänga. Jev ändrar inte det. |
| **MeasurementWorker** / utfallsberäkning | Motor | Avkastning vs index per horisont, hit rate | **Nej som beslutsfattare; ja som *konsument* av loggade sannolikheter** | Mätningen är ren. Däremot kan den *jämföra* Jevs loggade sannolikhet med träff — se införandet nedan. |
| **TradingWorker** (cykel, kortlista ∪ innehav) | Motor | Vad som analyseras när | **Nej** | Schemaläggning. |

Gränsen som måste hållas, ordagrant från roadmapens beslut:

1. **Motorn äger pengarna.** Agenterna (eller Jev i agentrollen) returnerar tes + conviction. Belopp, kvantitet och riskveto i skarp mening räknas i .NET.
2. **Koden räknar och väljer ut, LLM:en tolkar.** Screening och FactSheet förblir kod. Jev är kandidat där något *vägs ihop*, inte där något *räknas*.

---

## Avrådda platser (med skäl)

### Screening

Screeningen är den största tokenbesparingen och den kontroll agenterna mäts mot (`arkitektur-roadmap.md` etapp 5, `domain.screening`). Att sätta Jev där skulle (a) kosta ett anrop per kandidat eller per universum, (b) göra rankningen omätbar mot en deterministisk baslinje, och (c) bryta beslut 5. Rankningen förbättras när utfallen finns — med kod, inte med en beslutmodell.

### Riskveto i skarp mening

`RiskAssessment.veto` är rådgivande. `RiskEngine` är det som stoppar. En Jev-`noul` som "ingen position är försvarbar" får aldrig bli en genväg förbi eller ersättning för NAV-tak, kassakoll, quote-ålder eller minsta innehavstid. Om den loggas ska den loggas som *åsikt*, i `step_outputs`, parallellt med hur dagens `veto: bool` loggas — inte som ordergate.

### Sizing och ConvictionTier

`PositionSizer` översätter already-diskret conviction till andel av budget/innehav. Även om Jevs sannolikheter *skulle* visa sig kalibrerade i utfallen är rätt steg att justera trösklarna i `ConvictionTier` (etapp 8), inte att låta Jev returnera `quantity`. Kelly eller linjär scaling på sannolikhet är aktivt avrått i roadmapen och förblir avrått.

### Deterministiska exits

Stop-loss och tidsgräns körs varje cykel utan LLM just för att HOLD är det vanligaste agentsvaret och en position annars kan ligga kvar. Jev i exit-vägen skulle återinföra beroendet exits var byggda för att ta bort, plus nätverksberoende i den väg som ska fungera när agenttjänsten är nere.

### Motorn som helhet

All kod under `src/engine/Domain/Risk/` och use case-lagret som placerar ordrar är stängd för Jev. Integration, om den byggs, hör hemma i agenttjänsten (`src/agents`), bakom samma kontrakt motorn redan litar på: `TradeSignal` utan `amount_usd`.

### Ersätta hela teamet i ett steg

Analytikerns och riskansvarigas *textanteckningar* är input till beslutet och till minnet (`describe_reading` bäddar in `MarketRead`). Jev skriver dem inte. Att ta bort båda LLM-stegen och bara köra Jev på råa FactSheet-siffror är möjligt tekniskt, men det är ett annat experiment: det tar bort den tolkning beslut 5 säger är LLM:ens värde. Den här planen föreslår inte det.

---

## Föreslaget enda införande

**Namn:** Jev som portföljförvaltare (beslutssteget).

**Mål:** ett Jev-anrop producerar `stance`, `conviction` och `horizon_days`. Kontraktet mot motorn ändras inte. Ett nytt `team_id` (förslag: `default-jev`) införs *vid implementation*, så att `default` förblir mätbar baslinje — samma mönster som `default-memory` redan använder.

### Var i flödet

```
FactSheet (kod)
    → market_analyst → MarketRead          (kvar: lokal LLM)
    → risk_manager   → RiskAssessment      (kvar: lokal LLM)
    → jev_portfolio  → TradeView-fält       (NYTT: Jev)
    → TradeSignal.from_view(...)           (kvar: kod sätter pris/instrument/run)
```

Pipelinen behåller sin generiska loop. Skillnaden är att steget med `output_schema=TradeView` inte går via AG2/`StepRunner` mot Ollama, utan via en liten adapter som anropar System One och mappar svaret till `TradeView`. `TeamSpec` får alltså antingen en ny stegsort eller en runner-gren nycklad på roll — exakt mekanism är implementationsdetalj; *platsen* i flödet är efter `RiskAssessment`, som sista steg.

### State som skickas till Jev

Bara det portföljförvaltaren redan får se, plus det numeriska underlag risk/analytiker redan vägde in — **inte** råa verktygssvar, inte hela universumet, inte portföljens kassa (ingen agent producerar belopp; budget i prompten är medvetet borttagen).

Förslag till `state` (JSON-objekt, samma innehåll som dagens datablock plus FactSheet eftersom Jev inte har sett stegens prosa i en promptfil):

```json
{
  "instrument": { "type": "equity", "symbol": "..." },
  "existing_position": null,
  "FactSheet": { "...numeriska fält..." },
  "MarketRead": { "trend": "...", "valuation": "...", "observations": ["..."] },
  "RiskAssessment": { "downside": "...", "veto": false, "risks": ["..."] }
}
```

`existing_position` följer med när steget har `sees_position=True`, samma regel som idag: SELL utan innehav ska inte uppmuntras. Inga instruktioner i state — bara data. Prompt-hygienen (avgränsning, whitelist) gäller fortfarande; skillnaden är att Jev inte *kan* regenerera injicerad text som nästa steg läser, eftersom den inte skriver prosa vidare.

Pinna modellversion i config (`jev-1.13.0` eller den version som gäller vid bygge). Logga den *resolverade* versionssträngen från svaret, inte bara aliaset — samma skäl som `team_version`: trösklar kopplas till en fördelning.

### Tre frågor i samma anrop

Alla tre mot samma state, parallellt (ett round-trip, en state-kostnad).

| Nyckel | Typ | Definition | Mappar till |
|---|---|---|---|
| `stance` | `choice` | Alternativ `BUY`, `SELL`, `HOLD` med korta criteria på svenska eller engelska (välj ett språk och håll det; prompterna i repot är svenska — håll criteria konsistenta med det agenterna skrivit). SELL: bara meningsfullt när `existing_position` finns. | `TradeView.stance` |
| `conviction` | `score` | Skala **0–1**, instruktion: hur säker är riktningen, *inte* hur stor affären ska vara. | `TradeView.conviction` |
| `horizon_days` | `score` | Skala **1–30** (samma tak som `MAX_HORIZON_DAYS`). | `TradeView.horizon_days` (avrunda till heltal, klampa till [1, 30]) |

Medvetet **inte** en fjärde `noul`-fråga som riskveto: det skulle dupera `RiskAssessment.veto` och fresta till att läsa den som grind.

### Mapping till befintligt kontrakt

`TradeView` kräver också `thesis` och `key_risks`. Jev ger dem inte. Vid införandet:

- **`key_risks`:** ta `RiskAssessment.risks` i ordning, kapade som idag (`MAX_RISK_LENGTH` / `MAX_RISKS`). Om listan är tom — skriv en enda neutral risk som säger att risksteget inte lämnade någon, så kontraktet inte får noll risker på en non-HOLD (prompten kräver minst en; spegla det i koden).
- **`thesis`:** kort sammansatt sträng från `MarketRead` + valt stance, t.ex. trend/valuation plus hopfogade `observations`, med `MAX_THESIS_LENGTH`. Ingen ny LLM-tur. Det är medvetet sämre prosa än dagens PM — och det är poängen med experimentet: om stance/conviction från Jev slår `default` i utfall spelar den sämre tesen mindre roll; om den inte gör det har vi inte betalat två LLM-stackar för att få reda på det.
- **`instrument` / `reference_price` / `quote_as_of` / `run`:** oförändrat via `TradeSignal.from_view`.

Inget `amount_usd`. Inget nytt fält på trådkontraktet i den här planen. Vill man senare *exponera* Jev-sannolikheter till motorn är det en kontraktsändring (beslut 3) och hör hemma i en egen PR när utfallen motiverar det.

### Vad som loggas i `step_outputs`

Som idag: ett `RecordedStep` per steg, `output` som `jsonb`. För Jev-steget ska outputen innehålla **både** det som blev `TradeView` **och** råsvaren som förklarar det — annars kan `MeasurementWorker`/analys inte jämföra sannolikhet med utfall.

Minimum att spara i stegets JSON (utöver eller inuti ett utökat schema som *inte* går över HTTP till motorn):

- resolverad `model`-version
- `answers.stance` (val + `probabilities` + `confidence`)
- `answers.conviction` (score)
- `answers.horizon_days` (score)
- `usage.input_tokens` / `output_tokens` (kostnadsspår)
- den `TradeView` som faktiskt gick vidare till `TradeSignal`

Journalen förblir append-only. Misslyckat Jev-anrop ska **inte** bli HOLD med HTTP 200 — samma regel som etapp 1: backend-fel → 502/503/504, så motorn kan skilja "modellen valde HOLD" från "TypeSafe är nere".

### Hur mätning jämför Jev-sannolikhet med conviction

Ingen ny worker. Befintlig kedja räcker, med en rapportfråga till:

1. `MeasurementWorker` mäter som idag (`signal_outcomes`: instrument vs index, hit per stance, fasta horisonter).
2. Agenttjänsten får kopian via `POST /v1/outcomes` som idag.
3. Jämförelsen görs i analys/SQL över `agent.step_outputs` ⨝ `agent.signal_outcomes` (via `correlation_id`), inte i hot path:

   - **Kalibreringskurva:** binka Jevs `conviction`-score (eller `probabilities[BUY]` när stance=BUY, spegelvänt för SELL) i samma band som `ConvictionTier` (≤0,4 / 0,4–0,7 / >0,7) och jämför hit rate per band mot (a) samma band för `default`-teamets LLM-conviction, (b) screeningen.
   - **Stance-överensstämmelse:** hur ofta Jevs `choice` sammanfaller med vad `default` skulle ha sagt på samma FactSheet kan *inte* besvaras utan parallellkörning; kör därför `default-jev` som eget `team_id` over tid, inte som tyst ersättning inuti `default`.
   - **Kostnad per träff:** tokens × pris mot lokal Ollama-väggtid — se nästa avsnitt.

Etapp 8:s kalibrering ("jämför conviction mot utfallen och justera sizing-kurvan") får alltså ett andra spår: *om* Jevs band är bättre kalibrerade än LLM:ens är det evidens för att flytta trösklar — fortfarande i kod, fortfarande i motorn.

### Implementationsordning (när någon bygger — inte nu)

1. Ny teamkonstant `default-jev` som speglar `default` men byter sista stegets runner.
2. Settings för API-nyckel, bas-URL, pinnad modell — fail fast vid startup, samma stil som övriga `TAS_*`.
3. Adapter + enhetstester mot inspelade System One-svar (inga live-anrop i CI).
4. Journalfält för råa answers.
5. Kör parallellt med `default` tills hit rate och kalibrering finns; byt workerns `TeamId` först därefter.

---

## Kostnad vs lokal Ollama / `qwen2.5:14b`

| | Lokal `qwen2.5:14b` (AG2/Ollama) | Hostad Jev |
|---|---|---|
| **Pengar** | El/hårdvara; 0 USD/token | 0,042 USD / M inputtokens; output gratis |
| **Latens** | Sekunder–tiotals sekunder per steg på typisk lokal hårdvara; tre steg i kedjan | 70–500 ms för *hela* frågeuppsättningen enligt leverantör |
| **Vad som betalas** | Varje steg (analytiker, risk, PM) | Bara beslutssteget i det här förslaget; state ≈ FactSheet + två små JSON-objekt |
| **Reproducerbarhet** | `temperature=0` + `seed` pinnar beslutet *i samma processläge*, inte över omstarter/batchning (se CLAUDE.md) | Ingen temperatur/seed; pinna modellversion; fördelningen kan ändå skifta mellan versioner |
| **Text** | Skriver `thesis` / observationer | Skriver ingen text |
| **Datagräns** | Stannar på maskinen | State lämnar maskinen |

Räkneexempel i storleksordning (inte ett löfte): om ett besluts-state ligger runt några tusen tokens kostar ett Jev-anrop bråkdelar av en cent. Att ersätta *ett* lokalt PM-steg tar bort den långsammaste länken i kedjan och den delen av GPU-lasten, till en kostnad som i praktiken är försumbar jämfört med att köra tre lokala 14B-steg per kandidat i en cykel. Att ersätta *alla tre* stegen skulle spara mer väggtid men kräver en annan plan (textbortfall).

Slutsats för kostnadsfrågan i uppdraget: **ja — om Jev bara tar PM-steget är det ekonomiskt attraktivt mot lokal AI**, förutsatt att data får lämna maskinen och att utfallen visar att stance/conviction inte blir sämre. Det är inte skäl att röra screening eller motorn.

---

## Öppen fråga: lokal vs hostad

Hostad officiell endpoint är det som finns dokumenterat. State i det föreslagna införandet innehåller:

- instrumentsymbol
- numerisk `FactSheet` (pris, avkastning, volatilitet, P/E, sektor …)
- `MarketRead` / `RiskAssessment` (kategorier + korta svenska anteckningar)
- eventuellt innehav (kvantitet, snittpris)

Det är marknads- och portföljdata som lämnar värden. Inga API-nycklar till mäklare ligger i state idag, men principen är densamma: **det är ett medvetet beslut att skicka analysunderlag till tredje part**, inte en detalj att dölja bakom "det är bara features".

Den här planen tar *inte* ställning. Den kräver att beslutet dokumenteras (ADR) innan implementation:

- **Tillåt hostad Jev** → kör enligt införandet, hemlighet i `TAS_`-settings, ingen nyckel i loggar.
- **Tillåt inte** → antingen vänta på eventuell lokal/runtime-distribution från leverantören, eller lägg ner spåret. Hitta inte på en halvmesyr som skickar hashar "för säkerhets skull" och ändå läcker strukturen.

Lokal Ollama för analytiker/risk kan kvarstå oavsett — förslaget är en hybrid, inte ett totalt molnbyte.

---

## Icke-mål för denna plan

- Ingen kod, ingen adapter, ingen dependency (`typesafe-sdk` eller annat), ingen Docker-tjänst.
- Ingen ändring av `contracts/`, handel, agenter, promptfiler, motor, sizing, exits eller screening.
- Ingen ersättning av `RiskEngine` / `PositionSizer` / `ExitRules`.
- Ingen Kelly-sizing och ingen linjär mapping sannolikhet → belopp.
- Ingen parallell "shadow Jev" inuti `default` som tyst skriver över conviction — nytt `team_id` eller inget.
- Ingen lösning på nyhets-/makro-steg (etapp 8); Jev där är en separat utredning den dag specialisterna finns.
- Ingen uppdatering av `docs/arkitektur-roadmap.md` i den här PR:n — det här dokumentet står på egna ben och pekar in.

När införandet faktiskt byggs hör en kort hänvisning hemma i roadmapens etapp 8 (kalibrering) och en ADR som fångar beslutet lokal/hostad. Det är framtida arbete.
