#!/usr/bin/env bash
# Installs the pinned Kubernetes tools into <repo>/.tools/bin (gitignored), next to the
# repo-local .NET SDK and Node, so nothing on the host system changes (ADR-0006).
#
#   deploy/k3d/install-tools.sh
#   export PATH="$PWD/.tools/bin:$PATH" KUBECONFIG="$PWD/.tools/kubeconfig"
#
# Every download is checked against a SHA-256 pinned here, taken from the checksum file the
# project publishes with the release; a mismatch aborts before anything is installed.

set -euo pipefail

KUBECTL_VERSION=v1.36.5
KUBECTL_SHA256=33bc88a24c3b09cf55bfd59e6ca977881b61fc3f25ab65bc80c65b430248648e
HELM_VERSION=v4.3.0
HELM_SHA256=86584a54def73570558f66f5111cc53dfed56689637ae32c1201205d494f54fb
K3D_VERSION=v5.9.0
K3D_SHA256=06d8f25bc3a971c4eb29e0ff08429b180402db0f4dec838c9eac427e296800a0

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
bin_dir="$repo_root/.tools/bin"

if [[ "$(uname -s)-$(uname -m)" != "Linux-x86_64" ]]; then
    echo "error: checksums are pinned for linux-amd64 only; install kubectl $KUBECTL_VERSION," \
        "helm $HELM_VERSION and k3d $K3D_VERSION by hand on this platform" >&2
    exit 1
fi

work_dir="$(mktemp -d)"
trap 'rm -rf "$work_dir"' EXIT

# download <url> <file> <sha256>
download() {
    local url="$1" file="$2" expected="$3"
    echo "downloading $url"
    curl -fsSL --retry 3 -o "$work_dir/$file" "$url"
    if ! echo "$expected  $work_dir/$file" | sha256sum --check --status; then
        echo "error: checksum mismatch for $file (expected $expected)" >&2
        exit 1
    fi
}

download "https://dl.k8s.io/release/$KUBECTL_VERSION/bin/linux/amd64/kubectl" \
    kubectl "$KUBECTL_SHA256"
download "https://get.helm.sh/helm-$HELM_VERSION-linux-amd64.tar.gz" \
    helm.tar.gz "$HELM_SHA256"
download "https://github.com/k3d-io/k3d/releases/download/$K3D_VERSION/k3d-linux-amd64" \
    k3d "$K3D_SHA256"

tar -xzf "$work_dir/helm.tar.gz" -C "$work_dir" linux-amd64/helm

mkdir -p "$bin_dir"
install -m 0755 "$work_dir/kubectl" "$bin_dir/kubectl"
install -m 0755 "$work_dir/linux-amd64/helm" "$bin_dir/helm"
install -m 0755 "$work_dir/k3d" "$bin_dir/k3d"

echo "installed into $bin_dir:"
"$bin_dir/kubectl" version --client | head -1
"$bin_dir/helm" version --short
"$bin_dir/k3d" version | head -1
