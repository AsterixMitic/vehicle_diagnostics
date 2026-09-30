#!/usr/bin/env bash
# One command from nothing to a verified release on a local k3s cluster (ADR-0006):
# builds the three images from source, creates the k3d cluster if needed, imports the images,
# installs or upgrades the release, and runs its smoke test.
#
#   deploy/k3d/up.sh                                         # the stack
#   deploy/k3d/up.sh --set virtualDevice.enabled=true        # ... plus a virtual device
#   deploy/k3d/up.sh --no-build --set virtualDevice.processingMode=P0
#
# Every argument except --no-build goes to `helm upgrade --install`. Values not given return to
# the chart defaults on every run, so the release always matches the command line.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=deploy/k3d/env.sh
source "$repo_root/deploy/k3d/env.sh"

cluster=vehicle-diagnostics
namespace=vehicle-diagnostics
release=vehicle-diagnostics
chart="$repo_root/deploy/helm/vehicle-diagnostics"
ports=(4280 1883 18888 18889)

build=true
helm_args=()
for arg in "$@"; do
    if [[ "$arg" == --no-build ]]; then build=false; else helm_args+=("$arg"); fi
done

for tool in kubectl helm k3d; do
    if [[ ! -x "$repo_root/.tools/bin/$tool" ]]; then
        echo "error: $tool is missing; run deploy/k3d/install-tools.sh first" >&2
        exit 1
    fi
done
docker info >/dev/null 2>&1 || { echo "error: the Docker daemon is not reachable" >&2; exit 1; }

cluster_exists() { k3d cluster get "$cluster" >/dev/null 2>&1; }

# The cluster publishes the Compose ports, and the VM's memory holds only one of the two.
if ! cluster_exists; then
    if [[ -n "$(docker ps -q --filter label=com.docker.compose.project=vehiclediagnostics)" ]]; then
        echo "error: the Compose stack is running; stop it first:" >&2
        echo "  docker compose -f deploy/compose/docker-compose.yml --profile demo down" >&2
        exit 1
    fi
    for port in "${ports[@]}"; do
        if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
            echo "error: localhost:$port is already in use; the cluster needs ports ${ports[*]}" >&2
            exit 1
        fi
    done
fi

# image name -> "dockerfile context"
declare -A sources=(
    [api]="backend/src/Api/Dockerfile ."
    [web]="frontend/Dockerfile frontend"
    [virtual-device]="experiments/load-generator/VirtualDevice/Dockerfile ."
)
declare -A tags=()

# Built before the cluster exists on a first run: an SDK build next to a running cluster can
# exhaust a small Docker VM.
for image in api web virtual-device; do
    ref="vehiclediagnostics/$image"
    if [[ "$build" == true ]]; then
        read -r dockerfile context <<<"${sources[$image]}"
        echo "==> building $ref"
        docker build -q -t "$ref:dev" -f "$repo_root/$dockerfile" "$repo_root/$context" >/dev/null
    fi
    # Tag by content: an upgrade rolls a pod only when its image actually changed.
    id="$(docker image inspect -f '{{.Id}}' "$ref:dev")"
    tags[$image]="dev-${id#sha256:}"
    tags[$image]="${tags[$image]:0:16}"
    docker tag "$ref:dev" "$ref:${tags[$image]}"
done

if ! cluster_exists; then
    echo "==> creating k3d cluster $cluster"
    k3d cluster create --config "$repo_root/deploy/k3d/cluster.yaml"
fi
mkdir -p "$(dirname "$KUBECONFIG")"
k3d kubeconfig get "$cluster" >"$KUBECONFIG"
chmod 600 "$KUBECONFIG"

echo "==> importing images"
k3d image import -c "$cluster" \
    "vehiclediagnostics/api:${tags[api]}" \
    "vehiclediagnostics/web:${tags[web]}" \
    "vehiclediagnostics/virtual-device:${tags[virtual-device]}"

echo "==> installing release $release"
helm upgrade --install "$release" "$chart" \
    --namespace "$namespace" --create-namespace \
    --set "api.image.tag=${tags[api]}" \
    --set "web.image.tag=${tags[web]}" \
    --set "virtualDevice.image.tag=${tags[virtual-device]}" \
    --wait --timeout 10m \
    "${helm_args[@]}"

echo "==> smoke test"
helm test "$release" --namespace "$namespace" --logs

cat <<EOF

Ready. In this shell: source deploy/k3d/env.sh
  Dashboard         http://localhost:4280
  MQTT broker       localhost:1883
  Aspire dashboard  http://localhost:18888   (unless disabled)
  kubectl -n $namespace get pods
EOF
