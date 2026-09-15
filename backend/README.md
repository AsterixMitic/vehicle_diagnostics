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

```bash
dotnet run --project src/AppHost      # Mosquitto + TimescaleDB + Api, with the Aspire dashboard
```

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
