# VehicleDiagnostics backend

Modular monolith for the vehicle-diagnostics IoT platform (see `../docs/architecture.md`,
the single source of truth, and `../docs/adr/` for decisions).

## Prerequisites

- .NET 10 SDK — pinned by `global.json`. If missing, install locally from the repo root:
  `curl -fsSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --quality ga --install-dir .dotnet`
  and put `<repo>/.dotnet` on `PATH` (also set `DOTNET_ROOT=<repo>/.dotnet`).
- Docker — required for the Aspire-managed Mosquitto/TimescaleDB containers and the
  Testcontainers-based integration tests.

## Quality gates

Run all three from `backend/` before considering any change done:

```bash
dotnet build VehicleDiagnostics.slnx                          # zero warnings tolerated
dotnet format VehicleDiagnostics.slnx --verify-no-changes     # formatting
dotnet test VehicleDiagnostics.slnx                           # unit + architecture + integration
```

## Running the stack

Two equivalent paths. Aspire is the day-to-day one; Compose needs no .NET SDK on the machine.

```bash
dotnet run --project src/AppHost      # Mosquitto + TimescaleDB + Api, with the Aspire dashboard
```

```bash
cd ../deploy/compose
docker compose up -d --build                  # broker, database, dashboard, host (built from source)
docker compose --profile demo up -d --build   # ... plus a virtual device publishing at 1 Hz
docker compose ps                             # every container reports a health status
docker compose --profile demo down -v         # stop and remove everything, including data
```

| Endpoint | Compose | Aspire |
|---|---|---|
| Host readiness | `http://localhost:5080/health` | `http://localhost:5080/health` |
| MQTT broker | `localhost:1883` | `localhost:1883` |
| RabbitMQ management (internal bus) | `http://localhost:15672` (guest/guest) | URL in the dashboard |
| Dashboard (traces, metrics, logs) | `http://localhost:18888` | URL printed by the AppHost |

The virtual device publishes in processing mode **P2** (decoded signals) by default, or **P0**
(raw OBD-II frames, decoded by the backend over the internal bus):

```bash
DEVICE_MODE=P0 docker compose --profile demo up -d                 # containerized, P0
DEVICE_MODE=P2 DEVICE_PREFIX=esp32p2 docker compose --profile demo up -d --no-deps virtual-device
PROCESSING_MODE=P0 dotnet run --project ../experiments/load-generator/VirtualDevice   # from the host
```

Both modes derive from one OBD-II encoding of the drive cycle, so a P0 run and a P2 run persist
identical values for the same sequence number — the comparison the thesis is built on.

## Layout

| Project | Role |
|---|---|
| `src/Domain` | Entities, value objects, domain events — no I/O, references nothing |
| `src/Application` | Use cases and ports — references Domain only |
| `src/Contracts` | Versioned device↔backend message contracts (leaf, shared with tooling) |
| `src/Infrastructure` | Adapters: EF Core/TimescaleDB, MQTT transport |
| `src/Api` | Modular monolith host (composition root) |
| `src/ServiceDefaults` | OpenTelemetry, health checks, resilience defaults |
| `src/AppHost` | .NET Aspire local orchestration |
| `tests/Architecture.Tests` | NetArchTest rules — the layering above is enforced, not suggested |
