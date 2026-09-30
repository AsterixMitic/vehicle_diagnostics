#!/usr/bin/env bash
# Checks the chart's resource policy (ADR-0006, design D8) on the rendered manifests: every
# container and init container, with every optional component and the test pod rendered,
# has CPU and memory requests and a memory limit, and none has a CPU limit.
#
#   deploy/k3d/check-resources.sh [extra helm template flags]

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export PATH="$repo_root/.tools/bin:$PATH"

helm template check "$repo_root/deploy/helm/vehicle-diagnostics" \
    --set aspireDashboard.enabled=true --set virtualDevice.enabled=true "$@" |
    python3 -c '
import sys, yaml

failures, checked = [], 0
for doc in yaml.safe_load_all(sys.stdin):
    if not doc:
        continue
    spec = doc.get("spec", {})
    pod = spec if doc["kind"] == "Pod" else spec.get("template", {}).get("spec")
    if not pod:
        continue
    for container in pod.get("initContainers", []) + pod.get("containers", []):
        checked += 1
        where = doc["kind"] + "/" + doc["metadata"]["name"] + ":" + container["name"]
        resources = container.get("resources", {})
        requests, limits = resources.get("requests", {}), resources.get("limits", {})
        for missing in [k for k in ("cpu", "memory") if k not in requests]:
            failures.append(f"{where}: no {missing} request")
        if "memory" not in limits:
            failures.append(f"{where}: no memory limit")
        if "cpu" in limits:
            failures.append(f"{where}: has a CPU limit")

for failure in failures:
    print(failure)
print(f"{checked} containers checked, {len(failures)} violations")
sys.exit(1 if failures or not checked else 0)
'
