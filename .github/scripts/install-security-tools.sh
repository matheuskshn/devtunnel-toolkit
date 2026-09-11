#!/usr/bin/env bash
set -euo pipefail

# Install only verified upstream release artifacts, never a mutable install script.
if [[ "$(uname -s)" != Linux ]]; then
    echo 'This installer supports Linux only.' >&2
    exit 2
fi
case "$(uname -m)" in
    x86_64)
        trivy_arch=64bit
        trivy_checksum=2ae6fe3ee734b7fdf11335663e18c75ea12dccc76062f09f164a3b0f8be4371a
        gitleaks_arch=x64
        gitleaks_checksum=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb
        ;;
    aarch64)
        trivy_arch=ARM64
        trivy_checksum=b94ce1976bbf3c15b514b605ee88be7c6d94a29be2302847ff01cb794d47aad5
        gitleaks_arch=arm64
        gitleaks_checksum=e4a487ee7ccd7d3a7f7ec08657610aa3606637dab924210b3aee62570fb4b080
        ;;
    *) echo 'Unsupported architecture; expected x86_64 or aarch64.' >&2; exit 2 ;;
esac
destination="${1:?Pass a dedicated output directory}"
mkdir -p "$destination"
download_dir="$(mktemp -d)"
trap 'rm -f "$download_dir/trivy.tar.gz" "$download_dir/gitleaks.tar.gz"; rmdir "$download_dir"' EXIT
curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
    "https://github.com/aquasecurity/trivy/releases/download/v0.74.0/trivy_0.74.0_Linux-${trivy_arch}.tar.gz" \
    -o "$download_dir/trivy.tar.gz"
echo "$trivy_checksum  $download_dir/trivy.tar.gz" | sha256sum -c -
tar -xzf "$download_dir/trivy.tar.gz" -C "$destination" trivy
curl --fail --silent --show-error --location --retry 3 --connect-timeout 15 --max-time 180 --proto '=https' --tlsv1.2 \
    "https://github.com/gitleaks/gitleaks/releases/download/v8.30.1/gitleaks_8.30.1_linux_${gitleaks_arch}.tar.gz" \
    -o "$download_dir/gitleaks.tar.gz"
echo "$gitleaks_checksum  $download_dir/gitleaks.tar.gz" | sha256sum -c -
tar -xzf "$download_dir/gitleaks.tar.gz" -C "$destination" gitleaks
