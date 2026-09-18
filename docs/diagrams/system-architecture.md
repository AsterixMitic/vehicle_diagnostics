# System architecture diagrams

Visual companion to `../architecture.md`, which stays the single source of truth. Where a
diagram and that document disagree, the document wins and the diagram is the bug.

Status marking is deliberate: this is a thesis in progress, so each diagram distinguishes what
is **implemented and verified**, what is **built but not yet wired into a running path**, and
what is **specified but not yet built**.

Companion documents: `hardware-wiring.md` for how to connect the boards, `backend-detail.md`
for the server side at the level of its actual types and messages.

---

## 1. Whole system

Three physical layers, plus the signal sources that feed them.

```mermaid
flowchart TB
    subgraph L0["LAYER 0 · Signal sources"]
        direction LR
        CAR["Real vehicle<br/>OBD-II socket<br/>read-only services only"]
        SIM["Simulated ECU<br/>Arduino Uno + MCP2515<br/>UDS + fault injection"]
        REP["Capture replay<br/>recorded CAN traffic<br/>via USB-CAN adapter"]
    end

    subgraph L1["LAYER 1 · Far edge — ESP32 gateway"]
        direction TB
        HAL["TWAI driver — CAN frames"]
        TP["ISO-TP transport — ISO 15765-2"]
        DG["OBD-II services · UDS client"]
        PR["Processing mode P0–P3<br/>runtime configuration"]
        NET["MQTT publisher<br/>store-and-forward buffer"]
        HAL --> TP
        TP --> DG
        DG --> PR
        PR --> NET
    end

    VD["Virtual device service<br/>publishes identical envelopes<br/>no hardware required"]

    subgraph L2["LAYER 2 · Near edge — edge server"]
        direction TB
        MQ["MQTT broker — Mosquitto"]
        API["Modular monolith host"]
        BUS["Internal bus — RabbitMQ"]
        TS[("TimescaleDB<br/>signal_readings hypertable")]
        OTEL["OpenTelemetry collector<br/>and dashboard"]
    end

    subgraph L3["LAYER 3 · Cloud"]
        direction TB
        K8S["Services on k3s"]
        LGTM["Loki · Grafana · Tempo · Mimir"]
        LONG[("Long-term aggregate storage")]
    end

    CAR -->|"CAN 2.0B at 500 kbps"| HAL
    SIM -->|"CAN 2.0B at 500 kbps"| HAL
    REP -->|"CAN 2.0B at 500 kbps"| HAL
    NET -->|"MQTT over TLS"| MQ
    VD -->|"MQTT — same topics, same envelope"| MQ
    MQ --> API
    API <--> BUS
    API --> TS
    API -.->|"OTLP"| OTEL
    API -->|"aggregates"| K8S
    OTEL -.-> LGTM
    K8S --> LONG

    classDef built fill:#dcfce7,stroke:#16a34a,stroke-width:2px,color:#14532d
    classDef partial fill:#fef3c7,stroke:#d97706,stroke-width:2px,color:#78350f
    classDef planned fill:#f1f5f9,stroke:#94a3b8,stroke-dasharray:5 5,color:#334155
    class MQ,API,BUS,TS,OTEL,VD built
    class HAL,TP partial
    class CAR,SIM,REP,DG,PR,NET,K8S,LGTM,LONG planned
```

Green with a solid border is implemented and verified. Amber is built and tested but not yet
wired into a running path: the CAN link layer runs but sends only a fixed frame, and the ISO-TP
transport layer is complete and covered by tests without the diagnostic client that will drive
it. Grey with a dashed border is specified but not yet built.

**Architecture invariant.** The boundary and the protocol between layers are identical in every
processing mode. Only *which layer performs which phase* changes. Without that invariant the
mode comparison is not a fair test, and the fair test is the thesis.

---

## 2. Signal sources and where each one injects

Five ways to get data in, at two injection points. This is the property that lets the system be
developed and measured without a vehicle present.

```mermaid
flowchart LR
    subgraph HW["Requires hardware"]
        direction TB
        CAR["Real vehicle<br/>ground truth, read-only"]
        SIM["Arduino sim-ECU<br/>UDS, DTCs, injected faults"]
        USB["Capture replay over CAN<br/>USB-CAN adapter"]
    end

    subgraph SW["No hardware"]
        direction TB
        VD["Virtual device service<br/>synthetic drive cycle"]
        MREP["Capture replay to MQTT<br/>recorded frames, no CAN"]
    end

    ESP["ESP32 gateway firmware<br/>TWAI · ISO-TP · OBD-II · P0–P3"]

    CONTRACT{{"THE CONTRACT<br/>topic veh/.../dev/.../tel/raw or tel/signals<br/>MessageEnvelope, schemaVersion 1"}}

    BE["Backend — identical code for every source"]

    CAR -->|"CAN frames"| ESP
    SIM -->|"CAN frames"| ESP
    USB -->|"CAN frames"| ESP
    ESP --> CONTRACT
    VD --> CONTRACT
    MREP --> CONTRACT
    CONTRACT --> BE

    classDef built fill:#dcfce7,stroke:#16a34a,stroke-width:2px,color:#14532d
    classDef planned fill:#f1f5f9,stroke:#94a3b8,stroke-dasharray:5 5,color:#334155
    classDef iface fill:#fef3c7,stroke:#d97706,stroke-width:2px,color:#78350f
    class VD,BE built
    class CAR,SIM,USB,MREP,ESP planned
    class CONTRACT iface
```

