# Deployment

Three ways to run the same system, with the same images and configuration:

| Path | Where | For |
|---|---|---|
| .NET Aspire | `backend/src/AppHost` | Day-to-day development (see `backend/README.md`) |
| Docker Compose | `compose/` | Containers without a cluster, no .NET SDK needed (see `backend/README.md`) |
| Kubernetes (k3s + Helm) | `helm/`, `k3d/` | The orchestrated cloud layer of architecture.md §4 (ADR-0006) |

## Kubernetes

`helm/vehicle-diagnostics/` is one chart that installs the whole stack as one release:
TimescaleDB, RabbitMQ, Mosquitto, the host, the dashboard and, optionally, the Aspire dashboard
and a virtual device. `k3d/` runs it on a local single-node k3s cluster inside Docker.

### Tools

`kubectl`, `helm` and `k3d` are pinned and installed repo-local into `.tools/bin` (gitignored),
checksum-verified:

```bash
deploy/k3d/install-tools.sh
source deploy/k3d/env.sh          # PATH, KUBECONFIG=.tools/kubeconfig, Docker Desktop socket
```

The kubeconfig and the kubectl and Helm caches stay in `.tools/` as well; `~/.kube/config` is
never touched.

### Start and stop

The cluster publishes the Compose ports, and a small Docker VM holds only one of the two
stacks, so stop Compose first (its data volume is kept):

```bash
docker compose -f deploy/compose/docker-compose.yml --profile demo down

deploy/k3d/up.sh                                    # build, create cluster, install, smoke test
deploy/k3d/up.sh --set virtualDevice.enabled=true   # ... plus a virtual device (P2, 1 Hz)
deploy/k3d/down.sh                                  # delete the cluster, including all data
```

`up.sh` is idempotent: run it again after a code change and it rebuilds the images and rolls
only the pods whose image changed. Every argument except `--no-build` goes to
`helm upgrade --install`, and values not given return to the chart defaults.

| Endpoint | URL |
|---|---|
| Thesis dashboard (ingress) | `http://localhost:4280` |
| MQTT broker (devices outside the cluster) | `localhost:1883` |
| Aspire dashboard (traces, metrics, logs) | `http://localhost:18888` |
| OTLP gRPC for hosts outside the cluster | `localhost:18889` |
| RabbitMQ management | `kubectl -n vehicle-diagnostics port-forward svc/rabbitmq 15672`, then `http://localhost:15672` |

### Operating the release

```bash
source deploy/k3d/env.sh
kubectl -n vehicle-diagnostics get pods
kubectl -n vehicle-diagnostics top pods             # memory and CPU per component
helm test vehicle-diagnostics -n vehicle-diagnostics --logs

# Switch the virtual device's processing mode; only its pod is replaced.
deploy/k3d/up.sh --no-build --set virtualDevice.enabled=true --set virtualDevice.processingMode=P0
```

Chart checks that need no cluster:

```bash
helm lint --strict deploy/helm/vehicle-diagnostics
deploy/k3d/check-resources.sh                       # requests + memory limits, no CPU limits
```

### What differs from Compose

- **Ordering** — the host's init container waits until the broker, the database and the bus
  accept connections (Compose: `depends_on: service_healthy`).
- **Health** — readiness and liveness probes. The host leaves service when a dependency is lost,
  but is restarted only when its own process hangs. The dashboard's readiness no longer includes
  the proxy to the host; `helm test` checks that path.
- **One host, replaced not rolled** — two instances would both ingest every message. Messages
  published while the host is being replaced are lost, as on Compose, because MQTT sessions are
  not persistent.
- **Resources** — requests and memory limits everywhere, no CPU limits (throttling would add
  latency to the measured stages).
- **Traces** name the pod, namespace and node (`k8s.*` resource attributes).

### Measured on the development machine

Virtual device in P2 at 1 Hz, two minutes of steady state each, same day (2026-09-30), the
cluster freshly installed, both paths checking the bus over TCP:

| Path | e2e p50 / p95 / p99 ms | backend p50 / p95 ms |
|---|---|---|
| Compose | 7.12 / 8.10 / 8.53 | 6.07 / 7.04 |
| k3d | 6.18 / 34.92 / 70.23 | 5.26 / 34.07 |

Medians are comparable. The cluster's tail is heavier, and after a series of disruptive tests
it was heavier still (p95 about 60 ms). Compare processing modes within one path; a difference
across paths measures the platform, not where processing runs.

### Memory

k3s and its add-ons need about 500 MB and the stack about 400 MB more. With Docker Desktop's
default of 1.6 GB this fits only with Compose stopped. If pods stay `Pending` or are
`OOMKilled`, raise the VM's memory in Docker Desktop, or start without the Aspire dashboard:

```bash
deploy/k3d/up.sh --set aspireDashboard.enabled=false
```

### A real k3s host

The chart runs unchanged on a k3s node. Load the images with `k3s ctr images import` or push them
to a registry and set `api.image`, `web.image` and `virtualDevice.image`. Set
`timescaledb.persistence.storageClass` on a cluster without `local-path`. Install one release per
namespace, because the Service names are fixed to the Compose names that the dashboard's proxy
and the host's configuration use.
