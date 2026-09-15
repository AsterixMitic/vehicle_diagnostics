# ADR-0002: Modular monolith with namespace-isolated bounded contexts

Status: Accepted · Date: 2026-09-15 · Change: add-backend-foundation

## Context

architecture.md §10 mandates a modular monolith as the starting architectural style, with
microservices extracted only for a written reason. Its §21 repository layout shows single
`Domain`/`Application`/`Infrastructure` projects, while the project conventions state "one bounded context per
project" — the two rules needed reconciliation before scaffolding. Premature decomposition would
produce a distributed monolith and undermine the fair-comparison invariant of the research.

## Decision

- The composition root is a single ASP.NET Core minimal-API host, **`src/Api`**. No `Services/*`
  projects are scaffolded until a service is actually extracted (each extraction gets its own ADR).
- Hexagonal layering as single projects: `Domain`, `Application`, `Infrastructure`, `Api`, with the
  dependency rule Domain → nothing, Application → Domain only, Infrastructure/Api → inward.
- Inside each layer project, each bounded context lives under its own root namespace
  (`Domain.Telemetry`, `Application.Telemetry`, …). A shared kernel (typed IDs, `Result`) is the
  only namespace contexts may share.
- All of the above is enforced by an automated architecture test suite (NetArchTest) that must stay
  green in the standard test run; "one bounded context per project" applies in full once services
  are extracted.
- Each context owns its database schema; cross-schema access from another context is forbidden.

## Consequences

- Simple local run and debugging; one process to orchestrate in this phase.
- Extraction later only *tightens* boundaries (namespace rule becomes a project boundary); the
  architecture tests are written so extraction does not require rewriting them.
- Namespace isolation is weaker than a compiler-enforced project boundary — mitigated by the
  CI-blocking architecture tests.

## Alternatives considered

- **Scaffold all five services from §21 now** — rejected: dead structure, contradicts the
  backbone-first rule and §10's extract-on-reason policy.
- **Per-context projects inside the monolith** (e.g. `Domain.Telemetry.csproj`) — rejected:
  diverges from §21's explicit layout and multiplies project count without changing observable
  guarantees, since the architecture tests enforce the same isolation.
