# Vehicle Diagnostics IoT Platform

An IoT system for vehicle diagnostics built on an ESP32, the CAN bus and distributed services.
An embedded gateway reads engine data from a car's OBD-II port and sends it to a server side
that decodes, stores and displays it.

Bachelor thesis: *IoT систем за дијагностику возила заснован на ESP32, CAN магистрали и
дистрибуираним сервисима*, Aleksandar Mitić, Faculty of Electronic Engineering, University of
Niš, 2026.

## What it does

```
vehicle or            ESP32 gateway             edge server / cloud
simulated ECU
┌──────────┐  CAN   ┌──────────────┐  MQTT   ┌───────────┐   ┌─────────────────────┐   ┌─────────────┐
│ engine   │──────▶ │ OBD-II       │───────▶ │ Mosquitto │──▶│ .NET 10 backend     │──▶│ TimescaleDB │
│ ECU      │ 500    │ service 01,  │ QoS 1   └───────────┘   │ ingest · decoding · │   └─────────────┘
└──────────┘ kbit/s │ ISO-TP       │                         │ analytics, RabbitMQ │──▶ REST + SSE API
                    └──────────────┘                         └─────────────────────┘      │
                                                                                 Angular dashboard
```

The research question of the thesis is **where the data should be processed**. Decoding raw
CAN frames can run on the device or on the server, and the choice is a runtime setting that a
command over MQTT changes without reflashing or redeploying:

| Mode | Device publishes | Decoding runs |
|---|---|---|
| P0 | raw OBD-II answers on `tel/raw` | in the backend |
| P2 | decoded signals on `tel/signals` | on the ESP32 |

Both modes use the same message envelope and the same path through the system, and every
message is traced from the device to the database write, so latency, throughput and losses can
be compared on the same input.

## Repository layout

| Folder | Contents |
|---|---|
| `firmware/gateway` | ESP32 gateway in C on ESP-IDF: CAN link, ISO-TP, OBD-II service 01, MQTT publishing |
| `firmware/sim-ecu` | Simulated engine ECU on an Arduino Uno with an MCP2515, for repeatable bench runs |
| `firmware/tools/sil` | Software-in-the-loop bench: the gateway's scheduler against the simulator, no hardware |
| `backend` | Modular monolith on .NET 10 with hexagonal architecture: telemetry, signal decoding and analytics contexts |
| `frontend` | Angular dashboard: devices, live values, history and the comparison of the two modes |
| `deploy` | Docker Compose stack and a Helm chart for Kubernetes (k3s) |
| `edge` | Mosquitto broker configuration |
| `experiments` | Virtual device used as a load generator, capture fixtures and a recorded drive profile |
| `docs` | System specification (`architecture.md`), architecture decision records and diagrams |

## Running it

The whole server side with a virtual device that publishes at 1 Hz, in containers:

```bash
cd deploy/compose
docker compose --profile demo up -d --build
```

| What | Address |
|---|---|
| Dashboard | http://localhost:4280 |
| API and health | http://localhost:5080 |
| Traces, metrics and logs | http://localhost:18888 |
| MQTT broker | localhost:1883 |

Development with .NET Aspire, the tests and the remaining commands are in
[`backend/README.md`](backend/README.md); the Kubernetes path is in
[`deploy/README.md`](deploy/README.md).

## Hardware

- Gateway: ESP32 development board with an SN65HVD230 CAN transceiver on the OBD-II connector.
- Simulated ECU: Arduino Uno with an MCP2515 CAN module.

Wiring, building and the bench procedure are in [`firmware/README.md`](firmware/README.md).
On a vehicle the gateway sends only OBD-II service 01 read requests: no write, reset or
routine-control services.

## Tests

```bash
cd backend && dotnet test VehicleDiagnostics.slnx    # unit, architecture and integration tests
make -C firmware/gateway/test/host                   # firmware tests on a development machine
make -C firmware/sim-ecu/test/host                   # simulator tests
```

The integration tests start their own containers and need Docker.
