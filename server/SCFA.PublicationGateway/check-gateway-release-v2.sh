#!/usr/bin/env bash
set -euo pipefail
umask 077
script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
mode=check
if [[ "${1:-}" == "--report-only" ]]; then
    mode=report
    shift
fi
exec python3 "$script_dir/ops/gateway_updater.py" "$mode" "$@"
