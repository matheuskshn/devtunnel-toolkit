#!/usr/bin/env bash
set -euo pipefail

mode="${1:-}"
if [[ ( "$#" -ne 1 && "$#" -ne 3 ) || ( "$mode" != --enable && "$mode" != --disable ) ]]; then
  printf '%s\n' 'Usage: configure-startup.sh --enable|--disable [--data-dir ABSOLUTE_PATH]' >&2
  exit 2
fi
if [[ "$(id -u)" -eq 0 ]]; then
  printf '%s\n' 'Run this installer as your regular desktop user, not as root.' >&2
  exit 1
fi
package_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd -P)"
agent_path="$package_dir/agent/DevTunnelToolkit.Agent"
if [[ ! -x "$agent_path" ]]; then
  printf '%s\n' 'The packaged agent executable is missing.' >&2
  exit 1
fi
shift
# Reuse the same ownership checks, quoting and rollback used by the interface.
exec "$agent_path" --configure-startup "${mode#--}" "$@"