| Source | Injects at | Exercises | Hardware |
|---|---|---|---|
| Real vehicle | CAN | firmware and backend, real timing | vehicle, gateway |
| Arduino sim-ECU | CAN | firmware and backend, repeatable | bench, gateway |
| Capture replay over CAN | CAN | firmware and backend, byte-identical runs | USB-CAN adapter |
| Virtual device service | MQTT | backend only | none |
| Capture replay to MQTT | MQTT | backend only | none |

Two injection points, and the difference matters. A CAN-level source replaces the **vehicle**, so
the firmware still runs and its timing is measured. An MQTT-level source replaces the **whole
device**, so the firmware is bypassed entirely and only backend behaviour is under test.

---

## 3. Ingest pipeline — how P0 and P2 converge

Both processing modes reach the same storage path through the same deduplication, which is why
their results are directly comparable.

```mermaid
flowchart TB
    PUB["Device publishes envelope"]
    PUB --> TOPIC{"Which topic?"}

    TOPIC -->|"tel/raw — mode P0"| RAWV["MqttIngestService<br/>parse envelope, validate frames"]
    TOPIC -->|"tel/signals — mode P2"| SIGV["MqttIngestService<br/>parse envelope, validate readings"]

    RAWV --> ACC["AcceptRawTelemetry<br/>Telemetry context"]
    ACC -->|"RawFramesReceived"| B1(["RabbitMQ"])
    B1 --> DEC["DecodeFrames — Signal Decoding context<br/>ISO-TP single frame, OBD-II Mode 01, SAE J1979"]
    DEC -->|"SignalsDecoded"| B2(["RabbitMQ"])
    B2 --> ING["IngestTelemetry<br/>Telemetry context"]
    SIGV --> ING

    ING --> DB[("signal_readings hypertable<br/>ON CONFLICT DO NOTHING<br/>keyed by device, sequence, signal, time")]

    RAWV -.->|"invalid"| REJ["Counted by reason<br/>pipeline continues"]
    SIGV -.->|"invalid"| REJ
    DEC -.->|"undecodable frame"| REJ

    classDef built fill:#dcfce7,stroke:#16a34a,stroke-width:2px,color:#14532d
    class PUB,TOPIC,RAWV,SIGV,ACC,B1,DEC,B2,ING,DB,REJ built
```

The whole path carries one W3C trace identifier, from the device through both bus hops into the
database. Losing that continuity would make the end-to-end latency unmeasurable, so it is
asserted by an integration test rather than assumed.

---

## 4. Bounded contexts

Contexts talk only through published messages. No context reads another's tables.

```mermaid
flowchart LR
    subgraph IMPL["Implemented"]
        direction TB
        TEL["Telemetry<br/>reception · dedup · storage"]
        SD["Signal Decoding<br/>CAN frames to physical values"]
    end

    subgraph PLAN["Planned"]
        direction TB
        DM["Device Management<br/>registration · health · mode switching"]
        DX["Diagnostics<br/>UDS sessions · DTCs · security access"]
        AN["Analytics<br/>windows · aggregates · anomalies"]
    end

    TEL -->|"RawFramesReceived"| SD
    SD -->|"SignalsDecoded"| TEL
    DM -.->|"ProcessingModeChanged"| TEL
    TEL -.->|"TelemetryReceived"| AN
    DX -.->|"DtcDetected"| AN

    classDef built fill:#dcfce7,stroke:#16a34a,stroke-width:2px,color:#14532d
    classDef planned fill:#f1f5f9,stroke:#94a3b8,stroke-dasharray:5 5,color:#334155
    class TEL,SD built
    class DM,DX,AN planned
```

Inside the monolith each context is its own namespace, and the separation is enforced by
architecture tests rather than by convention. When a context is extracted into a service, the
namespace boundary becomes a project boundary and the same tests keep passing.

---

## 5. Dependency rule

Dependencies point inward only. Verified by NetArchTest on every build.

```mermaid
flowchart TB
    APIP["Api — composition root, HTTP endpoints"]
    INFRA["Infrastructure — EF Core · MQTTnet · MassTransit"]
    APP["Application — use cases, ports as interfaces"]
    DOM["Domain — entities, value objects, no I/O at all"]
    CON["Contracts — versioned message shapes"]

    APIP --> INFRA
    APIP --> APP
    INFRA --> APP
    INFRA --> CON
    APP --> DOM

    classDef built fill:#dcfce7,stroke:#16a34a,stroke-width:2px,color:#14532d
    class APIP,INFRA,APP,DOM,CON built
```

The Domain project references nothing: no projects, no packages, no clock. Time enters through
an injected abstraction so domain behaviour is deterministic under test.

---

## 6. Bench topology

```mermaid
flowchart LR
    ESP["ESP32<br/>GPIO5 to CTX, GPIO4 to CRX"]
    TRX["SN65HVD230<br/>termination ON"]
    MCP["MCP2515 + TJA1050<br/>termination ON"]
    UNO["Arduino Uno<br/>D10 CS, D11 SI, D12 SO, D13 SCK, D2 INT"]
    SOCK["OBD-II socket on the bench"]
    ELM["ELM327 v2.1<br/>reference scanner"]

    ESP ---|"3V3 · GND · CTX · CRX"| TRX
    TRX ---|"CAN-H / CAN-L twisted pair"| MCP
    MCP ---|"SPI"| UNO
    TRX --- SOCK
    SOCK --- ELM

    classDef planned fill:#f1f5f9,stroke:#94a3b8,stroke-dasharray:5 5,color:#334155
    class ESP,TRX,MCP,UNO,SOCK,ELM planned
```

Common ground is mandatory. Total termination measures about 60 Ω, from two 120 Ω resistors in
parallel. In a real vehicle the termination on the transceiver module must be **off**, because
the vehicle bus is already terminated at both ends.
