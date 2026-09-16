# ADR-0004: Internal message bus is MassTransit 8 over RabbitMQ

Status: Accepted · Date: 2026-09-16 · Change: add-raw-frame-decode

## Context

Bounded contexts communicate only through messages (architecture.md §11, §13); the first
real cross-context flow appears now: the Telemetry context receives raw CAN frames that the
Signal Decoding context must turn into readings, which the Telemetry context then persists.
architecture.md §6 names MassTransit over RabbitMQ, with Kafka as an alternative transport
to be measured. The bus was deliberately deferred (add-backend-foundation, design D4) until
this flow existed, to avoid an unmeasured moving part with no second party.

## Decision

- **MassTransit, 8.x line**, over **RabbitMQ 4**. MassTransit 9 moved to a commercial
  license; 8.x is Apache-2.0, still maintained, and covers everything the architecture asks
  of the bus (consumers, retry, error queues, outbox, sagas, OpenTelemetry propagation).
  Same reasoning that pinned FluentAssertions to 7.x (ADR-0003).
- Message contracts are plain versioned records in `Contracts` (`Contracts.Internal`);
  publishing goes through ports declared in `Application`, implemented in `Infrastructure`.
  Neither `Domain` nor `Application` references the bus library.
- Delivery is at-least-once with a bounded retry policy (1 s, 5 s, 15 s) and MassTransit's
  `_error` queue for exhausted messages. Consumers are idempotent by the envelope's
  `(deviceId, sequence)` identity, which the store already enforces.
- Per-device ordering uses MassTransit's in-process partitioner keyed by device identifier.
  **Precondition:** this guarantee holds only while a consumer runs in a single process.
  Extracting a consumer into its own service, or scaling one, requires revisiting this ADR
  (partitioned transport or single-active-consumer queues).
- The transactional outbox is not enabled yet: nothing in the current flow writes to a
  database before publishing, so there is no atomicity gap to close. It is enabled by
  configuration when a saga or write-then-publish flow arrives (DiagOrchestrator).

## Consequences

- One more container (RabbitMQ with the management plugin) in both orchestration paths,
  health-gated before the host starts.
- Trace context crosses the bus in message headers; MassTransit's activity source joins the
  shared observability defaults so consumer spans are children of the device's trace.
- Kafka remains a measured alternative: MassTransit's rider model lets it be added as a
  second transport for the throughput comparison without touching consumers.

## Alternatives considered

- **MassTransit 9** — rejected: commercial license for a thesis project with no funding.
- **Wolverine, NServiceBus, Rebus** — rejected: architecture.md names MassTransit; the
  author's stated tooling familiarity reduces risk in a fixed timeframe.
- **In-process channel between contexts** — rejected: defers the bus again while the flow
  that justifies it exists; would also hide the network cost the research measures.
- **Kafka first** — rejected as the default: RabbitMQ's routing and per-queue error handling
  fit the low-volume command/event mix; throughput comparison is a later experiment.
