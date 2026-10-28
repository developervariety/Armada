#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
# shellcheck source=resolve-framework.sh
. "${SCRIPT_DIR}/resolve-framework.sh"
armada_resolve_framework "$@"
shift "${ARMADA_FRAMEWORK_ARGS_CONSUMED}"
PUBLISH_DIR="${HOME}/.armada/bin"
SERVER_EXE="${PUBLISH_DIR}/Armada.Server"

echo
echo "[publish-server] Publishing Armada.Server (${ARMADA_TARGET_FRAMEWORK}) to ${PUBLISH_DIR}..."
# The resolver exports one space-separated argument list. Read it into an array and expand each word
# quoted, so the project path and the output path stay single arguments. Options the resolver did not
# consume stay on the dashboard deploy below.
read -r -a armada_dotnet_framework_args <<< "${ARMADA_DOTNET_FRAMEWORK_ARGS}"
dotnet publish "${REPO_ROOT}/src/Armada.Server" -c Release "${armada_dotnet_framework_args[@]}" -o "${PUBLISH_DIR}"

echo
echo "[publish-server] Deploying dashboard assets..."
if ! "${SCRIPT_DIR}/deploy-dashboard.sh" "$@"; then
    if [ -f "${HOME}/.armada/dashboard/index.html" ]; then
        echo "[publish-server] WARNING: Dashboard deploy failed. Keeping the previously deployed React dashboard."
    else
        echo "ERROR: Dashboard deploy failed and no deployed React dashboard is available." >&2
        exit 1
    fi
fi

if [ ! -f "${SERVER_EXE}" ]; then
    echo "ERROR: Published server executable not found at ${SERVER_EXE}" >&2
    exit 1
fi

echo
echo "[publish-server] Completed."
echo "[publish-server] Server executable: ${SERVER_EXE}"
