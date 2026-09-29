# ИоТ систем за дијагностику возила заснован на ESP32, CAN магистрали и дистрибуираним сервисима

> Arhitektonska specifikacija završnog rada — **jedini izvor istine** za projekat.
> Autor: Aleksandar · Mentor: prof. dr Dragan Stojanović · Elektronski fakultet u Nišu
> Verzija: 2.0 (.NET 10 / C# 14) · Zamenjuje verziju 1.0 (Rust)
>
> Ovaj dokument spaja projektnu specifikaciju sa opštim arhitektonskim smernicama. Gde se njih dve razilaze, merodavna je sekcija 5
> (Usaglašenost sa opštim smernicama).

---

## Sadržaj

1. [Pregled i cilj sistema](#1-pregled-i-cilj-sistema)
2. [Istraživačko pitanje](#2-istraživačko-pitanje)
3. [Hardverski inventar i testbed](#3-hardverski-inventar-i-testbed)
4. [Arhitektura sistema — pregled slojeva](#4-arhitektura-sistema--pregled-slojeva)
5. [Usaglašenost sa opštim smernicama](#5-usaglašenost-sa-opštim-smernicama)
6. [Tehnološki stek](#6-tehnološki-stek)
7. [Sloj 1 — Ugrađeni uređaj (ESP32 gateway)](#7-sloj-1--ugrađeni-uređaj-esp32-gateway)
8. [Simulirani ECU (Arduino Uno + MCP2515)](#8-simulirani-ecu-arduino-uno--mcp2515)
9. [Sloj 2 — Edge server](#9-sloj-2--edge-server)
10. [Sloj 3 — Računarski oblak](#10-sloj-3--računarski-oblak)
11. [Domenski model i ograničeni konteksti](#11-domenski-model-i-ograničeni-konteksti)
12. [Projektni obrasci](#12-projektni-obrasci)
13. [Komunikacija, teme i formati poruka](#13-komunikacija-teme-i-formati-poruka)
14. [Skladištenje podataka](#14-skladištenje-podataka)
15. [Osmatranje sistema](#15-osmatranje-sistema)
16. [Bezbednost](#16-bezbednost)
17. [Scenariji raspodele obrade (jezgro istraživanja)](#17-scenariji-raspodele-obrade-jezgro-istraživanja)
18. [Metodologija evaluacije i metrike](#18-metodologija-evaluacije-i-metrike)
19. [Dobre prakse i inženjerska disciplina](#19-dobre-prakse-i-inženjerska-disciplina)
20. [Analiza složenosti sistema](#20-analiza-složenosti-sistema)
21. [Struktura repozitorijuma](#21-struktura-repozitorijuma)
22. [Plan realizacije po fazama](#22-plan-realizacije-po-fazama)
23. [Rizici i mere ublažavanja](#23-rizici-i-mere-ublažavanja)
24. [Rečnik pojmova](#24-rečnik-pojmova)

---

## 1. Pregled i cilj sistema

Sistem predstavlja platformu za dijagnostiku vozila zasnovanu na Internetu stvari. Ugrađeni uređaj se povezuje na CAN magistralu vozila, prikuplja dijagnostičke podatke primenom standardnih automobilskih protokola, i prosleđuje ih serverskom delu sistema radi skladištenja, analize i vizuelizacije.

Ključna karakteristika sistema je da se **obrada podataka može izvršavati na tri različita nivoa** — na ugrađenom uređaju (far edge), na edge serveru (near edge / gateway sloj), ili u računarskom oblaku — pri čemu je raspodela obrade **konfiguracija u vreme izvršavanja**, a ne posledica arhitekture.

### Osnovni tokovi

| Tok | Smer | Opis |
|---|---|---|
| Telemetrija | vozilo → oblak | Periodično očitavanje parametara (OBD-II Mode 01) |
| Dijagnostički događaji | vozilo → oblak | DTC kodovi, anomalije |
| Dijagnostičke komande | oblak → vozilo | UDS sesije, čitanje po identifikatoru, rutine |
| Upravljanje uređajima | oblak → uređaj | Konfiguracija, registracija, promena režima obrade |

### Nefunkcionalni zahtevi

- **Determinističko uzorkovanje** na ugrađenom uređaju sa ograničenim i merljivim jitterom.
- **Otpornost na prekide veze** — gubitak konekcije ne sme uzrokovati gubitak podataka.
- **Konfigurabilna raspodela obrade** bez rekompajliranja i ponovnog programiranja.
- **Osmatrivost** — svaki zahtev trasiran kroz sve slojeve.
- **Bezbednost operacija upisa** — nijedna operacija koja menja stanje ECU-a ne izvršava se nad realnim vozilom.

---

## 2. Istraživačko pitanje

> **Gde u kontinuumu ugrađeni uređaj → edge server → oblak treba izvršavati obradu dijagnostičkih podataka vozila, i kako ta odluka utiče na kašnjenje, iskorišćenje resursa, opterećenje mreže i pouzdanost sistema?**

Podpitanja:

1. Koliko obrade je izvodljivo na resursno ograničenom mikrokontroleru pre nego što determinizam uzorkovanja počne da se narušava?
2. Koliko se smanjuje opterećenje mreže pomeranjem dekodovanja ka ivici, i po koju cenu u procesorskom opterećenju i potrošnji?
3. Da li edge server predstavlja bolji kompromis od oblaka za obradu, a oblak se svodi na agregaciju i dugoročno čuvanje?
4. Kako izbor formata serijalizacije utiče na propusni opseg i kašnjenje?

Hipoteza: postoji tačka preloma u kojoj dodatna obrada na uređaju prestaje da se isplati, i ona zavisi od učestanosti uzorkovanja i kvaliteta mrežne veze.

---

## 3. Hardverski inventar i testbed

### Raspoloživa oprema

| Komponenta | Uloga |
|---|---|
| ESP32 | Dijagnostički gateway — far edge čvor |
| SN65HVD230 CAN transiver | Fizički sloj CAN za ESP32 (3.3 V logika) |
| Arduino Uno | Simulirani ECU |
| MCP2515 CAN modul (TJA1050) | CAN kontroler + transiver za Uno (5 V, SPI) |
| OBD-II muški konektor (16 pin) | Povezivanje na utičnicu vozila |
| OBD-II ženski konektor + 5-pin kabl | „Lažna OBD utičnica" na radnom stolu |
| ELM327 v2.1 | Referentni skener za verifikaciju |

### Nedostaje

| Komponenta | Zašto | Prioritet |
|---|---|---|
| DC-DC buck 12 V → 5 V (ulaz 8–36 V) | Napajanje sa pina 16 u vozilu | Visok (pre faze F2) |
| Inline osigurač 1–2 A | Zaštita +12 V linije | Visok |
| microSD modul | Store-and-forward baferovanje | Srednji |
| USB-CAN adapter (CANable) | Referentna validacija ISO-TP implementacije | Srednji |
| Upredena parica (UTP par) | CAN-H / CAN-L otporan na smetnje | Nizak |

### Topologija A — radni sto

```
[ESP32 + SN65HVD230] ──CANH/CANL──┬── [MCP2515 + Arduino Uno]
     terminacija 120 Ω ON         │       terminacija 120 Ω ON (J1)
                                  │
                        [OBD-II ženska utičnica]
                                  │
                            [ELM327 v2.1]
```

Zajednička masa obavezna. Ukupna terminacija 60 Ω.

### Topologija B — realno vozilo

```
[Vozilo, OBD-II utičnica] ──muški konektor──> [SN65HVD230] ──> [ESP32]
      pin 6  = CAN-H                            terminacija OFF
      pin 14 = CAN-L
      pin 16 = +12 V ──> osigurač ──> buck 12/5 V ──> ESP32
      pin 4/5 = masa
```

**Kritično:** terminacija na SN65HVD230 modulu mora biti isključena u vozilu — magistrala je već terminirana sa oba kraja.

### Pinout

**ESP32 → SN65HVD230:** `3V3→VCC`, `GND→GND`, `GPIO5→CTX(D)`, `GPIO4→CRX(R)`
**Arduino Uno → MCP2515:** `D10→CS`, `D11→SI`, `D12→SO`, `D13→SCK`, `D2→INT`, `5V→VCC`, `GND→GND`

Proveriti kristal na MCP2515 modulu (8 ili 16 MHz) i uskladiti konfiguraciju biblioteke.

---

## 4. Arhitektura sistema — pregled slojeva

```
┌─────────────────────────────────────────────────────────────────┐
│ SLOJ 0 — Vozilo / ECU                                           │
│   CAN magistrala 500 kbps · realno vozilo · simulirani ECU      │
└────────────────────────────┬────────────────────────────────────┘
                             │ CAN 2.0B okviri
┌────────────────────────────┴────────────────────────────────────┐
│ SLOJ 1 — FAR EDGE: ESP32 gateway                                │
│   TWAI → ISO-TP → OBD-II / UDS → obrada → objavljivanje         │
│   Konverzija protokola (CAN → MQTT) · režimi P0–P3              │
└────────────────────────────┬────────────────────────────────────┘
                             │ MQTT / TLS (WiFi)
┌────────────────────────────┴────────────────────────────────────┐
│ SLOJ 2 — NEAR EDGE: edge server (lokalni računar)               │
│   MQTT broker · dekodovanje · analiza · lokalno skladište       │
│   Funkcionalan i bez veze ka oblaku                             │
└────────────────────────────┬────────────────────────────────────┘
                             │ agregati (MassTransit / broker)
┌────────────────────────────┴────────────────────────────────────┐
│ SLOJ 3 — OBLAK: Kubernetes (k3s)                                │
│   Mikroservisi · dugoročno skladištenje · analitika · API       │
│   Osmatranje (LGTM) nad celim sistemom                          │
└─────────────────────────────────────────────────────────────────┘
```

### Raspodela odgovornosti

| Sloj | Prima | Radi | Šalje dalje |
|---|---|---|---|
| Far edge | CAN okvire | Reasemblacija, filtriranje, opciono dekodovanje i detekcija | Okviri / PDU / signali / događaji |
| Near edge | Poruke sa uređaja | Dekodovanje, korelacija, kratkoročna analiza | Agregate i događaje |
| Oblak | Agregate | Dugoročna analitika, vizuelizacija, orkestracija | Komande ka uređaju |

**Invarijanta arhitekture:** granica i protokol između slojeva su isti u svim režimima obrade. Menja se isključivo *koji sloj izvršava koju fazu*. Bez ove invarijante poređenje režima nije fer, a to je srž istraživanja.

---

## 5. Usaglašenost sa opštim smernicama

Ova sekcija mapira opšte arhitektonske principe na konkretne odluke projekta. Svaka odluka označena kao **odbijeno** ili **smanjeno** ima obavezan ADR.

### Prihvaćeno u punoj meri

| Princip | Realizacija u projektu |
|---|---|
| Edge-Fog-Cloud kontinuum | Tri sloja; raspodela obrade je predmet istraživanja (sekcija 17) |
| Design for Failure | Store-and-forward na dva nivoa, retry sa backoff-om, circuit breaker, idempotentni potrošači |
| Filtriranje i agregacija na ivici | Režimi P1–P3 |
| Thresholding i lokalni događaji | Režim P3 — detekcija anomalija na uređaju |
| Konverzija protokola na gateway-u | CAN / ISO-TP → MQTT; to je bukvalno uloga ESP32 gateway-a |
| MQTT za ingestiju uređaj → gateway | MQTTnet, QoS 1, TLS |
| Dekompozicija po poslovnim sposobnostima | Ograničeni konteksti (sekcija 11) |
| Saga obrazac | `DiagOrchestrator` kao orkestrirana saga (MassTransit) |
| API Gateway / BFF | YARP kao ulazna tačka, autentifikacija, kompozicija |
| Event-driven, asinhrone granice | Servisi komuniciraju događajima, ne direktnim pozivima |
| Event streaming backbone | MassTransit nad RabbitMQ (Kafka kao alternativa za merenje) |
| Kontejnerizacija (Docker) | OCI slike za sve servise |
| Orkestracija (Kubernetes) | k3s + Helm (prošireni deo, faza F8) |
| Osmatranje: health, logovi, trasiranje | OpenTelemetry .NET, Grafana Alloy, LGTM |
| Sidecar za cross-cutting concerns | Grafana Alloy kao sidecar kolektor |

### Prihvaćeno u smanjenom obimu

| Princip | Odluka | Obrazloženje |
|---|---|---|
| Mikroservisi | **Modularni monolit kao polazna tačka**, servisi se izdvajaju po potrebi | Prerano izdvajanje daje distribuirani monolit. Svako izdvajanje ima ADR sa razlogom (nezavisno skaliranje, izolacija otkaza, različit životni ciklus) |
| Database per Service | **Logički da, fizički delimično** — odvojene šeme, deljena instanca PostgreSQL-a | Izolacija podataka je poštovana (nema unakrsnog pristupa), ali pet baznih instanci na jednom edge računaru je operativni teret bez koristi |
| CQRS | **Laka varijanta** — razdvojene putanje pisanja i čitanja, bez odvojenih skladišta | Putanja upisa je visokoučestana telemetrija, putanja čitanja su agregatni upiti. Puni CQRS sa materijalizovanim pogledima nije opravdan |
| Speed + Batch sloj | **Speed = edge sloj, Batch = oblak** | Prirodno se poklapa sa arhitekturom; ne gradi se poseban Lambda stack sa dva paralelna cevovoda |
| gRPC | **Samo za sinhrone unutrašnje pozive gde su potrebni** | Većina komunikacije je asinhrona kroz broker. gRPC se koristi tamo gde postoji stvaran zahtev-odgovor odnos |
| Edge AI / TinyML | **Statistička detekcija u P3; ML model je prošireni deo** | Detekcija anomalija u P3 koristi klizeće prozore i pragove. ML model na uređaju ne blokira evaluaciju |

### Odbijeno (sa obrazloženjem)

| Princip | Odluka | Obrazloženje |
|---|---|---|
| Event Sourcing | **Odbijeno** | Telemetrija je prirodno append-only tok merenja, ne sekvenca domenskih promena stanja. Event sourcing bi dodao znatnu složenost bez odgovora na istraživačko pitanje |
| Service Mesh (Istio / Linkerd) | **Odbijeno** | Operativna složenost mesh-a nije opravdana na skali od pet servisa. mTLS, discovery i trasiranje pokriveni su kroz .NET Aspire i OpenTelemetry |
| Dapr | **Odbijeno** | Funkcionalno se preklapa sa MassTransit i Aspire; uvođenje bi značilo dve apstrakcije nad istim problemom |
| Serverless / FaaS | **Odbijeno** | Sistem obrađuje stalan tok telemetrije; model kratkotrajnih funkcija sa hladnim startom je suprotan zahtevu za niskim i stabilnim kašnjenjem |
| CoAP | **Odbijeno** | Jedan protokol ingestije znači jedno merenje. Uvođenje CoAP-a udvostručuje kombinacije eksperimenata bez doprinosa istraživačkom pitanju |
| GraphQL | **Odbijeno** | Kontrolnoj tabli (ADR-0005) je dovoljan REST sa nekoliko upita po stranici |
| XaaS apstrakcije (DaaS, MaaS, SaaS) | **Konceptualno, ne realizovano** | Obrađuju se u teorijskom poglavlju kao kontekst; realizacija ne donosi merljiv rezultat |
| LoRaWAN / BLE / 6LoWPAN | **Van obima** | Sistem koristi WiFi; alternativni transporti pominju se kao pravac razvoja |

**Pravilo koje proističe iz ove tabele:** svaka tehnologija u sistemu mora imati zapisan razlog postojanja u ADR-u. Ako razlog ne staje u tri rečenice, tehnologija se ne uvodi.

---

## 6. Tehnološki stek

### Izbor platforme

**.NET 10 (LTS), C# 14.** Obrazloženje za ADR-0001:

1. .NET 10 je LTS izdanje sa podrškom do novembra 2028; .NET 8 i .NET 9 izlaze iz podrške u novembru 2026, dakle pre odbrane rada.
2. Autor ima profesionalno iskustvo u .NET ekosistemu, što smanjuje rizik implementacije u ograničenom vremenskom okviru.
3. Ekosistem pokriva sve arhitektonske potrebe zrelim bibliotekama (MassTransit, MQTTnet, EF Core, OpenTelemetry).
4. Prvoklasna podrška za OpenTelemetry — presudno, jer je osmatranje merni instrument istraživanja.

### Stek

| Sloj | Tehnologija |
|---|---|
| Firmver (gateway) | C, ESP-IDF (FreeRTOS) |
| Firmver (simulirani ECU) | C++, Arduino framework, MCP2515 biblioteka |
| Backend runtime | .NET 10 (LTS), C# 14 |
| Web API | ASP.NET Core 10 Minimal APIs |
| Frontend (ADR-0005) | Angular 22, Angular Material, ECharts (ngx-echarts), Vitest; nginx u kontejneru |
| API Gateway | YARP |
| MQTT | MQTTnet |
| Interni bus, saga, outbox | MassTransit nad RabbitMQ (Kafka kao alternativa) |
| gRPC | Grpc.AspNetCore (samo gde je zahtev-odgovor) |
| ORM / pristup podacima | EF Core 10 (+ Npgsql), Dapper na vrućim putanjama čitanja |
| Relaciona baza | PostgreSQL |
| Vremenska baza | TimescaleDB |
| Objektno skladište | MinIO |
| Lokalna orkestracija | .NET Aspire |
| Orkestracija (prošireno) | k3s + Helm |
| Otpornost | `Microsoft.Extensions.Http.Resilience` (Polly) |
| Validacija | FluentValidation |
| Osmatranje | OpenTelemetry .NET SDK → Grafana Alloy → Loki / Tempo / Mimir / Grafana |
| Testiranje | xUnit, FluentAssertions, Testcontainers, NetArchTest |

Napomena o alatima: .NET 10 uvodi novi `.slnx` format rešenja koji zamenjuje stari `.sln`. Projekat koristi `.slnx`.

### Native AOT kao mogućnost merenja

.NET 10 donosi poboljšanja u Native AOT kompilaciji. Ovo otvara dodatnu, jeftinu dimenziju evaluacije: poređenje JIT i Native AOT verzije edge servisa po veličini kontejnerske slike, vremenu hladnog starta i potrošnji memorije. Relevantno je jer edge server po definiciji ima manje resursa od oblaka. Opciono, spada u prošireni deo.

---

## 7. Sloj 1 — Ugrađeni uređaj (ESP32 gateway)

### Slojevita arhitektura firmvera

```
┌──────────────────────────────────────────────┐
│ Application  — režim rada, raspored zadataka │
├──────────────────────────────────────────────┤
│ Diagnostics  — OBD-II servisi · UDS klijent  │
├──────────────────────────────────────────────┤
│ Transport    — ISO-TP (ISO 15765-2)          │
├──────────────────────────────────────────────┤
│ Link         — TWAI drajver, filtri, prihvat │
├──────────────────────────────────────────────┤
│ HAL          — GPIO, tajmeri, WiFi, SPI, SD  │
└──────────────────────────────────────────────┘
```

Svaki sloj zavisi isključivo od sloja neposredno ispod. ISO-TP i dijagnostički slojevi pišu se bez direktnih poziva ka hardveru — komuniciraju preko apstrakcije reda poruka — pa se mogu kompajlirati i testirati na razvojnom računaru bez ESP32 hardvera.

### Zadaci (FreeRTOS)

| Zadatak | Prioritet | Odgovornost |
|---|---|---|
| `can_rx_task` | najviši | Čitanje okvira iz TWAI reda u prsten bafer |
| `isotp_task` | visok | Segmentacija/reasemblacija, flow control tajmeri |
| `diag_task` | srednji | Raspoređivanje PID upita, obrada odgovora |
| `process_task` | srednji | Dekodovanje / agregacija (zavisno od režima) |
| `publish_task` | nizak | Serijalizacija i MQTT objavljivanje |
| `buffer_task` | nizak | Store-and-forward na SD karticu i reprodukcija |

Komunikacija između zadataka isključivo preko FreeRTOS redova, bez deljenog promenljivog stanja. Time se eliminišu mutex-i na vrućoj putanji i vremensko ponašanje ostaje predvidivo.

### Vremenska ograničenja

- Flow Control okvir mora biti poslat u roku od nekoliko desetina milisekundi od prijema First Frame okvira, inače ECU prekida prenos.
- Uzorkovanje se pokreće tajmerom, ne odlaganjem u petlji — jitter je merena veličina.
- Objavljivanje ka mreži nikada ne sme blokirati prijem CAN okvira.

### Konfigurabilni režim obrade

Firmver implementira režime `P0`–`P3` (sekcija 17). Režim se menja komandom sa servera i čuva u NVS memoriji. Eksperimenti se izvode bez ponovnog programiranja uređaja, čime se iz merenja eliminiše varijabla „različite verzije firmvera".

---

## 8. Simulirani ECU (Arduino Uno + MCP2515)

Simulirani ECU je ravnopravna komponenta sistema, ne pomoćni alat:

- Izlaže **UDS servise koje realno vozilo ne izlaže** — sesije, security access, rutine, upis.
- Omogućava **kontrolisano ubacivanje grešaka** — DTC kodovi, negativni odgovori, timeout-i, oštećeni okviri.
- Omogućava **ponovljive eksperimente**, što je preduslov za statistički validno merenje.
- Uklanja zavisnost od pristupa vozilu, najveći logistički rizik projekta.

### Funkcionalnost

| Servis | Opis |
|---|---|
| OBD-II Mode 01 | Odgovor na skup PID-ova sa sintetičkim, fizički smislenim vrednostima |
| OBD-II Mode 03 / 04 | Čitanje i brisanje DTC kodova |
| OBD-II Mode 09 | VIN |
| UDS 0x10 | Diagnostic Session Control |
| UDS 0x22 | Read Data By Identifier |
| UDS 0x19 | Read DTC Information |
| UDS 0x27 | Security Access (seed/key) |
| UDS 0x31 | Routine Control |
| Injektor grešaka | Komandom se aktivira greška, kašnjenje ili negativan odgovor |

Generator vrednosti implementira jednostavan model vozila (obrtaji prate poziciju papučice, temperatura raste asimptotski), tako da podaci imaju vremensku strukturu pogodnu za testiranje analitike.

---

## 9. Sloj 2 — Edge server

Edge server je lokalni računar u istoj mreži kao gateway. Ima dvostruku ulogu: predstavlja **srednji nivo obrade** koji se poredi sa uređajem i oblakom, i **tačku otpornosti** — sistem ostaje funkcionalan kada veza ka oblaku ne postoji.

### Komponente

- MQTT broker (Mosquitto) — ulazna tačka podataka sa uređaja
- `DecodeService` — dekodovanje signala na osnovu DBC opisa
- Servis kratkoročne analize i detekcije anomalija
- Lokalna TimescaleDB instanca sa kratkim periodom zadržavanja
- Agent za prosleđivanje agregata ka oblaku
- Grafana Alloy kao lokalni kolektor telemetrije

### Store-and-forward

Kada veza ka oblaku nije dostupna, edge server nastavlja prijem i obradu, a agregate zadržava u lokalnom redu. Po uspostavljanju veze red se reprodukuje uz očuvanje redosleda i idempotentnost na strani potrošača. Isti obrazac primenjen je i na uređaju (SD kartica) — sistem ima **dva nivoa baferovanja**.

---

## 10. Sloj 3 — Računarski oblak

### Arhitektonski stil

Serverski deo realizuje se kao **modularni monolit iz kojeg se izdvajaju mikroservisi**. Ovo je svesna odluka, ne kompromis:

- Modularni monolit obezbeđuje jasne granice uz jednostavno lokalno pokretanje i debagovanje.
- Servisi se izdvajaju tek kada za to postoji stvaran razlog, ne unapred.
- Svako izdvajanje dokumentuje se kao ADR i predstavlja materijal za poglavlje o arhitekturi.

### Servisi

| Servis | Razlog izdvajanja | Skalira po |
|---|---|---|
| `IngestService` | Najveća učestanost poruka, nezavisno skaliranje | Broj uređaja |
| `DecodeService` | Procesorski intenzivno, bez stanja | Propusnost poruka |
| `DiagOrchestrator` | Dugotrajno stanje (sesije), drugačiji životni ciklus | Broj sesija |
| `DeviceRegistry` | Retke izmene, jaka konzistentnost | — |
| `ApiGateway` | Ulazna tačka, autentifikacija, kompozicija | Broj korisnika |

### Slojevi unutar servisa (heksagonalna arhitektura)

```
        ┌───────────────────────────────┐
        │          Domain               │  bez I/O, bez framework-a
        └───────────────┬───────────────┘
        ┌───────────────┴───────────────┐
        │        Application            │  use case-ovi, portovi (interfejsi)
        └───────────────┬───────────────┘
        ┌───────────────┴───────────────┐
        │       Infrastructure          │  adapteri: EF Core, MQTTnet, MassTransit
        └───────────────────────────────┘
```

Pravilo zavisnosti: zavisnosti pokazuju isključivo ka unutra. `Domain` projekat ne referencira nijedan paket koji obavlja I/O. **Provera se automatizuje kroz NetArchTest u `tests/Architecture.Tests` i mora biti zelena u CI-ju** — pravilo koje se ne proverava automatski nije pravilo.

---

## 11. Domenski model i ograničeni konteksti

### Ograničeni konteksti

| Kontekst | Jezik domena | Odgovornost |
|---|---|---|
| Device Management | Uređaj, registracija, konfiguracija, zdravlje | Ko su uređaji i u kakvom su stanju |
| Telemetry | Očitavanje, uzorak, serija | Prijem i trajno čuvanje merenja |
| Signal Decoding | Poruka, signal, DBC opis, fizička vrednost | Prevođenje sirovih podataka u fizičke veličine |
| Diagnostics | Sesija, zahtev, odgovor, DTC, rutina | Dijagnostičke operacije nad ECU-om |
| Analytics | Prozor, agregat, anomalija, zdravlje vozila | Izvođenje uvida iz podataka |

Konteksti komuniciraju isključivo preko događaja i eksplicitnih ugovora. Deljenje tabela između konteksta je zabranjeno; svaki kontekst ima sopstvenu šemu u bazi.

### Ključni agregati

```csharp
// Koren agregata
public sealed class Vehicle
{
    public VehicleId Id { get; }
    public Vin Vin { get; }
    private readonly List<DeviceId> _devices = [];
    private readonly List<EcuAddress> _ecus = [];
}

public sealed class Device
{
    public DeviceId Id { get; }
    public ProcessingMode ProcessingMode { get; private set; }
    public DateTimeOffset LastSeen { get; private set; }
    public DeviceHealth Health { get; private set; }
}

public sealed class DiagnosticSession
{
    public SessionId Id { get; }
    public EcuAddress Target { get; }
    public SessionState State { get; private set; }
    public SecurityLevel SecurityLevel { get; private set; }
}
```

### Objekti vrednosti

`CanId`, `Pid`, `Did`, `DtcCode`, `Vin`, `SignalValue`, `EcuAddress`, `DeviceId`, `VehicleId`, `SessionId`

Svi se realizuju kao `readonly record struct` sa validacijom u factory metodi:

```csharp
public readonly record struct Pid
{
    public byte Value { get; }
    private Pid(byte value) => Value = value;

    public static Result<Pid, ValidationError> Create(byte value) =>
        IsSupported(value)
            ? Result.Ok(new Pid(value))
            : Result.Fail<Pid, ValidationError>(ValidationError.UnsupportedPid(value));
}
```

Time je nemoguće konstruisati nevalidan `Pid` niti ga zameniti sa `Did` — kompajler to sprečava, bez troška u vreme izvršavanja.

### Domenski događaji

`TelemetryReceived` · `SignalDecoded` · `DtcDetected` · `DtcCleared` · `AnomalyDetected` · `SessionOpened` · `SessionClosed` · `DeviceWentOffline` · `ProcessingModeChanged`

---

## 12. Projektni obrasci

### 12.1 Obrasci u firmveru

| Obrazac | Primena | Zašto baš tu |
|---|---|---|
| **Layered architecture** | TWAI → ISO-TP → dijagnostika → aplikacija | Protokolski stek je prirodno slojevit; omogućava izolovano testiranje |
| **State machine** | ISO-TP reasemblacija, UDS sesija | Protokol *jeste* automat; eksplicitno modelovanje eliminiše celu klasu grešaka |
| **Producer–consumer** | FreeRTOS redovi između zadataka | Razdvaja vremenski kritičan prijem od sporog objavljivanja |
| **Ring buffer** | Bafer CAN okvira | Ograničena memorija, poznato ponašanje pri prelivanju |
| **Strategy** | Izbor režima obrade P0–P3 | Jezgro istraživanja — režim zamenjiv u vreme izvršavanja |
| **Command** | UDS zahtevi kao objekti | Omogućava red čekanja, ponavljanje, timeout po zahtevu |
| **Facade** | Jedinstven dijagnostički API ka aplikaciji | Skriva razliku između OBD-II i UDS puta |
| **Store-and-forward (Outbox)** | SD bafer pri prekidu veze | Trajnost bez blokiranja glavnog toka |
| **Watchdog / Supervisor** | Nadzor zadataka | Oporavak od zaglavljivanja bez intervencije |
| **Object pool** | Preraspodela bafera poruka | Izbegavanje dinamičke alokacije na vrućoj putanji |

### 12.2 Obrasci u serverskom delu

| Obrazac | Realizacija u .NET-u |
|---|---|
| **Hexagonal (Ports & Adapters)** | Portovi kao interfejsi u `Application`, adapteri u `Infrastructure` |
| **Repository** | `ITelemetryRepository` u `Application`, EF Core implementacija u `Infrastructure` |
| **Anti-corruption layer** | Granica između automobilskog domena (CAN/UDS) i internog modela |
| **CQRS (laka varijanta)** | Odvojeni handleri i modeli za pisanje i čitanje; Dapper na putanji čitanja |
| **Event-driven / Pub-Sub** | MassTransit `IPublishEndpoint` / `IConsumer<T>` |
| **Saga / Process Manager** | MassTransit `MassTransitStateMachine` za `DiagOrchestrator` |
| **Transactional Outbox** | MassTransit EF Core Outbox — atomičan upis i objava |
| **Idempotent Consumer** | Deduplikacija po `(DeviceId, Sequence)`; nužno uz at-least-once isporuku |
| **Circuit Breaker / Retry** | `Microsoft.Extensions.Http.Resilience`, MassTransit retry politike |
| **Bulkhead** | Odvojeni pulovi resursa po servisu |
| **Dead Letter Queue** | MassTransit `_error` i `_skipped` redovi |
| **API Gateway / BFF** | YARP sa autentifikacijom i kompozicijom odgovora |
| **Strangler Fig** | Postupno izdvajanje servisa iz monolita |
| **Sidecar** | Grafana Alloy uz svaki servis |
| **Health checks** | `Microsoft.Extensions.Diagnostics.HealthChecks`, `/health` i `/alive` |

### 12.3 Obrasci specifični za C#

Ovi obrasci su argument *zašto je C# adekvatan izbor*, a ne samo *da jeste*.

**Strongly-typed IDs preko `readonly record struct`** — tipska bezbednost bez alokacije na hipu. Zamena `Pid` i `Did` postaje greška pri kompajliranju.

**Typestate kroz generičke fantomske tipove** — stanje protokola kodirano u sistemu tipova:

```csharp
public interface ISessionState;
public interface IUnlocked : ISessionState;

public readonly struct DefaultSession : ISessionState;
public readonly struct ExtendedSession : ISessionState;
public readonly struct UnlockedSession : IUnlocked;

public sealed class UdsSession<TState> where TState : ISessionState
{
    public UdsSession<ExtendedSession> EnterExtended() { /* ... */ }
}

public static class UdsSessionExtensions
{
    // Dostupno isključivo kada je sesija otključana
    public static Task<Result<Unit, UdsError>> WriteDataAsync<TState>(
        this UdsSession<TState> session, Did did, ReadOnlyMemory<byte> data,
        CancellationToken ct) where TState : IUnlocked { /* ... */ }
}
```

Poziv `WriteDataAsync` nad sesijom koja nije otključana **ne kompajlira se**. Za dijagnostički sistem, gde pogrešan redosled operacija ima fizičke posledice, ovo je jak argument i zaslužuje poseban pododeljak u radu.

**`Result<T, TError>` umesto izuzetaka za očekivane greške** — negativni odgovori (NRC), timeout-i i neuspela validacija su očekivana stanja protokola, ne izuzetne situacije. Izuzeci se rezervišu za stvarno izuzetne uslove.

**Source generators za serijalizaciju** — `System.Text.Json` sa `JsonSerializerContext` uklanja refleksiju iz vruće putanje i omogućava Native AOT.

**`TimeProvider`** — vreme ulazi kroz apstrakciju, nikada preko `DateTime.Now`; testovi deterministički kontrolišu vreme.

**`System.IO.Pipelines` i `Span<T>`** — parsiranje binarnih CAN okvira bez nepotrebnih alokacija na putanji ingestije.

**`Channel<T>`** — asinhroni producer-consumer unutar servisa, .NET ekvivalent FreeRTOS redova iz firmvera.

---

## 13. Komunikacija, teme i formati poruka

### Hijerarhija MQTT tema

```
veh/{vehicleId}/dev/{deviceId}/tel/raw        # sirovi CAN okviri (P0)
veh/{vehicleId}/dev/{deviceId}/tel/pdu        # reasemblirani PDU (P1)
veh/{vehicleId}/dev/{deviceId}/tel/signals    # dekodovani signali (P2)
veh/{vehicleId}/dev/{deviceId}/evt/dtc        # DTC događaji
veh/{vehicleId}/dev/{deviceId}/evt/anomaly    # anomalije (P3)
veh/{vehicleId}/dev/{deviceId}/sys/status     # zdravlje uređaja
veh/{vehicleId}/dev/{deviceId}/cmd/req        # komanda ka uređaju
veh/{vehicleId}/dev/{deviceId}/cmd/resp       # odgovor uređaja
```

Struktura tema je stabilna u svim režimima — menja se samo koja se koristi. Time isti potrošači rade u svim scenarijima.

### Format poruka

Poređenje formata je deo evaluacije jer direktno utiče na opterećenje mreže:

| Format | Prednost | Nedostatak |
|---|---|---|
| JSON | Čitljiv, jednostavan debug | Najveći obim |
| CBOR | Binaran, kompaktan, samoopisujući | Slabija čitljivost |
| MessagePack | Kompaktan, široka podrška | Slično CBOR-u |
| Protobuf | Najkompaktniji, formalna šema | Zahteva generisanje koda |

Podrazumevano CBOR; JSON kao režim za debagovanje. Razlika u obimu se meri i iskazuje u rezultatima.

### Anatomija poruke

```csharp
public sealed record MessageEnvelope<TPayload>
{
    public required int SchemaVersion { get; init; }
    public required DeviceId DeviceId { get; init; }
    public required VehicleId VehicleId { get; init; }
    public required long Sequence { get; init; }          // osnov za deduplikaciju
    public required DateTimeOffset TimestampDevice { get; init; }
    public required string TraceId { get; init; }          // W3C Trace Context
    public required ProcessingMode ProcessingMode { get; init; }
    public required TPayload Payload { get; init; }
}
```

`TraceId` je arhitektonski ključan: bez njega nije moguće izmeriti kašnjenje kroz slojeve, a to je centralna metrika istraživanja.

### Interni bus

MassTransit nad RabbitMQ. Teme prate ograničene kontekste (`telemetry.raw`, `telemetry.decoded`, `diagnostics.events`, `device.lifecycle`). Particionisanje po `DeviceId` obezbeđuje redosled po uređaju uz paralelizam između uređaja. Kafka se može uključiti kao alternativni transport radi poređenja propusnosti.

---

## 14. Skladištenje podataka

| Skladište | Namena | Obrazloženje |
|---|---|---|
| TimescaleDB | Dekodovani signali, metrike zdravlja | Vremenske serije velike učestanosti, upiti po prozorima |
| PostgreSQL | Vozila, uređaji, korisnici, konfiguracije | Jaka konzistentnost, relacije, transakcije |
| MinIO | DBC datoteke, snimci sesija, izveštaji | Nestruktuiran sadržaj promenljive veličine |

Izolacija podataka po kontekstu ostvaruje se odvojenim šemama, uz zabranu unakrsnog pristupa (proverava se u code review-u i integracionim testovima).

Politika zadržavanja: sirovi podaci kratko i u visokoj rezoluciji; agregati dugoročno u nižoj rezoluciji. Na edge serveru period zadržavanja je kraći nego u oblaku — to je deo argumenta zašto edge sloj postoji.

---

## 15. Osmatranje sistema

Osmatranje nije dodatak nego **merni instrument istraživanja**. Bez njega scenariji iz sekcije 17 ne mogu biti kvantitativno upoređeni.

| Signal | Alat | Šta se meri |
|---|---|---|
| Trasiranje | OpenTelemetry → Tempo | Putanja zahteva: uređaj → edge → oblak → nazad |
| Metrike | OpenTelemetry → Prometheus / Mimir | Propusnost, kašnjenje po fazi, resursi, stopa grešaka |
| Logovi | Serilog / `ILogger` → Loki | Strukturirani logovi sa `TraceId` korelacijom |
| Vizuelizacija (operativna) | Grafana | Kontrolne table sistema: tragovi, metrike, logovi |
| Vizuelizacija (evaluacija, ADR-0005) | Angular kontrolna tabla | Podaci uživo, istorija signala, poređenje režima P0–P3, CSV izvoz |

### Instrumentacija po slojevima

- **Uređaj** — vremenske oznake po fazama (prijem okvira, kraj reasemblacije, kraj dekodovanja, objavljivanje), iskorišćenje CPU i memorije, dubina redova, broj odbačenih okvira, jitter uzorkovanja. Šalju se u `sys/status` temi.
- **Backend** — `ActivitySource` i `Meter` iz .NET-a, automatska instrumentacija za ASP.NET Core, HttpClient, EF Core i MassTransit; OTLP izvoz.
- **Propagacija konteksta** — `TraceId` se prenosi kroz MQTT korisni deo i kroz MassTransit zaglavlja. Gubitak kontinuiteta traga znači gubitak merenja.
- **Aspire** — `ServiceDefaults` projekat centralizuje OpenTelemetry konfiguraciju, health check-ove i politike otpornosti za sve servise.

Domenske metrike uz tehničke: stopa negativnih odgovora (NRC), broj neuspelih reasemblacija, opterećenje CAN magistrale, broj detektovanih DTC kodova.

---

## 16. Bezbednost

### Bezbednost sistema

- TLS na svim mrežnim vezama.
- Identitet uređaja: sertifikat ili token po uređaju, nikada deljeni pristupni podaci.
- Autorizacija po temama — uređaj objavljuje isključivo na svoje teme.
- Autentifikacija na API gateway-u; komande koje menjaju stanje zahtevaju povišen nivo ovlašćenja.
- Revizioni trag za svaku dijagnostičku komandu.

### Bezbednost u automobilskom smislu

UDS SecurityAccess (servis `0x27`) implementira se **isključivo nad simuliranim ECU-om**. Analiza obuhvata napad ponavljanjem, slabe seed/key algoritme i posledice neautentifikovanog pristupa magistrali. Kontekstualizacija kroz ISO/SAE 21434 i UN R155 daje radu širu težinu.

### Operativna bezbednost (obavezno)

- Nad realnim vozilom izvršavaju se **isključivo operacije čitanja** (Mode 01, 03, 09).
- Nikada: `ECUReset`, upis po identifikatoru, rutine koje menjaju stanje, programiranje.
- Sve operacije upisa i sve UDS sesije izvode se nad simuliranim ECU-om.
- Kodne putanje koje izvršavaju upis moraju biti zaštićene eksplicitnom proverom tipa odredišta (`EcuTarget.Simulated`).
- Vozilo parkirano tokom merenja; uređaj se ne ostavlja priključen (pin 16 je stalno pod naponom).
- Osigurač na +12 V liniji obavezan.

---

## 17. Scenariji raspodele obrade (jezgro istraživanja)

Obrada se dekomponuje na četiri faze:

```
F1: Prijem okvira    F2: Reasemblacija (ISO-TP)    F3: Dekodovanje    F4: Analiza
```

Režimi definišu koliko faza se izvršava na uređaju:

| Režim | Na uređaju | Šalje se | Očekivanje |
|---|---|---|---|
| **P0** | F1 | Sirovi CAN okviri | Najveće opterećenje mreže, najmanje CPU na uređaju |
| **P1** | F1, F2 | Reasemblirani PDU | Umeren obim, ISO-TP tajminzi zadovoljeni lokalno |
| **P2** | F1, F2, F3 | Dekodovani signali | Znatno manji obim, veće CPU opterećenje |
| **P3** | F1–F4 | Samo događaji i anomalije | Najmanji obim, najveće opterećenje uređaja |

Nezavisno od režima, varira se odredište obrade:

| Odredište | Opis |
|---|---|
| **D1** | Sve u oblak |
| **D2** | Obrada na edge serveru, agregati u oblak |
| **D3** | Obrada na edge serveru, oblak samo za dugoročno čuvanje |

Matrica: 4 režima × 3 odredišta = 12 konfiguracija. Svaka se izvršava nad **identičnim** ulazom (reprodukcija snimljenog CAN saobraćaja), čime se garantuje uporedivost.

### Dodatne varijable

- Učestanost uzorkovanja: 1 Hz, 10 Hz, 50 Hz, 100 Hz
- Kvalitet mreže: idealna, ograničen propusni opseg, gubitak 1 % / 5 %, potpuni prekid
- Format serijalizacije: JSON, CBOR
- Broj simuliranih uređaja: 1, 10, 100, 500 (virtuelni klijenti u kontejnerima)

---

## 18. Metodologija evaluacije i metrike

### Metrike

| Kategorija | Metrika | Način merenja |
|---|---|---|
| Kašnjenje | Kraj-do-kraja (p50, p95, p99) | Trasiranje po `TraceId`, od prijema okvira do upisa u bazu |
| Kašnjenje | Po fazi | Rasponi (spans) unutar traga |
| Propusnost | Okvira/s, poruka/s | Brojači u svakoj fazi |
| Mreža | Bajtova/s, veličina korisnog dela | Merenje na MQTT klijentu |
| Resursi (uređaj) | CPU %, slobodna memorija, dubina redova | Interni brojači, `esp_get_free_heap_size` |
| Resursi (server) | CPU, RAM, mrežni I/O po servisu | Prometheus, .NET runtime metrike |
| Potrošnja | Prosečna struja | Merenje na napajanju uređaja |
| Pouzdanost | Izgubljene poruke, neuspele reasemblacije | Poređenje `Sequence` brojača |
| Determinizam | Jitter uzorkovanja | Odstupanje intervala od nominalnog |
| Tačnost | Odstupanje dekodovanih vrednosti | Poređenje P2/P3 sa referentnim dekodovanjem |

### Protokol merenja

1. Snimiti referentni CAN saobraćaj (realno vozilo + simulirani ECU) u datoteku.
2. Za svaku konfiguraciju reprodukovati **identičan** snimak.
3. Svako merenje ponoviti najmanje 10 puta; odbaciti prvi ciklus (zagrevanje JIT-a i keševa).
4. Izveštavati medijanu i percentile, ne samo srednju vrednost.
5. Rezultate izvoziti iz Prometheus-a u tabelarni oblik za dalju obradu.

### Kontrola valjanosti

- Referentna verifikacija ELM327 skenerom — potvrda tačnosti očitanih vrednosti.
- Verifikacija sopstvene ISO-TP implementacije naspram `isotpsend`/`isotprecv` (SocketCAN), ako se nabavi USB-CAN adapter.
- Sinhronizacija vremena preko NTP-a; merenje i kompenzacija pomeraja sata na uređaju.

---

## 19. Dobre prakse i inženjerska disciplina

### Testiranje

| Nivo | Predmet | Alat |
|---|---|---|
| Jedinični | Domenska logika, ISO-TP automat, dekoderi | xUnit, FluentAssertions |
| Firmver (host) | ISO-TP i dijagnostički slojevi bez hardvera | Unity / CMock ili host build |
| Protokolska usaglašenost | ISO-TP segmentacija, UDS odgovori, NRC kodovi | Tabelarni testovi (`[Theory]`) |
| Reprodukcija | Dekodovanje nad snimljenim CAN logom | Snimci kao test fiksture |
| Integracioni | Repozitorijumi, broker, baze | Testcontainers |
| Ugovorni | Šeme poruka između servisa | Verzionisane šeme, provera kompatibilnosti |
| Sistemski | Ceo tok uređaj → oblak | .NET Aspire test host |
| Arhitektonski | Pravila zavisnosti između slojeva | NetArchTest |
| Otpornost | Otkazi mreže i ubačene greške | Injektor grešaka u simuliranom ECU-u |

### Praksa razvoja

- **ADR (Architecture Decision Records)** — svaka značajna odluka u `docs/adr/` po šablonu: kontekst, odluka, posledice, razmatrane alternative. Tabela iz sekcije 5 generiše početni skup ADR-ova. Ovo direktno postaje sadržaj poglavlja o arhitekturi.
- **Konfiguracija preko okruženja** — ista slika u svim okruženjima; bez tvrdo kodiranih vrednosti.
- **Strukturirani logovi** sa `TraceId` u svakom zapisu.
- **Semantičko verzionisanje** ugovora; `SchemaVersion` u svakoj poruci.
- **CI cevovod** — `dotnet build`, `dotnet format --verify-no-changes`, analizatori kao greške, `dotnet test`, NetArchTest, izgradnja kontejnerskih slika.
- **Infrastruktura kao kod** — Aspire za lokalno okruženje, Helm chart za k3s.
- **Nullable reference types uključeni**, upozorenja tretirana kao greške.
- **`Directory.Build.props`** za deljene postavke svih projekata (`TreatWarningsAsErrors`, `Nullable`, `LangVersion`, `AnalysisLevel`).

### Kvalitet koda

- `Domain` bez zavisnosti od framework-a — proverava se automatski.
- Javni API svakog projekta dokumentovan XML komentarima.
- Granice projekata poklapaju se sa granicama ograničenih konteksta.
- Bez kružnih zavisnosti.
- Bez `.Result`, `.Wait()`, `async void`; `CancellationToken` se prosleđuje kroz ceo asinhroni lanac.

---

## 20. Analiza složenosti sistema

### Dimenzije složenosti

| Dimenzija | Izvor | Kako se ograničava |
|---|---|---|
| **Protokolska** | ISO-TP, OBD-II, UDS — automati sa tajmerima | Slojevita dekompozicija; automati modelovani eksplicitno, ne kroz razbacane uslove |
| **Vremenska** | Tvrda ograničenja (flow control), determinističko uzorkovanje | Razdvajanje kritičnih od nekritičnih zadataka; nema blokiranja na vrućoj putanji |
| **Distribuirana** | Tri sloja, asinhrona komunikacija, delimični otkazi | Idempotentnost, at-least-once uz deduplikaciju, eksplicitne garancije isporuke |
| **Heterogenost** | Tri jezika (C, C++, C#), tri klase hardvera | Jedinstven ugovor poruka; jedinstveno trasiranje kroz slojeve |
| **Konfiguraciona** | 12 kombinacija režima i odredišta | Režim kao podatak, ne kao grana u kodu |
| **Operativna** | Kontejneri, orkestracija, osmatranje | Aspire za lokalno; jedna komanda za pokretanje celog sistema |

### Suštinska naspram slučajne složenosti

**Suštinska** (neizbežna, čini srž rada): automobilski protokolski stek, distribuirana priroda sistema, vremenska ograničenja ugrađenog uređaja, raspodela obrade kao predmet istraživanja.

**Slučajna** (aktivno se suzbija): raznolikost tehnologija bez opravdanja, prerano izdvajanje servisa, apstrakcije bez potrebe, duplirana logika između slojeva.

Sekcija 5 je formalni mehanizam za suzbijanje slučajne složenosti — ništa ne ulazi u sistem bez zapisanog razloga.

### Granice obima (šta se namerno ne radi)

- Ne implementira se CAN FD — klasičan CAN je dovoljan za OBD-II i UDS.
- Ne implementira se DoIP — pominje se kao pravac razvoja.
- Ne implementira se programiranje ECU-a (servisi `0x34`–`0x37`) osim kao opcioni prikaz nad simulatorom.
- Ne radi se obrnuti inženjering proizvođačkih identifikatora.
- Frontend se razvija samo kao klijent javnog API-ja za čitanje (ADR-0005): ne čita bazu i ne uvodi sopstvenu poslovnu logiku.
- Odbijene tehnologije iz sekcije 5 ne ulaze u sistem ni „usput".

### Redosled obuzdavanja složenosti

U svakom trenutku postoji **radna kičma kraj-do-kraj**, a složenost se dodaje sloj po sloj. Nikada se ne grade dva nezavršena sloja istovremeno.

---

## 21. Struktura repozitorijuma

```
.
├── firmware/
│   ├── CONVENTIONS.md                        # konvencije za C / ESP-IDF
│   ├── gateway/                              # ESP32 dijagnostički gateway
│   │   ├── main/
│   │   ├── components/
│   │   │   ├── hal/
│   │   │   ├── can_link/                     # TWAI drajver, filtri
│   │   │   ├── isotp/                        # ISO-TP automat
│   │   │   ├── diag/                         # OBD-II servisi, UDS klijent
│   │   │   ├── processing/                   # režimi P0–P3
│   │   │   └── net/                          # WiFi, MQTT, store-and-forward
│   │   └── test/                             # host testovi protokolskog steka
│   └── sim-ecu/                              # Arduino Uno simulirani ECU
├── backend/
│   ├── VehicleDiagnostics.slnx
│   ├── Directory.Build.props
│   ├── src/
│   │   ├── AppHost/                          # .NET Aspire orkestracija
│   │   ├── ServiceDefaults/                  # OTel, health, resilience
│   │   ├── Contracts/                        # verzionisani ugovori poruka
│   │   ├── Domain/                           # entiteti, objekti vrednosti, događaji
│   │   ├── Application/                      # use case-ovi, portovi (interfejsi)
│   │   ├── Infrastructure/                   # EF Core, MQTTnet, MassTransit, DBC
│   │   └── Services/
│   │       ├── IngestService/
│   │       ├── DecodeService/
│   │       ├── DiagOrchestrator/
│   │       ├── DeviceRegistry/
│   │       └── ApiGateway/
│   └── tests/
│       ├── Domain.Tests/
│       ├── Application.Tests/
│       ├── Integration.Tests/
│       └── Architecture.Tests/               # NetArchTest pravila
├── edge/                                     # kompozicija za edge server
├── deploy/
│   ├── compose/
│   └── helm/
├── observability/
│   ├── alloy/
│   ├── dashboards/
│   └── alerts/
├── experiments/
│   ├── scenarios/                            # definicije 12 konfiguracija
│   ├── captures/                             # snimci CAN saobraćaja
│   ├── load-generator/                       # virtuelni uređaji
│   └── results/
├── docs/
│   ├── architecture.md                       # ovaj dokument
│   ├── adr/
│   ├── diagrams/
│   └── protocol-notes/
└── thesis/                                   # tekst rada (srpski)
```

---

## 22. Plan realizacije po fazama

| Faza | Sadržaj | Kriterijum završetka |
|---|---|---|
| **F0** | Radni sto, rešenje i skelet projekata, NetArchTest pravila, prvi CAN okvir ESP32 ↔ Uno | Okvir poslat i primljen u oba smera; arhitektonski testovi zeleni |
| **F1** | ISO-TP na oba uređaja; OBD-II Mode 01 nad simulatorom | Višeokvirna poruka uspešno reasemblirana |
| **F2** | Čitanje sa realnog vozila (samo čitanje), verifikacija ELM327 skenerom | Vrednosti se poklapaju sa referentnim skenerom |
| **F3** | MQTT objavljivanje; edge server sa brokerom i TimescaleDB; Grafana | Podaci vidljivi na kontrolnoj tabli uživo |
| **F4** | Modularni monolit, domen + prvi izdvojeni servis; Aspire | Ceo tok radi jednom komandom |
| **F5** | Režimi P0–P3, instrumentacija, OpenTelemetry trasiranje | Kašnjenje merljivo kroz sve slojeve |
| **F6** | Izvođenje eksperimenata, generator opterećenja, prikupljanje rezultata | Popunjena matrica 4×3 sa ponovljenim merenjima |
| **F7** | UDS sesije i security access nad simulatorom | Typestate automat radi kraj-do-kraj |
| **F8** | *Prošireno:* k3s, pun LGTM, drugi izdvojeni servis, store-and-forward, Native AOT poređenje | — |
| **F9** | Pisanje rada, obrada rezultata, dijagrami | — |

**Kičma sistema (F0–F6) mora biti završena pre bilo čega iz F8.** Prošireni deo su poglavlja o produkcionoj zrelosti i ne smeju blokirati evaluaciju.

---

## 23. Rizici i mere ublažavanja

| Rizik | Verovatnoća | Uticaj | Mera |
|---|---|---|---|
| Vozilo ne izlaže CAN na OBD portu | Srednja | Visok | Provera pinova 6 i 14; simulirani ECU pokriva sve eksperimente |
| ISO-TP tajminzi se ne poštuju | Srednja | Visok | Rani prototip; validacija naspram `can-utils`; prioriteti zadataka |
| Preširok obim tehnologija | Visoka | Visok | Sekcija 5 kao formalni filter; podela na kičmu i prošireni deo |
| Oštećenje ECU-a vozila | Niska | Vrlo visok | Isključivo čitanje nad vozilom; upisi zaštićeni proverom tipa odredišta |
| Oštećenje uređaja naponskim udarom | Srednja | Srednji | Osigurač, buck sa zaštitom; power bank za prve testove |
| Rezultati merenja nisu uporedivi | Srednja | Visok | Identičan snimak; režim kao konfiguracija, ne kao grana koda |
| Nedovoljno vremena za evaluaciju | Srednja | Visok | Instrumentacija u F5, pre proširenja; evaluacija nije poslednji korak |

---

## 24. Rečnik pojmova

| Pojam | Značenje |
|---|---|
| **CAN** | Controller Area Network — magistrala za komunikaciju između upravljačkih jedinica vozila |
| **TWAI** | Two-Wire Automotive Interface — Espressif-ov naziv za CAN kontroler u ESP32 |
| **ISO-TP** | ISO 15765-2, transportni protokol za poruke veće od 8 bajtova preko CAN-a |
| **OBD-II** | On-Board Diagnostics II — standardizovana dijagnostika emisija i osnovnih parametara |
| **UDS** | Unified Diagnostic Services (ISO 14229) — opšti dijagnostički protokol |
| **PID** | Parameter Identifier — identifikator parametra u OBD-II |
| **DID** | Data Identifier — identifikator podatka u UDS-u |
| **DTC** | Diagnostic Trouble Code — kod dijagnostičke greške |
| **NRC** | Negative Response Code — kod negativnog odgovora u UDS-u |
| **ECU** | Electronic Control Unit — elektronska upravljačka jedinica |
| **DBC** | Format datoteke koja opisuje mapiranje CAN poruka u fizičke signale |
| **PDU** | Protocol Data Unit — kompletna poruka nakon reasemblacije |
| **ADR** | Architecture Decision Record — zapis arhitektonske odluke |
| **LGTM** | Loki, Grafana, Tempo, Mimir — skup alata za osmatranje sistema |
| **Far edge** | Krajnji uređaj uz izvor podataka (ESP32 gateway) |
| **Near edge** | Posrednički čvor između uređaja i oblaka (edge server) |
