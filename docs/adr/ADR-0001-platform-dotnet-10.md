# ADR-0001: Backend platform is .NET 10 LTS / C# 14

Status: Accepted · Date: 2026-09-15 · Change: add-backend-foundation

## Context

The backend of the vehicle-diagnostics IoT platform needs a runtime and language that will remain
supported through the thesis defense, has mature libraries for every architectural need (MQTT,
messaging, ORM, observability), and that the author can work in productively within a constrained
timeframe. `architecture.md` v2.0 replaced the earlier Rust-based specification (v1.0) with a
.NET-based one. An older note in the project conventions still said ".NET 9"; the two had to be reconciled.

## Decision

The backend targets **.NET 10 (LTS)** with **C# 14**, using the `.slnx` solution format and a
`global.json` pinning the SDK. The project conventions are corrected to match.

## Consequences

- Support window until November 2028 — safely past the defense (.NET 8/9 leave support in
  November 2026, before it).
- First-class OpenTelemetry support, which is the measuring instrument of the thesis.
- Native AOT improvements in .NET 10 keep the optional JIT-vs-AOT edge comparison (architecture.md
  §6) available without a platform change.
- Developers and CI must install a .NET 10 SDK; `global.json` makes any mismatch an explicit error.

## Alternatives considered

- **.NET 9** — rejected: out of support November 2026, i.e. before the thesis defense.
- **Rust** (specification v1.0) — rejected in architecture.md v2.0: less author experience, higher
  implementation risk in a fixed timeframe; the type-safety arguments (newtype, typestate) carry
  over to C# via `readonly record struct` and generic phantom-state types.
