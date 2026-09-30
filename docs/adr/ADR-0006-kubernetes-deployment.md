# ADR-0006: Kubernetes deployment with a hand-written Helm chart on k3s

Status: Accepted · Date: 2026-09-30 · Change: add-kubernetes-deployment

## Context

architecture.md places the cloud layer on Kubernetes, specifically k3s (§4). It lists k3s + Helm
as the orchestration choice (§5 "Orkestracija", §6 stack), names a Helm chart as the
infrastructure-as-code artifact (§19 "Infrastruktura kao kod"), reserves `deploy/helm/` (§21),
and schedules the work in phase F8 (§22). The backbone that F8 waits on exists: the monolith
with both processing paths, the evaluation API and the dashboard. They run under Aspire and
Docker Compose.

The open questions were how to package the stack, how to run k3s on a development machine whose
Docker Desktop VM has about 1.6 GB of memory, and which Kubernetes defaults would distort the
measurements the thesis is about.

## Decision

- **One hand-written Helm chart**, `deploy/helm/vehicle-diagnostics/`, installs the whole
  current stack as one release: TimescaleDB, RabbitMQ, Mosquitto, the monolith host, the
  dashboard, and optionally the Aspire dashboard (OTLP sink) and the virtual device. It uses
  **the same images and configuration as the Compose path** and no third-party subcharts.
- **Service names equal the Compose service names** (`api`, `web`, `timescaledb`, ...), so the
  dashboard image's `api:8080` upstream and the host's environment work unchanged. This implies
  **one release per namespace** (`vehicle-diagnostics`).
- **The host runs as exactly one replica with the `Recreate` strategy.** Without MQTT shared
  subscriptions, two instances, including the overlap of a rolling update, would ingest every
  message twice and double the traffic recorded by the evaluation.
- **Start ordering by an init container** that waits until the broker, the database and the bus
  accept connections. Readiness and liveness probes carry the meaning of the Compose health
  checks. The host's liveness excludes its dependencies.
- **Requests and memory limits on every container, and no CPU limits.** CFS throttling would
  add latency to exactly the stages the thesis measures.
- **Exposure through the k3s built-ins**: Traefik ingress for the dashboard, ServiceLB
  `LoadBalancer` services for MQTT and the Aspire dashboard. The ports are the ones Compose
  uses (4280, 1883, 18888, 18889), so devices and browsers are configured identically on both
  paths.
- **k3d runs k3s locally.** One server node on `rancher/k3s:v1.36.4-k3s1` (stable channel) runs
  inside Docker. Images built from source are imported into it instead of pushed to a registry.
  `kubectl` 1.36, Helm 4.3.0 and k3d 5.9.0 are installed repo-local in `.tools/` (gitignored),
  pinned and checksum-verified, like the .NET SDK and Node.
- **A Helm test hook** checks the host's readiness and the dashboard's proxy from inside the
  cluster, and the start script runs it after every install.

## Consequences

- Kubernetes is a third way to run the same system, next to Aspire (daily development) and
  Compose (containers without a cluster). It adds no new application dependency and no code
  change.
- The Compose and Kubernetes paths cannot run at the same time: they share ports on purpose, and
  the VM's memory would not hold both. The start script refuses to run while Compose holds the
  ports.
- Kubernetes-specific measurements become possible: per-component resource use from
  `metrics-server` (`kubectl top`), and the traces carry pod names from the downward API.
- Replacing the host loses the messages published while no subscriber exists, as on Compose,
  because MQTT sessions are not persistent. The deployment does not hide this. Store-and-forward
  is a separate F8 item.
- The chart is single-node in practice: TimescaleDB uses k3s's node-local `local-path` volumes.
  A real cluster selects another StorageClass through a value.
- The Mosquitto configuration exists twice (`edge/mosquitto/mosquitto.conf` and the chart's
  values), because Helm cannot read files outside the chart. It is two lines, with comments
  pointing each copy to the other.

## Alternatives considered

- **Bitnami (or other) subcharts for PostgreSQL and RabbitMQ.** Rejected: different images than
  Compose, so the two paths would stop being comparable. The free Bitnami catalog moved to a
  legacy repository in 2025. And the subcharts bring hundreds of values for single-instance dev
  services.
- **Release-prefixed names with an nginx override in the chart.** Rejected: it duplicates
  frontend configuration in the chart, and the copies would drift.
- **Rolling updates, or several host replicas.** Rejected until the host consumes through MQTT
  shared subscriptions. Duplicate ingestion would corrupt the evaluation's traffic numbers.
- **Crash-until-ready instead of an init container.** Rejected: the schema initialization throws,
  the pod backs off exponentially, and startup time depends on restart timing.
- **CPU limits for "production hygiene".** Rejected for this system: throttling is a latency
  source unrelated to where processing runs, which is the research question.
- **kind or minikube.** Rejected: architecture.md names k3s, and k3d runs the real k3s binary.
  **Native k3s**: needs root and changes the host. The chart runs there unchanged when needed.
- **Raw manifests or Kustomize instead of Helm.** Rejected: architecture.md names Helm, and the
  optional components and per-environment values (images, passwords, storage class, device mode)
  are what a values file is for.
