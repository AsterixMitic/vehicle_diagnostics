# Vehicle diagnostics dashboard

Angular 22 dashboard for the vehicle-diagnostics platform (ADR-0005): live vehicle data, signal
history and the evaluation of processing modes P0–P3. It reads only the backend's public `/api`.

Conventions and commands are in [`CONVENTIONS.md`](CONVENTIONS.md). In short, with the repo-local Node on
`PATH` and the backend running on `localhost:5080`:

```bash
pnpm install
pnpm start
```

and open <http://localhost:4200>.
