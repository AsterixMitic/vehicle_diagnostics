# ADR-0003: Initial dependency set for the backend foundation

Status: Accepted · Date: 2026-09-15 · Change: add-backend-foundation

## Context

The project rules forbid adding a NuGet package without an ADR. The foundation increment (solution
skeleton + telemetry ingest walking skeleton) needs the smallest set that satisfies its specs:
MQTT reception, TimescaleDB persistence, enforced architecture rules, observability, and tests.

## Decision

The following packages enter the solution; nothing else does without a further ADR.

| Package | Role | Three-sentence reason |
|---|---|---|
| .NET Aspire (AppHost + ServiceDefaults) | Local orchestration, OTel wiring | Starts broker, database, and host with one command. Centralizes OpenTelemetry, health checks, and resilience defaults. Gives a local trace/metric dashboard without standing up LGTM yet. |
| MQTTnet | MQTT client | The device-to-backend protocol is MQTT per architecture.md §13. Managed client provides auto-reconnect/resubscribe required by the ingest spec. Mature and AOT-friendly. |
| EF Core 10 + Npgsql | Persistence | Relational mapping and migrations for the `telemetry` schema. Npgsql is the standard PostgreSQL/TimescaleDB provider. Dapper hot paths come later, measured, per §6. |
| OpenTelemetry .NET SDK (+ ASP.NET Core/HttpClient/EFCore instrumentation, OTLP exporter) | Traces/metrics/logs | Instrumentation is the measuring instrument of the thesis. First-class .NET support was a platform-selection criterion (ADR-0001). Exports OTLP to the Aspire dashboard now, Alloy/LGTM later. |
| xUnit + FluentAssertions | Unit tests | Standard test stack named by the project rules. Table-driven `[Theory]` support fits protocol/validation testing. FluentAssertions is pinned to the 7.x line: 8.x moved to a commercial license, 7.x stays Apache-2.0. |
| Testcontainers | Integration tests | Hypertable and ON CONFLICT semantics must be tested against real TimescaleDB, and ingest against a real Mosquitto — mocks would lie. Disposable containers keep tests deterministic. |
| NetArchTest | Architecture enforcement | The dependency and context-isolation rules of ADR-0002 must be machine-checked. A rule that is not automatically verified is not a rule (architecture.md §10). |
| Microsoft.Extensions.TimeProvider.Testing (FakeTimeProvider) | Deterministic time in tests | Time enters the domain only via `TimeProvider`. Tests must control it deterministically. |

**Explicitly deferred** (with the reason the deferral is safe): MassTransit/RabbitMQ (no second
consumer exists yet; arrives with the decode increment), CBOR serializer (JSON is the debug format;
CBOR is a *measured* alternative in the evaluation increment), Dapper (optimization decided by
measurement), FluentValidation and YARP (their consumers don't exist yet).

**Dev-only security posture**: the Aspire-managed Mosquitto container allows anonymous local
connections. This is an accepted risk confined to the developer machine; TLS and per-device
identity (architecture.md §16) are a dedicated later increment and block anything leaving the dev
machine.

## Consequences

- Every dependency has a written reason; the ADR is the gate for the next one.
- The hand-rolled `Result<T, TError>` lives in the Domain shared kernel instead of a functional
  library — ~60 lines the language nearly covers.

## Alternatives considered

- **CSharpFunctionalExtensions / LanguageExt** for `Result` — rejected: heavy dependency for a
  small need, and Domain must stay framework-free.
- **MassTransit now** — rejected: speculative moving part with no second party to talk to.
- **Docker Compose instead of Aspire for local runs** — rejected for this phase: Aspire also
  provides the OTel dashboard and service defaults; Compose remains the edge-deployment story.
