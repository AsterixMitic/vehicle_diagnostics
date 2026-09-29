# Frontend conventions

The thesis dashboard (ADR-0005): an Angular 22 client of the backend's public `/api`. It shows
live vehicle data, signal history and the per-mode evaluation. `docs/architecture.md` still applies; this file adds what is specific to the frontend.

## Toolchain

The system Node is below the Angular 22 minimum. Use the repo-local Node 24 LTS in `../.node/`:

```bash
export PATH="$PWD/../.node/bin:$PATH"   # from frontend/
pnpm install
pnpm start          # dev server on :4200, /api proxied to http://localhost:5080
pnpm run build      # production build, strict budgets
pnpm test           # Vitest, single run
pnpm run lint       # angular-eslint
pnpm run format:check
```

A change is not done until `build`, `test`, `lint` and `format:check` all pass.

## Boundaries

- **Only the public `/api`.** No database access, no knowledge of backend internals. If the
  dashboard needs something the API does not offer, that is a backend change, proposed and made
  in a backend change.
- **Features never import each other.** `features/*` depend on `core/` and `shared/` only and link
  to each other by URL.
- **No package without an ADR.** The dependencies of ADR-0005 are the allowed set.

## Angular conventions

- Standalone components only, no NgModules. Zoneless change detection; every component is
  `ChangeDetectionStrategy.OnPush`.
- State in signals (`signal`, `computed`, `linkedSignal`); inputs and outputs through `input()`,
  `output()` and `model()`; dependencies through `inject()`. No decorators for inputs, no
  constructor injection.
- HTTP reads through `httpResource` keyed by a computed request, so a changed selection cancels
  the previous request. RxJS only where a stream is genuinely a stream.
- Built-in control flow (`@if`, `@for` with `track`, `@switch`, `@defer`), never `*ngIf`/`*ngFor`.
- Every route is lazy (`loadComponent`). Page selection lives in query parameters bound as
  inputs, so every view can be reloaded and linked.
- Strict TypeScript and strict templates. No `any`; no non-null assertion (`!`) without a
  comment saying why it holds.
- File names follow the 2025 style guide (`live.ts`, `live.html`, `live.spec.ts`).

## Language

- Code, identifiers, comments, commit messages: **English**. Route paths: **English**.
- Interface text: **Serbian Cyrillic**, keeping English technical terms and signal identifiers
  (`engine_rpm`, P0–P3, p95) as they are, so screenshots go into the thesis unchanged.
- Numbers and dates are formatted with the `sr-RS` locale (decimal comma). An absent value is
  shown as `—`, never as `0`.

## Testing

- Pure logic (ranges, URL mapping, formatting, buffers) gets Vitest unit tests.
- Components that render data get TestBed tests against a stubbed API, covering loading, empty
  and error states.
- Live behavior is verified against the running stack in a headless browser, not mocked.
