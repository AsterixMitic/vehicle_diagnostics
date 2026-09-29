# ADR-0005: Angular dashboard for evaluation and live data

Status: Accepted · Date: 2026-09-29 · Change: add-web-dashboard

## Context

architecture.md §20 excluded a frontend as a contribution of the thesis, and §15 assigned
visualization to Grafana. The author has since decided that the thesis needs its own dashboard:
the evaluation of processing modes P0–P3 (the content of chapter 6) must be shown and exported
from one place at the defense, alongside live vehicle data and, in later phases, device
monitoring and diagnostic commands. Grafana remains the tool for operating the system (traces,
metrics, logs), which is a different audience from the evaluation.

The read side it needs exists since add-evaluation-api: devices, latest values, bucketed
history, evaluation summary, series and CSV export, and a Server-Sent Events stream.

## Decision

- A single-page application in `frontend/`, built with **Angular 22** and its CLI, using
  standalone components, zoneless change detection, signals, `httpResource`, the built-in
  control flow and lazy-loaded routes. Routes are English (`/live`, `/history`, `/evaluation`,
  `/devices`); interface text is Serbian Cyrillic with English technical terms, matching the
  thesis, so screenshots go into it unchanged.
- **Angular Material 22** for components and theming (Material 3, following the system's light
  or dark preference).
- **Apache ECharts 6** through **ngx-echarts 22** for charts, importing only the chart types used.
  Chosen over Chart.js for native box plots and large time series, and over uPlot for breadth.
- **Vitest** (the Angular 22 default) for unit and component tests; **angular-eslint** and
  **Prettier** as mandatory checks, the counterpart of `dotnet format` on the backend.
- **pnpm** as package manager, versions pinned by the lockfile.
- Served in containers by **nginx**, which also proxies `/api` to the backend, so the browser
  talks to one origin and no CORS configuration exists.
- architecture.md §6, §15 and §20 are updated to say so; Grafana keeps the operational role.

## Consequences

- One more toolchain (Node, pnpm) for developers and CI, and one more container in Compose.
- The dashboard is a client of the public read API only; it never reads the database, which
  keeps it replaceable and keeps the bounded contexts closed.
- The live stream goes through nginx, which must not buffer it (`proxy_buffering off`) and must
  keep idle connections open long enough; this is configuration, recorded in the change.
- Browser end-to-end tests are not part of this decision; live verification is done by driving
  the running application in a headless browser. Playwright can be proposed later with its own
  reason.

## Alternatives considered

- **Grafana only** — rejected by the author: the evaluation should be shown and exported from
  the thesis's own application, and Grafana dashboards are awkward for per-range comparison and
  CSV export for the thesis tables.
- **React or Vue** — rejected: the author chose Angular; its opinionated structure (routing, DI,
  forms, testing in one CLI) also reduces decisions in a fixed timeframe.
- **Blazor WebAssembly** — rejected: would share C# with the backend, but charting and component
  ecosystems are thinner and the download is heavier; the API boundary makes shared code
  unnecessary.
- **No component library** — rejected: tables, date pickers and responsive layout are exactly the
  commodity parts a library should provide.
