#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${1:-${ARMADA_BASE_URL:-http://localhost:7890}}"
BASE_URL="${BASE_URL%/}"
HEALTH_URL="${BASE_URL}/api/v1/status/health"

if ! command -v curl >/dev/null 2>&1; then
    echo "ERROR: curl is required for health checks." >&2
    exit 1
fi

echo
echo "[healthcheck-server] Waiting for ${HEALTH_URL}..."

for _ in $(seq 1 30); do
    if curl -fsS "${HEALTH_URL}" >/dev/null 2>&1; then
        echo "[healthcheck-server] Healthy."
        exit 0
    fi

    sleep 1
done

echo "ERROR: Armada server did not become healthy at ${HEALTH_URL}" >&2
exit 1
