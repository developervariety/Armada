#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PUBLISH_DIR="${HOME}/.armada/bin"
SERVER_EXE="${PUBLISH_DIR}/Armada.Server"

echo
echo "[publish-server] Publishing Armada.Server to ${PUBLISH_DIR}..."
dotnet publish "${REPO_ROOT}/src/Armada.Server" -c Release -f net10.0 -o "${PUBLISH_DIR}"

echo
echo "[publish-server] Deploying dashboard assets..."
if ! "${SCRIPT_DIR}/deploy-dashboard.sh"; then
    echo "[publish-server] WARNING: Dashboard deploy failed. Armada will fall back to the embedded dashboard if available."
fi

if [ ! -f "${SERVER_EXE}" ]; then
    echo "ERROR: Published server executable not found at ${SERVER_EXE}" >&2
    exit 1
fi

echo
echo "[publish-server] Completed."
echo "[publish-server] Server executable: ${SERVER_EXE}"
