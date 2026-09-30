#!/usr/bin/env bash
# Removes the local cluster with everything in it, including the database volume, which lives
# inside the k3s node container (ADR-0006). The next up.sh starts from an empty database.
#
#   deploy/k3d/down.sh

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
# shellcheck source=deploy/k3d/env.sh
source "$repo_root/deploy/k3d/env.sh"

k3d cluster delete vehicle-diagnostics
rm -f "$KUBECONFIG"
