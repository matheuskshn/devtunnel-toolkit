#!/usr/bin/env bash
set -euo pipefail

# Install only verified upstream release artifacts, never a mutable install script.
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
    echo 'This installer supports Linux x86_64; install the pinned tools manually on other hosts.' >&2
    exit 2
fi
destination="${1:?Pass a dedicated output directory}"
mkdir -p "$destination"
download_dir="$(mktemp -d)"
trap 'rm -f "$download_dir/trivy.tar.gz" "$download_dir/gitleaks.tar.gz"; rmdir "$download_dir"' EXIT
curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
    https://github.com/aquasecurity/trivy/releases/download/v0.74.0/trivy_0.74.0_Linux-64bit.tar.gz \
    -o "$download_dir/trivy.tar.gz"
echo "2ae6fe3ee734b7fdf11335663e18c75ea12dccc76062f09f164a3b0f8be4371a  $download_dir/trivy.tar.gz" | sha256sum -c -
tar -xzf "$download_dir/trivy.tar.gz" -C "$destination" trivy
curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
    https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_x64.tar.gz \
    -o "$download_dir/gitleaks.tar.gz"
echo "551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb  $download_dir/gitleaks.tar.gz" | sha256sum -c -
tar -xzf "$download_dir/gitleaks.tar.gz" -C "$destination" gitleaks
