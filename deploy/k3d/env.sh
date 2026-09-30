# shellcheck shell=bash
# Source this file to use the repo-local tools against the local cluster:
#
#   source deploy/k3d/env.sh
#
# Puts .tools/bin first on PATH, points kubectl and helm at the cluster's kubeconfig, and uses
# Docker Desktop's socket when DOCKER_HOST is unset and that socket exists.

_vd_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export PATH="$_vd_root/.tools/bin:$PATH"
export KUBECONFIG="$_vd_root/.tools/kubeconfig"
if [[ -z "${DOCKER_HOST:-}" && -S "$HOME/.docker/desktop/docker.sock" ]]; then
    export DOCKER_HOST="unix://$HOME/.docker/desktop/docker.sock"
fi
unset _vd_root
